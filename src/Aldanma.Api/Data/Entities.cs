namespace Aldanma.Api.Data;

/// <summary>
/// A reported or listed identifier. Phone numbers and IBANs are stored only as a keyed hash plus a masked
/// form; the plain value is never written to the database. Domains and URLs are public and kept in plain text.
/// </summary>
public sealed class IdentifierRecord
{
    public long Id { get; set; }
    public required string Kind { get; set; }
    public required byte[] KeyHash { get; set; }
    /// <summary>Plain value for domains and URLs only; null for phone numbers and IBANs.</summary>
    public string? PublicValue { get; set; }
    public required string Masked { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Set by a moderator (e.g. after an accepted dispute). Hidden identifiers score as if never reported.</summary>
    public bool Hidden { get; set; }
    public List<ReportIdentifier> Reports { get; set; } = [];
}

public static class ReportStatus
{
    public const string Active = "active";
    public const string Rejected = "rejected";
}

/// <summary>One community report. It can name several identifiers seen in the same scam.</summary>
public sealed class Report
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Category { get; set; }
    public required string Channel { get; set; }
    public required string Loss { get; set; }
    /// <summary>Month the scam happened, if given (first day of the month).</summary>
    public DateOnly? OccurredOn { get; set; }
    /// <summary>Keyed hash of the reporter's IP address. Used only to count distinct reporters and stop repeats.</summary>
    public required byte[] ReporterHash { get; set; }
    public string Status { get; set; } = ReportStatus.Active;
    public List<ReportIdentifier> Identifiers { get; set; } = [];
}

public sealed class ReportIdentifier
{
    public Guid ReportId { get; set; }
    public Report Report { get; set; } = null!;
    public long IdentifierId { get; set; }
    public IdentifierRecord Identifier { get; set; } = null!;
}

/// <summary>An entry imported from a public block list such as USOM.</summary>
public sealed class BlocklistEntry
{
    public long Id { get; set; }
    public required string Source { get; set; }
    public required string Kind { get; set; }
    public required string Value { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    /// <summary>Set when the entry disappears from the source list.</summary>
    public DateTimeOffset? RemovedAt { get; set; }
}

/// <summary>
/// Where an unfinished block list import stopped, so a restart or redeploy resumes it instead of starting over.
/// One row per source while a run is going; deleted when the run completes.
/// </summary>
public sealed class BlocklistImportRun
{
    public required string Source { get; set; }
    /// <summary>"full" or "incremental".</summary>
    public required string Mode { get; set; }
    /// <summary>Stamp written to LastSeenAt of every entry this run sees; a full run removes what is older.</summary>
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>For an incremental run: only entries added since this day.</summary>
    public DateOnly? Since { get; set; }
    /// <summary>Index into the configured list types of the type being read.</summary>
    public int TypeIndex { get; set; }
    /// <summary>Next page to read, 1-based like the USOM API.</summary>
    public int NextPage { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// How far the moderation e-mails have reported, kept in the database so a restart or redeploy loses nothing:
/// every report, dispute and submission created after <see cref="LastNotifiedAt"/> is still to be mentioned.
/// </summary>
public sealed class NotificationState
{
    public required string Key { get; set; }
    /// <summary>Everything created up to this moment has been in a mail (or predates the feature).</summary>
    public DateTimeOffset LastNotifiedAt { get; set; }
    /// <summary>When the last mail went out; the throttle counts from here. Null before the first mail.</summary>
    public DateTimeOffset? LastSentAt { get; set; }
}

/// <summary>Cached RDAP answer for a domain.</summary>
public sealed class DomainRegistrationCache
{
    public required string Domain { get; set; }
    public bool Found { get; set; }
    public DateTimeOffset? RegisteredAt { get; set; }
    public string? Registrar { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
}

public static class DisputeStatus
{
    public const string Open = "open";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>Objection from someone who says an identifier is theirs and was reported wrongly.</summary>
public sealed class Dispute
{
    public Guid Id { get; set; }
    public long IdentifierId { get; set; }
    public IdentifierRecord Identifier { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public required string Contact { get; set; }
    public required string Reason { get; set; }
    public string Status { get; set; } = DisputeStatus.Open;
}

/// <summary>
/// A "was this result right?" answer. Holds only what the engine already returned (level, score, rule ids),
/// never the checked text or identifiers. Collected as labelled data for measuring and, later, training.
/// </summary>
public sealed class FeedbackEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool Correct { get; set; }
    public required string Level { get; set; }
    public int Score { get; set; }
    public required string RulesVersion { get; set; }
    public List<string> Kinds { get; set; } = [];
    public List<string> Categories { get; set; } = [];
    public List<string> Signals { get; set; } = [];
}

public static class SubmissionStatus
{
    public const string Open = "open";
    public const string Accepted = "accepted";
    public const string Rejected = "rejected";
}

/// <summary>
/// A message the user chose to send for review (the only case where message text is stored). Phone numbers
/// and IBANs in it are masked before it is saved. A moderator labels it; nothing is learned automatically.
/// </summary>
public sealed class Submission
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public required string Text { get; set; }
    public required string Sender { get; set; }
    /// <summary>What the user says about the result: "missed" (a scam not flagged), "false-alarm" or "confirmed" (it was right).</summary>
    public required string Claim { get; set; }
    /// <summary>Level the engine gave when the user sent it.</summary>
    public required string Level { get; set; }
    public required string RulesVersion { get; set; }
    public string Status { get; set; } = SubmissionStatus.Open;
    /// <summary>Moderator's label once reviewed: "scam" or "legit".</summary>
    public string? Label { get; set; }
}
