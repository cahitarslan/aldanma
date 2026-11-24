
namespace Aldanma.Api.Features.Check;

/// <summary>Collects every fact about an identifier (reports, block lists, domain age) and scores it.</summary>
public sealed class ReputationService(IdentifierStore store, IDomainRegistrationLookup registrations, RiskScorer scorer, AldanmaMetrics metrics, TimeProvider time)
{
    public async Task<IdentifierAssessment> AssessAsync(Identifier id, CancellationToken ct)
    {
        var record = await store.FindAsync(id, ct);
        var summary = await store.SummaryAsync(record, ct);
        var lists = await store.BlocklistsAsync(id, ct);
        DomainRegistration? registration = null;
        if (id.Kind == IdentifierKind.Domain && !IsIpAddress(id.Value))
            registration = await registrations.GetAsync(id.Value, ct);
        var assessment = scorer.Assess(id, new IdentifierFacts(summary, lists, registration), time.GetUtcNow());
        metrics.Lookup(id.Kind, assessment.Level);
        return assessment;
    }

    private static bool IsIpAddress(string value) => System.Net.IPAddress.TryParse(value, out _);
}
