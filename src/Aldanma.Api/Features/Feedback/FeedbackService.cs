using System.Text.RegularExpressions;

namespace Aldanma.Api.Features.Feedback;

public sealed record FeedbackRequest(bool? Correct, string? Level, int? Score, string? RulesVersion, string[]? Kinds, string[]? Categories, string[]? Signals);

/// <summary>Stores "was this result right?" answers as labelled data. Accepts only rule ids, never text.</summary>
public sealed partial class FeedbackService(AppDbContext db, AldanmaMetrics metrics, TimeProvider time)
{
    public const int MaxIds = 40;

    // Rule ids look like "fake-enforcement", "intent:money-request" or "domain-new"; anything else is rejected,
    // so free text cannot be smuggled into the labelled data.
    [GeneratedRegex("^[a-z0-9][a-z0-9:_-]{0,63}$")]
    private static partial Regex RuleIdRx();

    private static readonly HashSet<string> Levels = [RiskLevel.High, RiskLevel.Suspicious, RiskLevel.NoSigns];

    public async Task<string?> SubmitAsync(FeedbackRequest request, CancellationToken ct)
    {
        if (request.Correct is not { } correct) return "Cevap eksik.";
        if (request.Level is not { } level || !Levels.Contains(level)) return "Geçersiz sonuç seviyesi.";
        if (request.Score is not (>= 0 and <= 100)) return "Geçersiz puan.";
        if (request.RulesVersion is not { Length: > 0 and <= 20 } version) return "Geçersiz kural sürümü.";
        if (!ValidIds(request.Kinds) || !ValidIds(request.Categories) || !ValidIds(request.Signals)) return "Geçersiz kural adı.";

        db.Feedback.Add(new FeedbackEntry
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = time.GetUtcNow(),
            Correct = correct,
            Level = level,
            Score = request.Score.Value,
            RulesVersion = version,
            Kinds = [.. (request.Kinds ?? []).Distinct()],
            Categories = [.. (request.Categories ?? []).Distinct()],
            Signals = [.. (request.Signals ?? []).Distinct()],
        });
        await db.SaveChangesAsync(ct);
        metrics.FeedbackReceived(level, correct);
        return null;
    }

    private static bool ValidIds(string[]? ids) => ids is null || (ids.Length <= MaxIds && ids.All(id => id is not null && RuleIdRx().IsMatch(id)));
}
