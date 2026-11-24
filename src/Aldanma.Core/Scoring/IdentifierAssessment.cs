using Aldanma.Core.Identifiers;
using Aldanma.Core.Messages;

namespace Aldanma.Core.Scoring;

/// <summary>Risk assessment of a single phone number, IBAN, domain or URL.</summary>
public sealed record IdentifierAssessment(
    Identifier Identifier,
    int Score,
    string Level,
    IReadOnlyList<Signal> Signals,
    IReadOnlyDictionary<string, string> Details,
    ReportSummary Reports,
    bool Official,
    bool Reportable);
