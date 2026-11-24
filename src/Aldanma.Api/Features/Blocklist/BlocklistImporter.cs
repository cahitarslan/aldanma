using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aldanma.Api.Features.Blocklist;

public sealed record ImportResult(int Total, int Added, int Removed, int Skipped);

/// <summary>What the next import of a source should be: the whole list, or only what was added since a day.</summary>
public sealed record SyncPlan(bool Full, DateOnly? Since);

/// <summary>
/// Imports a block list (domains, URLs or IPs; only domains and URLs are kept) as public identifiers.
/// Lists can hold hundreds of thousands of lines, so rows go through PostgreSQL COPY into a temporary
/// table and are merged with set-based SQL. A full import marks entries missing from the new list as removed
/// (not deleted); a refresh only adds what is new.
/// </summary>
public sealed class BlocklistImporter(AppDbContext db, InputParser parser, TimeProvider time, ILogger<BlocklistImporter> log)
{
    /// <summary>Overlap for refreshes: entries dated up to this long before the last import are fetched again.</summary>
    public static readonly TimeSpan RefreshOverlap = TimeSpan.FromDays(2);

    /// <summary>
    /// Decides from the stored list alone, so a restart or redeploy never downloads the whole list again.
    /// Every import stamps the entries it saw with LastSeenAt, so the newest stamp is the last import and,
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

    /// <summary>Imports a whole plain-text list: one entry per line, blank lines and # comments ignored.</summary>
    public Task<ImportResult> ImportAsync(string source, string content, CancellationToken ct) =>
        ImportAsync(source, content.Split('\n'), full: true, ct);

    /// <param name="full">
    /// True: <paramref name="entries"/> is the whole list; entries missing from it are marked removed.
    /// False: a refresh with only new entries; nothing is removed and an empty refresh is fine.
    /// </param>
    public async Task<ImportResult> ImportAsync(string source, IEnumerable<string> entries, bool full, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var wanted = new HashSet<(string Kind, string Value)>();
        var skipped = 0;
        foreach (var raw in entries)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (parser.ParseSingle(line) is not { Kind: IdentifierKind.Domain or IdentifierKind.Url } id) { skipped++; continue; }
            wanted.Add((id.Kind, id.Value));
        }

        if (full)
        {
            // Nothing usable means the source changed format (an HTML page instead of a list): fail loudly instead of
            // reporting a successful import of zero entries, which is how the moved USOM list went unnoticed.
            if (wanted.Count == 0)
                throw new InvalidOperationException($"Block list '{source}' has no usable entries ({skipped} lines skipped); refusing to import.");

            // A suspiciously small download (an outage page, an empty file) must not wipe the list.
            var activeCount = await db.BlocklistEntries.CountAsync(b => b.Source == source && b.RemovedAt == null, ct);
            if (activeCount > 1000 && wanted.Count < activeCount / 2)
                throw new InvalidOperationException($"Block list '{source}' shrank from {activeCount} to {wanted.Count}; refusing to import.");
        }
        else if (wanted.Count == 0)
        {
            log.LogInformation("Block list {Source} refresh: nothing new", source);
            return new ImportResult(0, 0, 0, skipped);
        }

        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = conn.State != System.Data.ConnectionState.Open;
        if (opened) await conn.OpenAsync(ct);
        try
        {
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using (var create = new NpgsqlCommand("CREATE TEMP TABLE incoming (kind text NOT NULL, value text NOT NULL) ON COMMIT DROP", conn, tx))
                await create.ExecuteNonQueryAsync(ct);

            await using (var writer = await conn.BeginBinaryImportAsync("COPY incoming (kind, value) FROM STDIN (FORMAT BINARY)", ct))
            {
                foreach (var (kind, value) in wanted)
                {
                    await writer.StartRowAsync(ct);
                    await writer.WriteAsync(kind, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(value, NpgsqlDbType.Text, ct);
                }
                await writer.CompleteAsync(ct);
            }

            var added = 0;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO blocklist_entries ("Source", "Kind", "Value", "FirstSeenAt", "LastSeenAt", "RemovedAt")
                SELECT @source, kind, value, @now, @now, NULL FROM incoming
                ON CONFLICT ("Source", "Kind", "Value") DO UPDATE SET "LastSeenAt" = @now, "RemovedAt" = NULL
                RETURNING (xmax = 0)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("source", source);
                insert.Parameters.AddWithValue("now", now);
                await using var reader = await insert.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) if (reader.GetBoolean(0)) added++;
            }

            var removed = 0;
            if (full)
            {
                await using var remove = new NpgsqlCommand("""
                    UPDATE blocklist_entries SET "RemovedAt" = @now
                    WHERE "Source" = @source AND "RemovedAt" IS NULL AND "LastSeenAt" < @now
                    """, conn, tx);
                remove.Parameters.AddWithValue("source", source);
                remove.Parameters.AddWithValue("now", now);
                removed = await remove.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            log.LogInformation("Imported block list {Source} ({Mode}): {Total} entries, {Added} new, {Removed} removed, {Skipped} lines skipped",
                source, full ? "full" : "refresh", wanted.Count, added, removed, skipped);
            return new ImportResult(wanted.Count, added, removed, skipped);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
