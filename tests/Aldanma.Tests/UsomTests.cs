using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aldanma.Api.Data;
using Aldanma.Api.Features.Blocklist;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

public class UsomClientTests
{
    /// <summary>
    /// Serves the paged USOM API from memory the way the live one behaves: the page query is 1-based, the page field
    /// 0-based, and a page past the end repeats the last page. Domains: two pages (the second repeats id 2, as when
    /// new entries push older ones down during a download). URLs: one page.
    /// </summary>
    private sealed class FakeUsom(bool html = false) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            if (html) return Task.FromResult(Respond("<!DOCTYPE html><html><body>Swagger</body></html>", "text/html"));
            var json = url switch
            {
                _ when url.Contains("type=domain") && url.Contains("page=1&") =>
                    """{"totalCount":3,"count":2,"models":[{"id":3,"url":"kargo-takip-ptt.xyz"},{"id":2,"url":"odeme-iade.top"}],"page":0,"pageCount":2}""",
                _ when url.Contains("type=domain") =>
                    """{"totalCount":3,"count":2,"models":[{"id":2,"url":"odeme-iade.top"},{"id":1,"url":"e-devlet-iade.click"}],"page":1,"pageCount":2}""",
                _ when url.Contains("type=url") =>
                    """{"totalCount":1,"count":1,"models":[{"id":9,"url":"https://banka-giris.example.top/login"}],"page":0,"pageCount":1}""",
                _ => """{"message":"unexpected"}""",
            };
            return Task.FromResult(Respond(json, "application/json; charset=UTF-8"));
        }

        private static HttpResponseMessage Respond(string body, string mediaType) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8) { Headers = { ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(mediaType) } } };
    }

    private static UsomClient Client(FakeUsom handler) => new(
        new HttpClient(handler),
        Options.Create(new AldanmaOptions { Usom = new UsomOptions { PageSize = 2, PageDelayMilliseconds = 0 } }),
        NullLogger<UsomClient>.Instance);

    private static BlocklistImportStatus Status() => new(TimeProvider.System);

    /// <summary>Reads everything into one list, as a caller that saves page by page would see it.</summary>
    private static async Task<List<string>> ReadAll(UsomClient client, DateOnly? since, BlocklistImportStatus status)
    {
        var all = new List<string>();
        await client.ReadAsync(since, UsomPosition.Start, status, (page, _) => { all.AddRange(page); return Task.CompletedTask; }, CancellationToken.None);
        return all;
    }

    [Fact]
    public async Task Reads_every_page_of_every_type_once_from_page_1()
    {
        var handler = new FakeUsom();
        var status = Status();
        status.Start(BlocklistImportStatus.Full);
        var entries = await ReadAll(Client(handler), since: null, status);

        // odeme-iade.top was pushed onto the next page mid-download: seen twice, saved once by the upsert.
        Assert.Equal(["kargo-takip-ptt.xyz", "odeme-iade.top", "e-devlet-iade.click", "https://banka-giris.example.top/login"], entries.Distinct());
        Assert.Equal(3, handler.Requests.Count);
        Assert.Contains("page=1&", handler.Requests[0]);
        Assert.All(handler.Requests, r => Assert.DoesNotContain("page=0", r));
        Assert.All(handler.Requests, r => Assert.DoesNotContain("date_gte", r));
        var s = status.Snapshot();
        Assert.Equal((3, 3, 5), (s.PagesDone, s.PagesTotal, s.ItemsSoFar));
    }

    [Fact]
    public async Task A_refresh_asks_only_for_entries_since_a_day()
    {
        var handler = new FakeUsom();
        await ReadAll(Client(handler), new DateOnly(2026, 11, 22), Status());
        Assert.All(handler.Requests, r => Assert.Contains("date_gte=2026-11-22", r));
    }

    [Fact]
    public async Task An_html_page_instead_of_the_api_is_an_error()
    {
        var e = await Assert.ThrowsAsync<UsomFeedException>(() => ReadAll(Client(new FakeUsom(html: true)), null, Status()));
        Assert.Contains("instead of JSON", e.Message);
    }

    /// <summary>One page that fails <paramref name="failures"/> times with <paramref name="status"/>, then answers.</summary>
    private sealed class FlakyUsom(int failures, HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref Calls) <= failures) return Task.FromResult(new HttpResponseMessage(status));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"totalCount":1,"count":1,"models":[{"id":1,"url":"odgiris.com"}],"page":0,"pageCount":1}""", Encoding.UTF8, "application/json"),
            });
        }
    }

    private static UsomClient Flaky(FlakyUsom handler) => new(
        new HttpClient(handler),
        Options.Create(new AldanmaOptions { Usom = new UsomOptions { Types = ["domain"], PageDelayMilliseconds = 0, RetryDelaysSeconds = [0, 0, 0, 0] } }),
        NullLogger<UsomClient>.Instance);

    [Fact]
    public async Task A_passing_server_error_is_retried_instead_of_failing_the_run()
    {
        var handler = new FlakyUsom(failures: 3, HttpStatusCode.InternalServerError);
        Assert.Equal(["odgiris.com"], await ReadAll(Flaky(handler), null, Status()));
        Assert.Equal(4, handler.Calls);
    }

    [Fact]
    public async Task A_page_that_keeps_failing_fails_the_run_after_the_last_retry()
    {
        var handler = new FlakyUsom(failures: 100, HttpStatusCode.ServiceUnavailable);
        var e = await Assert.ThrowsAsync<UsomFeedException>(() => ReadAll(Flaky(handler), null, Status()));
        Assert.Contains("after 5 tries", e.Message);
        Assert.Equal(5, handler.Calls);
    }

    [Fact]
    public async Task A_client_error_is_not_retried()
    {
        var handler = new FlakyUsom(failures: 100, HttpStatusCode.NotFound);
        await Assert.ThrowsAsync<UsomFeedException>(() => ReadAll(Flaky(handler), null, Status()));
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void The_moved_plain_text_list_is_no_longer_the_default() =>
        Assert.DoesNotContain("url-list.txt", new UsomOptions().Url);

    [Fact]
    public void Status_estimates_the_time_left_and_hides_internals_on_failure()
    {
        var status = Status();
        status.Start(BlocklistImportStatus.Full);
        status.AddPages(50);
        status.PageDone(9999);
        var running = status.Snapshot();
        Assert.Equal(BlocklistImportStatus.Running, running.State);
        Assert.NotNull(running.SecondsRemaining);

        status.Fail("download failed");
        var failed = status.Snapshot();
        Assert.Equal((BlocklistImportStatus.Failed, "download failed"), (failed.State, failed.LastError));
        Assert.Null(failed.SecondsRemaining);
    }
}

public class BlocklistImportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [DatabaseFact]
    public async Task First_import_is_full_later_ones_refresh_and_never_remove()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<BlocklistImporter>();
        var week = TimeSpan.FromDays(7);
        int Active() => factory.WithDb(db => db.BlocklistEntries.CountAsync(b => b.Source == "plan-test" && b.RemovedAt == null)).Result;

        // Nothing stored yet: a full import. An HTML page is refused rather than imported as nothing.
        Assert.True((await importer.PlanAsync("plan-test", week, CancellationToken.None)).Full);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ImportAsync("plan-test", "<!DOCTYPE html>\n<html lang=\"en\">\n<title>Swagger</title>\n</html>\n", CancellationToken.None));
        Assert.Contains("no usable entries", e.Message);

        var full = await importer.ImportAsync("plan-test", ["odgiris.com", "https://banka-giris.example.top/login", "not a domain"], full: true, CancellationToken.None);
        Assert.Equal((2, 2, 1), (full.Total, full.Added, full.Skipped));

        // Stored list, recent full import: a refresh from two days before the last import.
        var plan = await importer.PlanAsync("plan-test", week, CancellationToken.None);
        Assert.False(plan.Full);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-2)), plan.Since);

        // A refresh adds and removes nothing that is missing from it; an empty refresh is fine.
        var refresh = await importer.ImportAsync("plan-test", ["yeni-kargo-iade.top"], full: false, CancellationToken.None);
        Assert.Equal((1, 0), (refresh.Added, refresh.Removed));
        Assert.Equal(0, (await importer.ImportAsync("plan-test", [], full: false, CancellationToken.None)).Total);
        Assert.Equal(3, Active());

        // Once the full import is older than the resync period, the next one is full again.
        Assert.True((await importer.PlanAsync("plan-test", TimeSpan.Zero, CancellationToken.None)).Full);
    }

    [DatabaseFact]
    public async Task Meta_shows_the_import_progress()
    {
        factory.Services.GetRequiredService<BlocklistImportStatus>().Start(BlocklistImportStatus.Incremental);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("CF-Connecting-IP", "198.51.100.77");
        var meta = await client.GetFromJsonAsync<JsonElement>("/api/meta");
        var import = meta.GetProperty("blocklistImport");
        Assert.Equal("running", import.GetProperty("state").GetString());
        Assert.Equal("incremental", import.GetProperty("mode").GetString());
    }
}

public class UsomOptionsBindingTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>
    /// The configuration binder appends to array defaults instead of replacing them, which once made every
    /// list type download twice. Bound options must hold each type and retry delay exactly once.
    /// </summary>
    [DatabaseFact]
    public void Array_settings_are_not_doubled_by_binding()
    {
        var usom = factory.Services.GetRequiredService<IOptions<AldanmaOptions>>().Value.Usom;
        Assert.Equal(["domain", "url"], usom.TypesOrDefault);
        Assert.Equal([2, 5, 15, 30], usom.RetryDelaysOrDefault);
    }
}

/// <summary>A full run that stops halfway keeps its pages, resumes from the next one, and removes nothing until it completes.</summary>
public class UsomResumeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    /// <summary>Domains on three pages, URLs on one. Page 3 of the domains fails (404, not retried) until <see cref="Healthy"/>.</summary>
    private sealed class PagedUsom : HttpMessageHandler
    {
        public bool Healthy;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            lock (Requests) Requests.Add(url);
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query)["page"]!);
            if (url.Contains("type=domain") && page == 3 && !Healthy) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var json = url.Contains("type=domain")
                ? $$"""{"totalCount":3,"count":1,"models":[{"id":{{page}},"url":"sayfa{{page}}-zararli.top"}],"page":{{page - 1}},"pageCount":3}"""
                : """{"totalCount":1,"count":1,"models":[{"id":9,"url":"https://banka-giris.example.top/login"}],"page":0,"pageCount":1}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [DatabaseFact]
    public async Task A_stopped_full_run_resumes_from_the_next_page_and_removes_only_when_complete()
    {
        var handler = new PagedUsom();
        await using var app = factory.WithWebHostBuilder(b => b
            .UseSetting("Aldanma:Usom:PageDelayMilliseconds", "0")
            .ConfigureServices(s => s.AddHttpClient<UsomClient>().ConfigurePrimaryHttpMessageHandler(() => handler)));
        // An entry from a full run ten days ago: due for a full resync, and USOM no longer lists it.
        await using (var seed = app.Services.CreateAsyncScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<AppDbContext>();
            var old = DateTimeOffset.UtcNow.AddDays(-10);
            db.BlocklistEntries.Add(new BlocklistEntry { Source = UsomFeedService.Source, Kind = "domain", Value = "eski-liste.top", FirstSeenAt = old, LastSeenAt = old });
            await db.SaveChangesAsync();
        }
        var feed = ActivatorUtilities.CreateInstance<UsomFeedService>(app.Services);
        async Task<List<string>> Active() => await factory.WithDb(db => db.BlocklistEntries
            .Where(b => b.Source == UsomFeedService.Source && b.RemovedAt == null).Select(b => b.Value).ToListAsync()).ContinueWith(t => t.Result.Order(StringComparer.Ordinal).ToList());

        // Run 1 stops at domain page 3: pages 1 and 2 are saved, the old entry is not removed yet.
        Assert.False(await feed.RunOnceAsync(CancellationToken.None));
        Assert.Equal(["eski-liste.top", "sayfa1-zararli.top", "sayfa2-zararli.top"], await Active());
        var open = await factory.WithDb(db => db.BlocklistImportRuns.SingleAsync());
        Assert.Equal(("full", 0, 3), (open.Mode, open.TypeIndex, open.NextPage));

        // Run 2 starts at domain page 3, not page 1, then completes and drops what USOM no longer lists.
        handler.Healthy = true;
        var before = handler.Requests.Count;
        Assert.True(await feed.RunOnceAsync(CancellationToken.None));
        Assert.Contains("type=domain", handler.Requests[before]);
        Assert.Contains("page=3&", handler.Requests[before]);
        Assert.DoesNotContain(handler.Requests.Skip(before), r => r.Contains("type=domain") && r.Contains("page=1&"));
        // The URL is stored the way the parser keys it (its registrable domain), like every other lookup.
        Assert.Equal(["example.top", "sayfa1-zararli.top", "sayfa2-zararli.top", "sayfa3-zararli.top"], await Active());
        Assert.Equal(0, await factory.WithDb(db => db.BlocklistImportRuns.CountAsync()));

        // The list is fresh now: the next run is a short refresh, not another full read.
        Assert.True(await feed.RunOnceAsync(CancellationToken.None));
        Assert.Equal("incremental", app.Services.GetRequiredService<BlocklistImportStatus>().Snapshot().Mode);
    }
}
