using System.Net.Mail;

namespace Aldanma.Api.Features.Disputes;

public sealed record DisputeRequest(string? Input, string? Contact, string? Reason, string? HumanToken);

/// <summary>Accepts objections from people who say a reported number, IBAN or site is theirs.</summary>
public sealed class DisputeService(AppDbContext db, IdentifierStore store, InputParser parser, AldanmaMetrics metrics, TimeProvider time, ILogger<DisputeService> log)
{
    public async Task<(bool Ok, string? Error)> SubmitAsync(DisputeRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Input)) return (false, "Numara, IBAN ya da adres gerekli.");
        var contact = request.Contact?.Trim() ?? "";
        if (contact.Length is < 5 or > 200 || !MailAddress.TryCreate(contact, out _)) return (false, "Sana dönebilmemiz için geçerli bir e-posta adresi yaz.");
        var reason = request.Reason?.Trim() ?? "";
        if (reason.Length is < 20 or > 1000) return (false, "Açıklama 20 ile 1000 karakter arasında olmalı.");

        if (parser.ParseSingle(request.Input.Trim()) is not { } identifier) return (false, "Tek bir numara, IBAN ya da adres yaz.");
        var record = await store.FindAsync(identifier, ct);
        if (record is null) return (false, "Bu kayıt için ihbar bulunmuyor.");

        var dispute = new Dispute
        {
            Id = Guid.CreateVersion7(),
            IdentifierId = record.Id,
            CreatedAt = time.GetUtcNow(),
            Contact = contact,
            Reason = reason,
        };
        db.Disputes.Add(dispute);
        await db.SaveChangesAsync(ct);
        metrics.DisputeReceived();
        log.LogInformation("Dispute {DisputeId} received for identifier {IdentifierId}", dispute.Id, record.Id);
        return (true, null);
    }
}
