using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Infrastructure;

public interface IDomainRegistrationLookup
{
    Task<DomainRegistration?> GetAsync(string domain, CancellationToken ct);
}

/// <summary>
/// Server-wide cap on RDAP calls in flight. Random unique domains would otherwise turn each lookup into outbound
/// traffic, so a flood here could amplify onto rdap.org and tie up the server.
/// </summary>
public sealed class RdapThrottle(IOptions<AldanmaOptions> options)
{
    public SemaphoreSlim Gate { get; } = new(options.Value.Rdap.MaxConcurrentFetches, options.Value.Rdap.MaxConcurrentFetches);
}

/// <summary>Per-request allowance of new RDAP fetches (scoped: one per HTTP request).</summary>
public sealed class RdapBudget(IOptions<AldanmaOptions> options)
{
    private int _left = options.Value.Rdap.MaxFetchesPerRequest;

    public bool TryTake()
    {
        if (_left <= 0) return false;
        _left--;
        return true;
    }
}

/// <summary>
/// Reads a domain's registration date over RDAP (the JSON successor of WHOIS) and caches the answer.
/// Failures, timeouts and skipped fetches return null: domain age is a helpful signal, never a blocking one.
/// </summary>
public sealed class RdapClient(HttpClient http, AppDbContext db, IOptions<AldanmaOptions> options, RdapBudget budget, RdapThrottle throttle,
    AldanmaMetrics metrics, TimeProvider time, ILogger<RdapClient> log)
    : IDomainRegistrationLookup
{
    /// <summary>How long a lookup waits for a free RDAP slot before giving up on the domain age.</summary>
    private static readonly TimeSpan SlotWait = TimeSpan.FromMilliseconds(250);

    private readonly RdapOptions _o = options.Value.Rdap;

    public async Task<DomainRegistration?> GetAsync(string domain, CancellationToken ct)
    {
        if (!_o.Enabled) return null;
        var now = time.GetUtcNow();
        var cached = await db.DomainRegistrations.AsNoTracking().FirstOrDefaultAsync(x => x.Domain == domain, ct);
        if (cached is not null && now - cached.FetchedAt < TimeSpan.FromDays(cached.Found ? _o.CacheDays : 1))
            return cached.Found ? new DomainRegistration(cached.RegisteredAt, cached.Registrar) : null;

        if (!budget.TryTake()) { metrics.RdapSkipped("per-request"); return null; }
        if (!await throttle.Gate.WaitAsync(SlotWait, ct)) { metrics.RdapSkipped("busy"); return null; }

        DomainRegistration? result;
        bool found;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_o.TimeoutSeconds));
            using var response = await http.GetAsync(_o.BaseUrl + Uri.EscapeDataString(domain), cts.Token);
            if (response.StatusCode == HttpStatusCode.NotFound) { result = null; found = false; }
            else
            {
                response.EnsureSuccessStatusCode();
                await using var body = await response.Content.ReadAsStreamAsync(cts.Token);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: cts.Token);
                result = Parse(doc.RootElement);
                found = result is not null;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            // Not cached: a transient failure should not hide the age for a whole day.
            log.LogInformation("RDAP lookup failed for a domain: {Error}", e.GetType().Name);
            return null;
        }
        finally
        {
            throttle.Gate.Release();
        }

        var entry = await db.DomainRegistrations.FirstOrDefaultAsync(x => x.Domain == domain, ct);
        if (entry is null) db.DomainRegistrations.Add(entry = new DomainRegistrationCache { Domain = domain });
        entry.Found = found;
        entry.RegisteredAt = result?.CreatedAt;
        entry.Registrar = result?.Registrar is { Length: > 200 } r ? r[..200] : result?.Registrar;
        entry.FetchedAt = now;
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException) { /* another request cached it first */ }
        return result;
    }

    /// <summary>Reads the "registration" event date and the registrar's name from an RDAP domain object.</summary>
    public static DomainRegistration? Parse(JsonElement root)
    {
        DateTimeOffset? created = null;
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
        {
            foreach (var ev in events.EnumerateArray())
            {
                if (ev.TryGetProperty("eventAction", out var action) && action.GetString() == "registration"
                    && ev.TryGetProperty("eventDate", out var date) && DateTimeOffset.TryParse(date.GetString(), out var d))
                {
                    created = d;
                }
            }
        }
        string? registrar = null;
        if (root.TryGetProperty("entities", out var entities) && entities.ValueKind == JsonValueKind.Array)
        {
            foreach (var en in entities.EnumerateArray())
            {
                var isRegistrar = en.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                    && roles.EnumerateArray().Any(x => x.GetString() == "registrar");
                if (isRegistrar) registrar = VcardName(en);
            }
        }
        return created is null && registrar is null ? null : new DomainRegistration(created, registrar);
    }

    // vcardArray: ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Name"], ...]]
    private static string? VcardName(JsonElement entity)
    {
        if (!entity.TryGetProperty("vcardArray", out var v) || v.ValueKind != JsonValueKind.Array || v.GetArrayLength() < 2) return null;
        foreach (var prop in v[1].EnumerateArray())
        {
            if (prop.ValueKind == JsonValueKind.Array && prop.GetArrayLength() >= 4 && prop[0].GetString() == "fn") return prop[3].GetString();
        }
        return null;
    }
}
