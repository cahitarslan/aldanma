using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Blocklist;

/// <summary>The feed answered with something other than the expected JSON, or broke off mid-download.</summary>
public sealed class UsomFeedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads the malicious address list from the USOM API (T.C. Siber Güvenlik Başkanlığı, siberguvenlik.gov.tr/api).
/// The old plain-text file (usom.gov.tr/url-list.txt) now redirects to an HTML page, so it must not be used.
/// The API is paged, newest first; every page of every type is read, because the importer needs the full list
/// to tell which entries were removed. Any error aborts the whole download: a partial list is never imported.
/// </summary>
public sealed class UsomClient(HttpClient http, IOptions<AldanmaOptions> options, ILogger<UsomClient> log)
{
    private sealed record Page(
        [property: JsonPropertyName("models")] List<Entry>? Models,
        [property: JsonPropertyName("page")] int Number,
        [property: JsonPropertyName("pageCount")] int PageCount,
        [property: JsonPropertyName("totalCount")] int TotalCount);

    private sealed record Entry([property: JsonPropertyName("url")] string? Url);

    public async Task<IReadOnlyList<string>> FetchAllAsync(CancellationToken ct)
    {
        var o = options.Value.Usom;
        var all = new List<string>();
        foreach (var type in o.Types)
        {
            var before = all.Count;
            var pageCount = 1;
            for (var page = 0; page < pageCount; page++)
            {
                if (page >= o.MaxPages) throw new UsomFeedException($"USOM '{type}' list has more than {o.MaxPages} pages; raise Aldanma:Usom:MaxPages.");
                var p = await GetPageAsync(o.Url, type, page, o.PageSize, ct);
                pageCount = p.PageCount;
                all.AddRange(p.Models!.Select(m => m.Url).OfType<string>());
                if (page + 1 < pageCount && o.PageDelayMilliseconds > 0) await Task.Delay(o.PageDelayMilliseconds, ct);
            }
            log.LogInformation("USOM '{Type}' list downloaded: {Count} entries in {Pages} pages", type, all.Count - before, pageCount);
        }
        return all;
    }

    private async Task<Page> GetPageAsync(string baseUrl, string type, int page, int pageSize, CancellationToken ct)
    {
        var url = $"{baseUrl}?type={Uri.EscapeDataString(type)}&page={page}&per-page={pageSize}";
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
