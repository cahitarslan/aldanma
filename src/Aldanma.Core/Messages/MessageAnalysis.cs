namespace Aldanma.Core.Messages;

/// <summary>Channel the message arrived on. The user picks it; the engine does not guess.</summary>
public static class Sender
{
    public const string Unknown = "unknown";
    public const string Header = "header";
    public const string Mobile = "mobile";
    public const string Foreign = "foreign";
    public const string WhatsApp = "whatsapp";

    public static readonly IReadOnlyList<string> All = [Unknown, Header, Mobile, Foreign, WhatsApp];
}

/// <summary>Risk levels. There is deliberately no "safe" level.</summary>
public static class RiskLevel
{
    public const string High = "high";
    public const string Suspicious = "suspicious";
    public const string NoSigns = "no-signs";
}

public sealed record NamedItem(string Id, string Name);

/// <summary>A single risk signal with its plain-language reason.</summary>
public sealed record Signal(string Id, string Type, string Name, int Weight, string Reason);

public sealed record LinkResult(string Raw, string Host, string RegisteredDomain, bool Official, IReadOnlyList<string> Signals);

public sealed record OfficialLink(string Name, string Url);

public sealed record VerificationPath(string Title, string Text, IReadOnlyList<OfficialLink> Links);

/// <summary>Result of analysing one message. The JSON shape is identical to the JS engine output.</summary>
public sealed record MessageAnalysis(
    int Score,
    string Level,
    IReadOnlyList<NamedItem> Categories,
    IReadOnlyList<NamedItem> Institutions,
    IReadOnlyList<Signal> Signals,
    IReadOnlyList<LinkResult> Links,
    IReadOnlyList<string> WhatToDo,
    IReadOnlyList<VerificationPath> Verification,
    IReadOnlyList<string> IfClicked,
    string Note,
    string RulesVersion);
