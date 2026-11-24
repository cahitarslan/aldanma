using System.Net.Mail;

namespace Aldanma.Api.Features.Disputes;

public sealed record DisputeRequest(string? Input, string? Contact, string? Reason, string? HumanToken);

/// <summary>Accepts objections from people who say a reported number, IBAN or site is theirs.</summary>
public sealed class DisputeService(AppDbContext db, IdentifierStore store, InputParser parser, AldanmaMetrics metrics, TimeProvider time, ILogger<DisputeService> log)
{
    public const int MinReasonLength = 20;
    public const int MaxReasonLength = 1000;

    /// <summary>
    /// Every check that does not need the human check. The endpoint runs it first, so a form with a mistake in it
    /// does not use up the single-use Turnstile token.
    /// </summary>
    public async Task<string?> ValidateAsync(DisputeRequest request, CancellationToken ct) => (await CheckAsync(request, ct)).Error;

    public async Task<(bool Ok, string? Error)> SubmitAsync(DisputeRequest request, CancellationToken ct)
    {
        var (record, error) = await CheckAsync(request, ct);
        if (record is null) return (false, error);

        var dispute = new Dispute
        {
            Id = Guid.CreateVersion7(),
            IdentifierId = record.Id,
            CreatedAt = time.GetUtcNow(),
            Contact = request.Contact!.Trim(),
            Reason = request.Reason!.Trim(),
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync(ct);
        metrics.DisputeReceived();
        log.LogInformation("Dispute {DisputeId} received for identifier {IdentifierId}", dispute.Id, record.Id);
        return (true, null);
    }

    private async Task<(IdentifierRecord? Record, string? Error)> CheckAsync(DisputeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Input)) return (null, "Numara, IBAN ya da adres gerekli.");
        var contact = request.Contact?.Trim() ?? "";
        if (contact.Length is < 5 or > 200 || !MailAddress.TryCreate(contact, out _)) return (null, "Sana dönebilmemiz için geçerli bir e-posta adresi yaz.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < MinReasonLength or > MaxReasonLength) return (null, $"Açıklama {MinReasonLength} ile {MaxReasonLength} karakter arasında olmalı.");

        if (parser.ParseSingle(request.Input.Trim()) is not { } identifier) return (null, "Tek bir numara, IBAN ya da adres yaz.");
        var record = await store.FindAsync(identifier, ct);
        return record is null ? (null, "Bu kayıt için ihbar bulunmuyor.") : (record, null);
    }
}
