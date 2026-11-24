using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Aldanma.Api.Features.Blocklist;

public sealed record ImportResult(int Total, int Added, int Removed, int Skipped);

/// <summary>
/// Imports a plain-text block list (one domain, URL or IP per line) as public identifiers.
/// Lists can hold hundreds of thousands of lines, so rows go through PostgreSQL COPY into a temporary
/// table and are merged with set-based SQL. Entries missing from a new download are marked removed, not deleted.
/// </summary>
public sealed class BlocklistImporter(AppDbContext db, InputParser parser, TimeProvider time, ILogger<BlocklistImporter> log)
{
    public async Task<ImportResult> ImportAsync(string source, string content, CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var wanted = new HashSet<(string Kind, string Value)>();
        var skipped = 0;
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (parser.ParseSingle(line) is not { Kind: IdentifierKind.Domain or IdentifierKind.Url } id) { skipped++; continue; }
            wanted.Add((id.Kind, id.Value));
        }

        // A suspiciously small download (an outage page, an empty file) must not wipe the list.
        var activeCount = await db.BlocklistEntries.CountAsync(b => b.Source == source && b.RemovedAt == null, ct);
        if (activeCount > 1000 && wanted.Count < activeCount / 2)
            throw new InvalidOperationException($"Block list '{source}' shrank from {activeCount} to {wanted.Count}; refusing to import.");

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

            int added, removed;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO blocklist_entries ("Source", "Kind", "Value", "FirstSeenAt", "LastSeenAt", "RemovedAt")
                SELECT @source, kind, value, @now, @now, NULL FROM incoming
                ON CONFLICT ("Source", "Kind", "Value") DO UPDATE SET "LastSeenAt" = @now, "RemovedAt" = NULL
                RETURNING (xmax = 0)
                """, conn, tx))
            {
                insert.Parameters.AddWithValue("source", source);
                insert.Parameters.AddWithValue("now", now);
                added = 0;
                await using var reader = await insert.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct)) if (reader.GetBoolean(0)) added++;
            }
            await using (var remove = new NpgsqlCommand("""
                UPDATE blocklist_entries SET "RemovedAt" = @now
                WHERE "Source" = @source AND "RemovedAt" IS NULL AND "LastSeenAt" < @now
                """, conn, tx))
            {
                remove.Parameters.AddWithValue("source", source);
                remove.Parameters.AddWithValue("now", now);
                removed = await remove.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);

            log.LogInformation("Imported block list {Source}: {Total} entries, {Added} new, {Removed} removed, {Skipped} lines skipped",
                source, wanted.Count, added, removed, skipped);
            return new ImportResult(wanted.Count, added, removed, skipped);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }
}
