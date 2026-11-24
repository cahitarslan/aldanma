namespace Aldanma.Api.Features.Submissions;

public sealed record SubmissionRequest(string? Text, string? Sender, string? Claim, string? Level, string? RulesVersion, bool Consent, string? HumanToken);

public sealed record ResolveSubmissionRequest(bool Accept, string? Label);

/// <summary>
/// The review queue. A user may send the message for review after answering "Was this right?": a wrong result
/// ("missed", "false-alarm") or a right one ("confirmed", which gives labelled examples for the corpus). It is the only
/// place message text is stored, only with explicit consent, and with phone numbers and IBANs masked first.
/// Rules change only after a person reviews the queue (see tools/suggest-rules.mjs); nothing is learned automatically.
/// </summary>
public sealed class SubmissionService(AppDbContext db, AldanmaMetrics metrics, TimeProvider time, ILogger<SubmissionService> log)
{
    public const int MinTextLength = 20;
    public const int MaxTextLength = 5000;
    public const string Missed = "missed";
    public const string FalseAlarm = "false-alarm";
    public const string Confirmed = "confirmed";
    public static readonly IReadOnlySet<string> Claims = new HashSet<string> { Missed, FalseAlarm, Confirmed };
    public static readonly IReadOnlySet<string> Labels = new HashSet<string> { "scam", "legit" };

    private static readonly HashSet<string> Levels = [RiskLevel.High, RiskLevel.Suspicious, RiskLevel.NoSigns];

    public async Task<string?> SubmitAsync(SubmissionRequest request, CancellationToken ct)
    {
        if (!request.Consent) return "Göndermek için onay kutusunu işaretlemelisin.";
        var text = request.Text?.Trim() ?? "";
        if (text.Length is < MinTextLength or > MaxTextLength) return $"Mesaj {MinTextLength} ile {MaxTextLength} karakter arasında olmalı.";
        var sender = string.IsNullOrEmpty(request.Sender) ? Sender.Unknown : request.Sender;
        if (!Sender.All.Contains(sender)) return "Geçersiz gönderen türü.";
        if (request.Claim is not { } claim || !Claims.Contains(claim)) return "Geçersiz bildirim türü.";
        if (request.Level is not { } level || !Levels.Contains(level)) return "Geçersiz sonuç seviyesi.";
        if (request.RulesVersion is not { Length: > 0 and <= 20 } version) return "Geçersiz kural sürümü.";

        var submission = new Submission
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = time.GetUtcNow(),
            Text = InputParser.MaskIdentifiers(text),
            Sender = sender,
            Claim = claim,
            Level = level,
            RulesVersion = version,
        };
        db.Submissions.Add(submission);
        await db.SaveChangesAsync(ct);
        metrics.SubmissionReceived(claim);
        log.LogInformation("Submission {SubmissionId} queued for review: {Claim}", submission.Id, claim);
        return null;
    }

    public static bool IsValidResolution(ResolveSubmissionRequest request) =>
        !request.Accept || (request.Label is not null && Labels.Contains(request.Label));

    /// <returns>False when no submission has this id.</returns>
    public async Task<bool> ResolveAsync(Guid id, ResolveSubmissionRequest request, CancellationToken ct)
    {
        var submission = await db.Submissions.FindAsync([id], ct);
        if (submission is null) return false;
        submission.Status = request.Accept ? SubmissionStatus.Accepted : SubmissionStatus.Rejected;
        submission.Label = request.Accept ? request.Label : null;
        await db.SaveChangesAsync(ct);
        log.LogInformation("Admin resolved submission {SubmissionId}: {Status} {Label}", id, submission.Status, submission.Label);
        return true;
    }
}
