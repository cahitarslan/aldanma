namespace Aldanma.Api.Infrastructure;

public sealed class AldanmaOptions
{
    public const string Section = "Aldanma";

    /// <summary>
    /// Secret key for the keyed hashes of phone numbers, IBANs and reporter IPs. Required outside Development.
    /// Changing it orphans every stored hash, so keep it stable and backed up.
    /// </summary>
    public string HashKey { get; set; } = "";

    /// <summary>Header value that unlocks the moderation endpoints. Empty disables them.</summary>
    public string AdminKey { get; set; } = "";

    /// <summary>Browser origins allowed to call the API (the static site).</summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>Cloudflare Turnstile secret. Empty disables the human check (development only).</summary>
    public string TurnstileSecret { get; set; } = "";

    /// <summary>
    /// Request header that carries the real client IP, set by the proxy in front of the API: CF-Connecting-IP behind
    /// Cloudflare, X-Forwarded-For behind Caddy or Cloud Run. Leave empty when nothing sits in front of the API:
    /// the header could then be forged to dodge rate limits.
    /// </summary>
    public string ClientIpHeader { get; set; } = "";

    /// <summary>Apply EF Core migrations at startup.</summary>
    public bool MigrateOnStartup { get; set; } = true;

    public RdapOptions Rdap { get; set; } = new();
    public UsomOptions Usom { get; set; } = new();
    public NotifyOptions Notify { get; set; } = new();
    public LimitOptions Limits { get; set; } = new();
}

/// <summary>
/// Flood protection inside the app. Volumetric attacks belong to the edge (Cloudflare, see docs/DEPLOY.md); these limits
/// keep one client, or a flood that got through, from exhausting the database, outbound calls or the hosting bill.
/// </summary>
public sealed class LimitOptions
{
    /// <summary>Sustained requests per minute per client (IPv4 address or IPv6 /64), across every endpoint.</summary>
    public int PerClientPerMinute { get; set; } = 120;
    /// <summary>Requests a client may send at once before the per-minute rate applies.</summary>
    public int PerClientBurst { get; set; } = 30;
    /// <summary>Requests in flight across the whole server; beyond this they queue, then get 429.</summary>
    public int MaxConcurrentRequests { get; set; } = 200;
    public int ConcurrentQueueLimit { get; set; } = 50;
    public int MaxConcurrentConnections { get; set; } = 1000;
    public int RequestHeadersTimeoutSeconds { get; set; } = 15;
    public int KeepAliveTimeoutSeconds { get; set; } = 60;
    public int MaxRequestHeadersTotalSizeKb { get; set; } = 16;
    public int MaxRequestLineSizeKb { get; set; } = 8;
    /// <summary>Default time a request may take; lookups get <see cref="LookupTimeoutSeconds"/> for their RDAP calls.</summary>
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int LookupTimeoutSeconds { get; set; } = 20;

    public bool IsValid() =>
        PerClientPerMinute is >= 6 and <= 100_000 && PerClientBurst is >= 1 and <= 10_000
        && MaxConcurrentRequests is >= 1 and <= 100_000 && ConcurrentQueueLimit is >= 0 and <= 100_000
        && MaxConcurrentConnections is >= 10 and <= 1_000_000
        && RequestHeadersTimeoutSeconds is >= 1 and <= 120 && KeepAliveTimeoutSeconds is >= 1 and <= 600
        && MaxRequestHeadersTotalSizeKb is >= 4 and <= 64 && MaxRequestLineSizeKb is >= 2 and <= 64
        && RequestTimeoutSeconds is >= 1 and <= 120 && LookupTimeoutSeconds >= RequestTimeoutSeconds && LookupTimeoutSeconds <= 120;
}

public sealed class RdapOptions
{
    public bool Enabled { get; set; } = true;
    public string BaseUrl { get; set; } = "https://rdap.org/domain/";
    public int TimeoutSeconds { get; set; } = 4;
    public int CacheDays { get; set; } = 7;
    /// <summary>New (uncached) RDAP fetches one request may trigger; the rest get no domain age.</summary>
    public int MaxFetchesPerRequest { get; set; } = 3;
    /// <summary>RDAP calls in flight across the server; when all are busy a lookup skips the age instead of waiting.</summary>
    public int MaxConcurrentFetches { get; set; } = 8;

    public bool IsValid() => TimeoutSeconds is >= 1 and <= 30 && MaxFetchesPerRequest is >= 0 and <= 10 && MaxConcurrentFetches is >= 1 and <= 100;
}

/// <summary>
/// E-mails to the site owner when a report, dispute or message for review arrives. Off unless the Resend key,
/// sender and recipient are all set. A mail holds counts only, never an identifier or message text.
/// </summary>
public sealed class NotifyOptions
{
    /// <summary>Resend API key with sending access only. Secret: set it as an environment variable.</summary>
    public string ResendApiKey { get; set; } = "";
    /// <summary>Sender on a domain verified in Resend, e.g. "Aldanma &lt;aldanma@mail.example.com&gt;".</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Wait after the first event so a burst becomes one mail.</summary>
    public int BatchDelaySeconds { get; set; } = 60;
    /// <summary>At most one mail per this many seconds, whatever arrives.</summary>
    public int MinIntervalSeconds { get; set; } = 1800;

    public bool IsEnabled => ResendApiKey.Length > 0 && From.Length > 0 && To.Length > 0;

    public bool IsValid() =>
        BatchDelaySeconds is >= 0 and <= 3600 && MinIntervalSeconds is >= 0 and <= 86_400
        && (!IsEnabled || (To.Contains('@') && From.Contains('@')));
}

public sealed class UsomOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>The paged address API. The old url-list.txt now redirects to an HTML page.</summary>
    public string Url { get; set; } = "https://siberguvenlik.gov.tr/api/address/index";
    /// <summary>Address types to import; the scorer uses domains and URLs only.</summary>
    public string[] Types { get; set; } = ["domain", "url"];
    /// <summary>Entries per request; the API allows at most 9999.</summary>
    public int PageSize { get; set; } = 9999;
    /// <summary>Safety stop: a list longer than this is treated as an error instead of being downloaded forever.</summary>
    public int MaxPages { get; set; } = 200;
    /// <summary>Pause between page requests, to stay polite to a public service.</summary>
    public int PageDelayMilliseconds { get; set; } = 500;
    public int IntervalHours { get; set; } = 12;

    public bool IsValid() =>
        Uri.TryCreate(Url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && Types.Length > 0
        && PageSize is >= 1 and <= 9999 && MaxPages is >= 1 and <= 10_000 && PageDelayMilliseconds is >= 0 and <= 10_000;
}
