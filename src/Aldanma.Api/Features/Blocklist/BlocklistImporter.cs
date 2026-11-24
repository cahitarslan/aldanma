using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aldanma.Api.Features.Blocklist;

public sealed record ImportResult(int Total, int Added, int Removed, int Skipped);

/// <summary>What the next import of a source should be: the whole list, or only what was added since a day.</summary>
public sealed record SyncPlan(bool Full, DateOnly? Since);

/// <summary>
/// Stores a block list (domains, URLs or IPs; only domains and URLs are kept) as public identifiers.
/// <para>
/// A run writes its entries as they arrive (<see cref="UpsertAsync"/>), each batch in its own short transaction,
/// stamping every entry it sees with the run's start time. Only when a full run has read the whole list does
/// <see cref="RemoveUnseenAsync"/> mark entries the source dropped (older stamp) as removed. So a run that stops
/// halfway keeps what it saved and can resume, and a half-read list never removes anything.
/// </para>
/// Lists can hold hundreds of thousands of lines, so rows go through PostgreSQL COPY into a temporary table and are
/// merged with set-based SQL.
/// </summary>
public sealed class BlocklistImporter(AppDbContext db, InputParser parser, TimeProvider time, ILogger<BlocklistImporter> log)
{
    /// <summary>Overlap for refreshes: entries dated up to this long before the last import are fetched again.</summary>
    public static readonly TimeSpan RefreshOverlap = TimeSpan.FromDays(2);

    /// <summary>
    /// A whole list is a few hundred thousand rows. The connection string keeps a short command timeout to protect
    /// requests (10 s in production), which a full merge can exceed, so the import's own commands get a longer one.
    /// </summary>
    public static readonly TimeSpan ImportCommandTimeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Decides from the stored list alone, so a restart or redeploy never downloads the whole list again.
    /// Every run stamps the entries it saw with LastSeenAt, so the newest stamp is the last import and,
    /// because a refresh only touches new entries, the oldest stamp among active entries is the last full import.
    /// </summary>
    public async Task<SyncPlan> PlanAsync(string source, TimeSpan fullEvery, CancellationToken ct)
    {
        var active = db.BlocklistEntries.Where(b => b.Source == source && b.RemovedAt == null);
        var oldest = await active.MinAsync(b => (DateTimeOffset?)b.LastSeenAt, ct);
        if (oldest is null || time.GetUtcNow() - oldest >= fullEvery) return new SyncPlan(true, null);
        var newest = await active.MaxAsync(b => b.LastSeenAt, ct);
        return new SyncPlan(false, DateOnly.FromDateTime((newest - RefreshOverlap).UtcDateTime));
    }

    /// <summary>Imports a whole plain-text list in one go: one entry per line, blank lines and # comments ignored.</summary>
    public Task<ImportResult> ImportAsync(string source, string content, CancellationToken ct) =>
        ImportAsync(source, content.Split('\n'), full: true, ct);

    /// <summary>Imports a list in one go. <paramref name="full"/>: the entries are the whole list, so missing ones are removed.</summary>
    public async Task<ImportResult> ImportAsync(string source, IEnumerable<string> entries, bool full, CancellationToken ct)
    {
        var stamp = time.GetUtcNow();
        var batch = await UpsertAsync(source, entries, stamp, ct);
        // Nothing usable in a whole list means the source changed format (an HTML page instead of a list).
        if (full && batch.Total == 0)
            throw new InvalidOperationException($"Block list '{source}' has no usable entries ({batch.Skipped} lines skipped); refusing to import.");
        var removed = full ? await RemoveUnseenAsync(source, stamp, ct) : 0;
        return batch with { Removed = removed };
    }

