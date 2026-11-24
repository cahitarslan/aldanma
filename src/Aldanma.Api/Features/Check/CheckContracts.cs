
namespace Aldanma.Api.Features.Check;

public sealed record CheckRequest(string? Text, string? Sender);

public sealed record LookupRequest(string[]? Items);

public sealed record OptionDto(string Id, string Name, int Count);

public sealed record ReportsDto(
    int ReporterCount,
    int ReportCount,
    string? FirstReportedMonth,
    string? LastReportedOn,
    IReadOnlyList<OptionDto> Categories,
    IReadOnlyList<OptionDto> Channels,
    int CoReportedCount,
    bool Disputed);

public sealed record IdentifierDto(
    string Kind,
    string Value,
    string Display,
    int Score,
    string Level,
    IReadOnlyList<Signal> Signals,
    IReadOnlyDictionary<string, string> Details,
    ReportsDto Reports,
    bool Official,
    bool Reportable);

public sealed record CheckResponse(string Mode, int Score, string Level, MessageAnalysis? Message, IReadOnlyList<IdentifierDto> Identifiers, string RulesVersion);

public sealed record LookupResponse(IReadOnlyList<IdentifierDto> Identifiers, IReadOnlyList<string> Unrecognised);
