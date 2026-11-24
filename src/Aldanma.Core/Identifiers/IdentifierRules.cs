using System.Text.Json;

namespace Aldanma.Core.Identifiers;

/// <summary>The "identifiers" section of rules/rules.json.</summary>
public sealed class IdentifierRules
{
    public sealed record SignalDefinition(string Name, int Weight, string Description);

    public sealed record ReportWeight(int MinReporters, int Weight);

    public IReadOnlyDictionary<string, SignalDefinition> Signals { get; private init; } = new Dictionary<string, SignalDefinition>();
    /// <summary>Ascending by <see cref="ReportWeight.MinReporters"/>.</summary>
    public IReadOnlyList<ReportWeight> ReportWeights { get; private init; } = [];
    public int NewDomainDays { get; private init; }
    public int YoungDomainDays { get; private init; }
    public int EstablishedDomainYears { get; private init; }
    public IReadOnlyList<string> HostingSuffixes { get; private init; } = [];
    public IReadOnlyList<string> PathHosts { get; private init; } = [];
    public IReadOnlyList<string> Shorteners { get; private init; } = [];
    public IReadOnlyDictionary<string, string> Banks { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, int> IbanLengths { get; private init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, string> Countries { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> IbanCountries { get; private init; } = new Dictionary<string, string>();
    /// <summary>Well-known official short lines (112, 160...) and their names.</summary>
    public IReadOnlyDictionary<string, string> EmergencyLines { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ReportCategories { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> ReportChannels { get; private init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> LossBands { get; private init; } = new Dictionary<string, string>();

    public static IdentifierRules Embedded()
    {
        using var stream = typeof(IdentifierRules).Assembly.GetManifestResourceStream("Aldanma.rules.json")
            ?? throw new InvalidOperationException("Embedded rule set not found.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    public static IdentifierRules Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var r = root.GetProperty("identifiers");
        var age = r.GetProperty("domainAge");
        return new IdentifierRules
        {
            Signals = r.GetProperty("signals").EnumerateObject().ToDictionary(
                p => p.Name,
                p => new SignalDefinition(p.Value.GetProperty("name").GetString()!, p.Value.GetProperty("weight").GetInt32(), p.Value.GetProperty("description").GetString()!),
                StringComparer.Ordinal),
            ReportWeights = [.. r.GetProperty("reportWeights").EnumerateArray()
                .Select(e => new ReportWeight(e.GetProperty("min").GetInt32(), e.GetProperty("weight").GetInt32()))
                .OrderBy(w => w.MinReporters)],
            NewDomainDays = age.GetProperty("newDays").GetInt32(),
            YoungDomainDays = age.GetProperty("youngDays").GetInt32(),
            EstablishedDomainYears = age.GetProperty("establishedYears").GetInt32(),
            HostingSuffixes = Strings(r, "hostingSuffixes"),
            PathHosts = Strings(r, "pathHosts"),
            Shorteners = Strings(root.GetProperty("domains"), "shorteners"),
            Banks = Map(r, "banks"),
            IbanLengths = r.GetProperty("ibanLengths").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal),
            Countries = Map(r, "countries"),
            IbanCountries = Map(r, "ibanCountries"),
            EmergencyLines = root.GetProperty("general").GetProperty("emergencyLines").EnumerateArray()
                .ToDictionary(e => e.GetProperty("number").GetString()!, e => e.GetProperty("name").GetString()!, StringComparer.Ordinal),
            ReportCategories = Options(r, "reportCategories"),
            ReportChannels = Options(r, "reportChannels"),
            LossBands = Options(r, "lossBands"),
        };
    }

    private static string[] Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!)];

    private static Dictionary<string, string> Options(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!, x => x.GetProperty("name").GetString()!, StringComparer.Ordinal);

    private static Dictionary<string, string> Map(JsonElement e, string name) =>
        e.GetProperty(name).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
}