    /// <summary>
    /// Adds or refreshes one batch (a page of the source) in its own transaction. Entries get <paramref name="stamp"/>
    /// as LastSeenAt unless they already carry a newer one, and lose any removal mark.
    /// </summary>
    public async Task<ImportResult> UpsertAsync(string source, IEnumerable<string> entries, DateTimeOffset stamp, CancellationToken ct)
    {
        var wanted = new HashSet<(string Kind, string Value)>();
        var skipped = 0;
        foreach (var raw in entries)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (parser.ParseSingle(line) is not { Kind: IdentifierKind.Domain or IdentifierKind.Url } id) { skipped++; continue; }
            wanted.Add((id.Kind, id.Value));
        }
        if (wanted.Count == 0) return new ImportResult(0, 0, 0, skipped);

        return await WithConnectionAsync(async (conn, tx) =>
        {
            await using (var create = new NpgsqlCommand("CREATE TEMP TABLE incoming (kind text NOT NULL, value text NOT NULL) ON COMMIT DROP", conn, tx))
                await create.ExecuteNonQueryAsync(ct);

            await using (var writer = await conn.BeginBinaryImportAsync("COPY incoming (kind, value) FROM STDIN (FORMAT BINARY)", ct))
            {
                writer.Timeout = ImportCommandTimeout;
                foreach (var (kind, value) in wanted)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(kind, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
                }
                await writer.CompleteAsync(ct);
            }

            var added = 0;
            await using (var insert = Command("""
                INSERT INTO blocklist_entries ("Source", "Kind", "Value", "FirstSeenAt", "LastSeenAt", "RemovedAt")
                SELECT @source, kind, value, @stamp, @stamp, NULL FROM incoming
                ON CONFLICT ("Source", "Kind", "Value") DO UPDATE
                    SET "LastSeenAt" = GREATEST(blocklist_entries."LastSeenAt", EXCLUDED."LastSeenAt"), "RemovedAt" = NULL
                RETURNING (xmax = 0)
                """, conn, tx, source, stamp))
            {
                await using var reader = await insert.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) if (reader.GetBoolean(0)) added++;
            }
            return new ImportResult(wanted.Count, added, 0, skipped);
        }, ct);
    }

    /// <summary>
    /// After a complete full run started at <paramref name="runStartedAt"/>: marks every entry the run did not see as
    /// removed. Refuses when the run saw less than half of the active list (an outage page, a truncated download).
    /// </summary>
    public async Task<int> RemoveUnseenAsync(string source, DateTimeOffset runStartedAt, CancellationToken ct)
    {
        var active = db.BlocklistEntries.Where(b => b.Source == source && b.RemovedAt == null);
        var activeCount = await active.CountAsync(ct);
        var seen = await active.CountAsync(b => b.LastSeenAt >= runStartedAt, ct);
        if (seen == 0)
            throw new InvalidOperationException($"Block list '{source}': the full run saw no usable entries; nothing removed.");
        if (activeCount > 1000 && seen < activeCount / 2)
            throw new InvalidOperationException($"Block list '{source}' shrank from {activeCount} to {seen}; refusing to remove the rest.");

        var removed = await WithConnectionAsync(async (conn, tx) =>
        {
            await using var remove = Command("""
                UPDATE blocklist_entries SET "RemovedAt" = now()
                WHERE "Source" = @source AND "RemovedAt" IS NULL AND "LastSeenAt" < @stamp
                """, conn, tx, source, runStartedAt);
            return await remove.ExecuteNonQueryAsync(ct);
        }, ct);
        log.LogInformation("Block list {Source} full run complete: {Seen} entries seen, {Removed} removed", source, seen, removed);
        return removed;
    }

    private static NpgsqlCommand Command(string sql, NpgsqlConnection conn, NpgsqlTransaction tx, string source, DateTimeOffset stamp)
    {
        var cmd = new NpgsqlCommand(sql, conn, tx) { CommandTimeout = (int)ImportCommandTimeout.TotalSeconds };
        cmd.Parameters.AddWithValue("source", source);
        cmd.Parameters.AddWithValue("stamp", stamp);
        return cmd;
    }

    private async Task<T> WithConnectionAsync<T>(Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> work, CancellationToken ct)
    {
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            var result = await work(conn, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
