using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aldanma.Core.Messages;

/// <summary>In-memory form of rules/rules.json. Patterns are compiled once at load time.</summary>
public sealed class RuleSet
{
    internal sealed record Institution(string Id, string Name, Regex[] Keywords, string[] Domains, string[] Tokens, string Verification, bool Mismatch);

    internal sealed record Category(string Id, string Name, string[] Institutions, int Weight, Regex[] Strong, Regex[] Weak, string Description,
        string[] WhatToDo, string VerificationText, OfficialLink[] VerificationLinks, bool Institutional, bool RequiresAction, string[]? SenderCondition);

    internal sealed record LanguageSignal(string Id, string Name, int Weight, Regex[] Patterns, string Description);

    internal sealed record IntentClass(string Id, Regex[] Patterns);

    internal sealed record Combination(string Id, string Name, int Score, string[] All, string[] Any, string[] None, string Description);

    internal sealed record Definition(string Name, int Weight, string Description);

    public string Version { get; private init; } = "";
    public int CategoryCount => Categories.Length;
    public int InstitutionCount => Institutions.Length;
    public int HighThreshold { get; private init; }
    public int SuspiciousThreshold { get; private init; }

    /// <summary>Maps a 0-100 score to a risk level. The only place thresholds are applied.</summary>
    public string LevelOf(int score) =>
        score >= HighThreshold ? RiskLevel.High : score >= SuspiciousThreshold ? RiskLevel.Suspicious : RiskLevel.NoSigns;

    internal Institution[] Institutions { get; private init; } = [];
    internal Category[] Categories { get; private init; } = [];
    internal LanguageSignal[] LanguageSignals { get; private init; } = [];
    internal IntentClass[] IntentClasses { get; private init; } = [];
    internal Combination[] Combinations { get; private init; } = [];
    internal Definition DigitLetters { get; private init; } = null!;
    internal Regex DigitLettersPattern { get; private init; } = null!;
    internal Definition MixedAlphabet { get; private init; } = null!;
    internal Definition ForeignAccents { get; private init; } = null!;
    internal Definition Shouting { get; private init; } = null!;
    internal string[] SecondLevelSuffixes { get; private init; } = [];
    internal string[] PublicSuffixes { get; private init; } = [];
    internal string[] KnownDomains { get; private init; } = [];
    internal string[] Shorteners { get; private init; } = [];
    internal string[] ChannelDomains { get; private init; } = [];
    internal string[] TokenStopWords { get; private init; } = [];
    internal HashSet<string> SuspiciousTlds { get; private init; } = [];
    internal HashSet<string> BareTlds { get; private init; } = [];
    /// <summary>Link signals in file order (the order decides the output order).</summary>
    internal List<KeyValuePair<string, Definition>> LinkSignals { get; private init; } = [];
    internal Dictionary<string, Definition> SenderSignals { get; private init; } = [];
    internal string NotConclusive { get; private init; } = "";
    internal string NoSignsWarning { get; private init; } = "";
    internal string[] BasicRules { get; private init; } = [];
    internal string[] IfClicked { get; private init; } = [];

    // ECMAScript mode makes \w, \d, \s and \b behave like the JS engine (ASCII only).
    internal static Regex Compile(string pattern) => new(pattern, RegexOptions.ECMAScript | RegexOptions.Compiled, TimeSpan.FromSeconds(2));

    /// <summary>Loads the rule set embedded in this assembly.</summary>
    public static RuleSet Embedded()
    {
        using var stream = typeof(RuleSet).Assembly.GetManifestResourceStream("Aldanma.rules.json")
            ?? throw new InvalidOperationException("Embedded rule set not found.");
        using var reader = new StreamReader(stream);
        return Load(reader.ReadToEnd());
    }

