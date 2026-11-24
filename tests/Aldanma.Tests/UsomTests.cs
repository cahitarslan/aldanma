using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Aldanma.Api.Features.Blocklist;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

public class UsomClientTests
{
    /// <summary>Serves the paged USOM API from memory: two domain pages and one URL page.</summary>
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
                _ when url.Contains("type=domain") && url.Contains("page=0") =>
                    """{"totalCount":3,"count":2,"models":[{"url":"kargo-takip-ptt.xyz","type":"domain"},{"url":"odeme-iade.top","type":"domain"}],"page":0,"pageCount":2}""",
                _ when url.Contains("type=domain") && url.Contains("page=1") =>
                    """{"totalCount":3,"count":1,"models":[{"url":"e-devlet-iade.click","type":"domain"}],"page":1,"pageCount":2}""",
                _ when url.Contains("type=url") =>
                    """{"totalCount":1,"count":1,"models":[{"url":"https://banka-giris.example.top/login","type":"url"}],"page":0,"pageCount":1}""",
                _ => """{"message":"unexpected"}""",
            };
            return Task.FromResult(Respond(json, "application/json"));
        }

        private static HttpResponseMessage Respond(string body, string mediaType) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };
    }

    private static UsomClient Client(FakeUsom handler) => new(
        new HttpClient(handler),
        Options.Create(new AldanmaOptions { Usom = new UsomOptions { PageSize = 2, PageDelayMilliseconds = 0 } }),
        NullLogger<UsomClient>.Instance);

    [Fact]
    public async Task Reads_every_page_of_every_type()
    {
        var handler = new FakeUsom();
        var entries = await Client(handler).FetchAllAsync(CancellationToken.None);

        Assert.Equal(["kargo-takip-ptt.xyz", "odeme-iade.top", "e-devlet-iade.click", "https://banka-giris.example.top/login"], entries);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, r => Assert.Contains("per-page=2", r));
        Assert.StartsWith("https://siberguvenlik.gov.tr/api/address/index?", handler.Requests[0]);
    }

    [Fact]
    public async Task An_html_page_instead_of_the_api_is_an_error()
    {
        var e = await Assert.ThrowsAsync<UsomFeedException>(() => Client(new FakeUsom(html: true)).FetchAllAsync(CancellationToken.None));
        Assert.Contains("instead of JSON", e.Message);
    }

    [Fact]
    public void The_moved_plain_text_list_is_no_longer_the_default() =>
        Assert.DoesNotContain("url-list.txt", new UsomOptions().Url);
}

public class BlocklistImportTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    [DatabaseFact]
    public async Task A_download_with_no_usable_entries_fails_instead_of_importing_nothing()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<BlocklistImporter>();

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            importer.ImportAsync("usom", "<!DOCTYPE html>\n<html lang=\"en\">\n<title>Swagger</title>\n</html>\n", CancellationToken.None));
        Assert.Contains("no usable entries", e.Message);

        var result = await importer.ImportAsync("usom", ["odgiris.com", "https://banka-giris.example.top/login", "not a domain"], CancellationToken.None);
        Assert.Equal(2, result.Total);
        Assert.Equal(1, result.Skipped);
        Assert.Equal(2, await factory.WithDb(db => db.BlocklistEntries.CountAsync(b => b.Source == "usom" && b.RemovedAt == null)));
    }
}
