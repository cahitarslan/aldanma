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
        foreach (var type in o.Types)
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

    private async Task<Page> GetPageAsync(UsomOptions o, string type, DateOnly? since, int page, CancellationToken ct)
    {
        var url = $"{o.Url}?type={Uri.EscapeDataString(type)}&page={page}&per-page={o.PageSize}"
            + (since is { } d ? "&date_gte=" + d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : "");
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new UsomFeedException($"USOM answered {(int)response.StatusCode} for '{type}' page {page}.");
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