    /// <summary>Loads a rule set from JSON text (e.g. a newer version read from disk).</summary>
    public static RuleSet Load(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var domains = r.GetProperty("domains");
        var writing = r.GetProperty("writing");
        var general = r.GetProperty("general");
        var suspicious = Strings(domains, "suspiciousTlds");
        var bare = new HashSet<string>(Strings(domains, "knownTlds"), StringComparer.Ordinal);
        bare.UnionWith(suspicious);
        var intent = r.TryGetProperty("intent", out var i) ? i : default;

        return new RuleSet
        {
            Version = Text(r, "version"),
            HighThreshold = r.GetProperty("thresholds").GetProperty("high").GetInt32(),
            SuspiciousThreshold = r.GetProperty("thresholds").GetProperty("suspicious").GetInt32(),
            Institutions = [.. r.GetProperty("institutions").EnumerateArray().Select(e => new Institution(
                Text(e, "id"), Text(e, "name"), Patterns(e, "keywords"), Strings(e, "domains"), Strings(e, "tokens"), Text(e, "verification"),
                !(e.TryGetProperty("mismatch", out var m) && m.ValueKind == JsonValueKind.False)))],
            Categories = [.. r.GetProperty("categories").EnumerateArray().Select(e => new Category(
                Text(e, "id"), Text(e, "name"), Strings(e, "institutions"), e.GetProperty("weight").GetInt32(), Patterns(e, "strong"), Patterns(e, "weak"),
                Text(e, "description"), Strings(e, "whatToDo"), Text(e.GetProperty("verification"), "text"),
                [.. e.GetProperty("verification").GetProperty("links").EnumerateArray().Select(b => new OfficialLink(Text(b, "name"), Text(b, "url")))],
                IsTrue(e, "institutional"), IsTrue(e, "requiresAction"),
                e.TryGetProperty("senderCondition", out _) ? Strings(e, "senderCondition") : null))],
            LanguageSignals = [.. r.GetProperty("languageSignals").EnumerateArray().Select(e => new LanguageSignal(
                Text(e, "id"), Text(e, "name"), e.GetProperty("weight").GetInt32(), Patterns(e, "patterns"), Text(e, "description")))],
            IntentClasses = intent.ValueKind == JsonValueKind.Object
                ? [.. intent.GetProperty("classes").EnumerateArray().Select(e => new IntentClass(Text(e, "id"), Patterns(e, "patterns")))]
                : [],
            Combinations = intent.ValueKind == JsonValueKind.Object
                ? [.. intent.GetProperty("combinations").EnumerateArray().Select(e => new Combination(
                    Text(e, "id"), Text(e, "name"), e.GetProperty("score").GetInt32(), Strings(e, "all"), Strings(e, "any"), Strings(e, "none"), Text(e, "description")))]
                : [],
            DigitLetters = ReadDefinition(writing.GetProperty("digitLetters")),
            DigitLettersPattern = Compile(Text(writing.GetProperty("digitLetters"), "pattern")),
            MixedAlphabet = ReadDefinition(writing.GetProperty("mixedAlphabet")),
            ForeignAccents = ReadDefinition(writing.GetProperty("foreignAccents")),
            Shouting = ReadDefinition(writing.GetProperty("shouting")),
            SecondLevelSuffixes = Strings(domains, "secondLevelSuffixes"),
            PublicSuffixes = Strings(domains, "publicSuffixes"),
            KnownDomains = Strings(domains, "knownDomains"),
            Shorteners = Strings(domains, "shorteners"),
            ChannelDomains = Strings(domains, "channelDomains"),
            TokenStopWords = Strings(domains, "tokenStopWords"),
            SuspiciousTlds = new HashSet<string>(suspicious, StringComparer.Ordinal),
            BareTlds = bare,
            LinkSignals = [.. domains.GetProperty("signals").EnumerateObject().Select(p => new KeyValuePair<string, Definition>(p.Name, ReadDefinition(p.Value)))],
            SenderSignals = r.GetProperty("sender").GetProperty("signals").EnumerateObject().ToDictionary(p => p.Name, p => ReadDefinition(p.Value), StringComparer.Ordinal),
            NotConclusive = Text(general, "notConclusive"),
            NoSignsWarning = Text(general, "noSignsWarning"),
            BasicRules = Strings(general, "basicRules"),
            IfClicked = Strings(general, "ifClicked"),
        };
    }

    private static string Text(JsonElement e, string name) => e.GetProperty(name).GetString()!;
    private static bool IsTrue(JsonElement e, string name) => e.TryGetProperty(name, out var d) && d.ValueKind == JsonValueKind.True;
    private static string[] Strings(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => x.GetString()!)];
    private static Regex[] Patterns(JsonElement e, string name) => [.. e.GetProperty(name).EnumerateArray().Select(x => Compile(x.GetString()!))];
    private static Definition ReadDefinition(JsonElement e) => new(Text(e, "name"), e.GetProperty("weight").GetInt32(), Text(e, "description"));
}
