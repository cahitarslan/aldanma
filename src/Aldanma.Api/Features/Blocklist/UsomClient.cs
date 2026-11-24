using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>The feed answered with something other than the expected JSON, or broke off mid-download.</summary>
public sealed class UsomFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the malicious address list from the USOM API (T.C. Siber Güvenlik Başkanlığı, siberguvenlik.gov.tr/api).
/// The old plain-text file (usom.gov.tr/url-list.txt) now redirects to an HTML page, so it must not be used.
/// <para>
/// How the API pages (checked against the live service): the <c>page</c> query parameter is 1-based while the
/// <c>page</c> field in the answer is 0-based; a page past the end returns the last page again; entries are newest
/// first, so entries added during a download push older ones onto the next page (seen twice, never missed).
/// <c>date_gte</c> limits the list to entries added since a date, which makes a refresh a page or two.
/// </para>
/// Any error aborts the whole download: a partial list is never imported.
/// </summary>
public sealed class UsomClient(HttpClient http, IOptions<AldanmaOptions> options, ILogger<UsomClient> log)
{
    private const int ProgressEveryPages = 10;

    private sealed record Page(
        [property: JsonPropertyName("models")] List<Entry>? Models,
        [property: JsonPropertyName("page")] int Index,
        [property: JsonPropertyName("pageCount")] int PageCount,
        [property: JsonPropertyName("totalCount")] int TotalCount);

    private sealed record Entry(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("url")] string? Url);

    /// <param name="since">Only entries added on or after this day; null downloads the whole list.</param>
    public async Task<IReadOnlyList<string>> FetchAsync(DateOnly? since, BlocklistImportStatus status, CancellationToken ct)
    {
        var o = options.Value.Usom;
        var seen = new HashSet<long>();
        var entries = new List<string>();
        var clock = Stopwatch.StartNew();
        foreach (var type in o.TypesOrDefault)
        {
            var first = await GetPageAsync(o, type, since, 1, ct);
            var pageCount = first.PageCount;
            if (pageCount > o.MaxPages) throw new UsomFeedException($"USOM '{type}' list has {pageCount} pages, more than Aldanma:Usom:MaxPages ({o.MaxPages}).");
            status.AddPages(Math.Max(pageCount, 1));

            for (var page = 1; page <= Math.Max(pageCount, 1); page++)
            {
                var p = page == 1 ? first : await GetPageAsync(o, type, since, page, ct);
                // The list shrank during the download and the API repeated its last page: everything is read.
                if (p.Index != page - 1) { status.PageDone(0); break; }
                var added = 0;
                foreach (var m in p.Models!)
                    if (m.Url is { Length: > 0 } url && seen.Add(m.Id)) { entries.Add(url); added++; }
                status.PageDone(added);

                if (page % ProgressEveryPages == 0)
                {
                    var s = status.Snapshot();
                    log.LogInformation("USOM download: page {Done}/{Total}, {Items} entries, {Elapsed:F0} s elapsed, about {Remaining} s left",
                        s.PagesDone, s.PagesTotal, s.ItemsSoFar, clock.Elapsed.TotalSeconds, s.SecondsRemaining);
                }
                if (page < pageCount && o.PageDelayMilliseconds > 0) await Task.Delay(o.PageDelayMilliseconds, ct);
            }
        }
        log.LogInformation("USOM download finished: {Count} entries in {Pages} pages, {Elapsed:F0} s{Since}",
            entries.Count, status.Snapshot().PagesDone, clock.Elapsed.TotalSeconds, since is { } d ? $", since {d:yyyy-MM-dd}" : "");
        return entries;
    }

    /// <summary>A failure worth another try: the service was busy or the network blinked, not a changed API.</summary>
    private sealed class TransientException(string message, TimeSpan? retryAfter = null, Exception? inner = null) : Exception(message, inner)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    /// <summary>Upper bound for a Retry-After the service asks for, so one answer cannot stall the run for long.</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    /// <summary>
    /// One page, tried again after a server error (5xx), 429 or a network failure or timeout, waiting
    /// <see cref="UsomOptions.RetryDelaysOrDefault"/> between tries (or the Retry-After the service sends).
    /// A full download is about 100 pages, so one passing 500 must not throw the whole run away.
    /// </summary>
    private async Task<Page> GetPageAsync(UsomOptions o, string type, DateOnly? since, int page, CancellationToken ct)
    {
        var url = $"{o.Url}?type={Uri.EscapeDataString(type)}&page={page}&per-page={o.PageSize}"
            + (since is { } d ? "&date_gte=" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await TryGetPageAsync(url, type, page, ct);
            }
            catch (TransientException e) when (attempt < o.RetryDelaysOrDefault.Count)
            {
                var wait = e.RetryAfter is { } ra ? (ra > MaxRetryAfter ? MaxRetryAfter : ra) : TimeSpan.FromSeconds(o.RetryDelaysOrDefault[attempt]);
                log.LogWarning("USOM '{Type}' page {Page}: {Problem}; try {Next} of {Max} in {Wait:F0} s",
                    type, page, e.Message, attempt + 2, o.RetryDelaysOrDefault.Count + 1, wait.TotalSeconds);
                await Task.Delay(wait, ct);
            }
            catch (TransientException e)
            {
                throw new UsomFeedException($"USOM '{type}' page {page} failed after {attempt + 1} tries: {e.Message}", e.InnerException);
            }
        }
    }

    private async Task<Page> TryGetPageAsync(string url, string type, int page, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            throw new TransientException(e is TaskCanceledException ? "timed out" : "network error", inner: e);
        }
        using var _ = response;
        var status = (int)response.StatusCode;
        if (status >= 500 || status == 429)
            throw new TransientException($"answered {status}", response.Headers.RetryAfter?.Delta);
        if (!response.IsSuccessStatusCode)
            throw new UsomFeedException($"USOM answered {status} for '{type}' page {page}.");
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
            throw new UsomFeedException($"USOM answered '{mediaType ?? "no content type"}' instead of JSON for '{type}' page {page}; the API may have moved.");
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            var p = await JsonSerializer.DeserializeAsync<Page>(body, cancellationToken: ct);
            if (p?.Models is null || p.PageCount < 0) throw new UsomFeedException($"USOM '{type}' page {page} has no address list.");
            return p;
        }
        catch (JsonException e)
        {
            throw new UsomFeedException($"USOM '{type}' page {page} is not valid JSON.", e);
        }
    }
}
