using System.Globalization;

namespace Aldanma.Api.Features.Check;

/// <summary>Scores what the user pasted. Nothing sent to these endpoints is stored or logged.</summary>
public static class CheckEndpoints
{
    public const int MaxTextLength = 5000;
    public const int MaxLookupItems = 10;

    public static void MapCheckEndpoints(this IEndpointRouteBuilder api)
    {
        // Analyses a whole message or a single identifier on the server (for API users).
        api.MapPost("/check", async (CheckRequest? req, InputParser parser, MessageEngine engine, ReputationService reputation, CancellationToken ct) =>
        {
            if (req is null || string.IsNullOrWhiteSpace(req.Text)) return Results.BadRequest(new ErrorResponse("Kontrol edilecek bir şey yazmadın."));
            if (req.Text.Length > MaxTextLength) return Results.BadRequest(new ErrorResponse($"En fazla {MaxTextLength} karakter yapıştırabilirsin."));
            var sender = string.IsNullOrEmpty(req.Sender) ? Sender.Unknown : req.Sender;
            if (!Sender.All.Contains(sender)) return Results.BadRequest(new ErrorResponse("Geçersiz gönderen türü."));

            var parsed = parser.Parse(req.Text);
            var message = parsed.Mode == InputMode.Text ? engine.Analyze(req.Text, sender) : null;
            var ids = new List<IdentifierDto>();
            foreach (var id in parsed.Identifiers) ids.Add(ToDto(await reputation.AssessAsync(id, ct), parser.Rules));
            var score = Math.Max(message?.Score ?? 0, ids.Count == 0 ? 0 : ids.Max(i => i.Score));
            return Results.Ok(new CheckResponse(parsed.Mode, score, engine.Rules.LevelOf(score), message, ids, engine.RulesVersion));
        }).RequireRateLimiting(RateLimits.Check).WithRequestTimeout(RequestTimeouts.Lookup);

        // Looks up identifiers the browser already extracted, so a pasted message never has to leave the device.
        api.MapPost("/lookup", async (LookupRequest? req, InputParser parser, ReputationService reputation, CancellationToken ct) =>
        {
            var items = req?.Items ?? [];
            if (items.Length == 0) return Results.BadRequest(new ErrorResponse("Aranacak numara, IBAN ya da adres yok."));
            if (items.Length > MaxLookupItems) return Results.BadRequest(new ErrorResponse($"Tek seferde en fazla {MaxLookupItems} kayıt aranabilir."));

            var seen = new HashSet<Identifier>();
            var ids = new List<IdentifierDto>();
            var unrecognised = new List<string>();
            foreach (var item in items)
            {
                var id = parser.ParseSingle(item);
                if (id is null) { unrecognised.Add(item ?? ""); continue; }
                if (seen.Add(id)) ids.Add(ToDto(await reputation.AssessAsync(id, ct), parser.Rules));
            }
            return Results.Ok(new LookupResponse(ids, unrecognised));
        }).RequireRateLimiting(RateLimits.Check).WithRequestTimeout(RequestTimeouts.Lookup);
    }

    internal static IdentifierDto ToDto(IdentifierAssessment a, IdentifierRules rules)
    {
        var r = a.Reports;
        var reports = new ReportsDto(
            r.ReporterCount,
            r.ReportCount,
            r.FirstReportedAt?.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            r.LastReportedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            Options(r.Categories, rules.ReportCategories),
            Options(r.Channels, rules.ReportChannels),
            r.CoReportedCount,
            r.Disputed);
        return new IdentifierDto(a.Identifier.Kind, a.Identifier.Value, a.Identifier.Display, a.Score, a.Level, a.Signals, a.Details, reports, a.Official, a.Reportable);
    }

    private static OptionDto[] Options(IReadOnlyDictionary<string, int> counts, IReadOnlyDictionary<string, string> names) =>
        [.. counts.OrderByDescending(x => x.Value).Select(x => new OptionDto(x.Key, names.GetValueOrDefault(x.Key, x.Key), x.Value))];
}
