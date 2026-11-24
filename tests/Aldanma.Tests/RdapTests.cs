using System.Text.Json;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

public class RdapTests
{
    [Fact]
    public void Registration_date_and_registrar_are_read_from_an_RDAP_answer()
    {
        const string json = """
            {
              "objectClassName": "domain", "ldhName": "EXAMPLE.COM",
              "events": [
                { "eventAction": "registration", "eventDate": "2026-11-15T10:20:30Z" },
                { "eventAction": "expiration", "eventDate": "2027-11-15T10:20:30Z" }
              ],
              "entities": [
                { "objectClassName": "entity", "roles": ["registrar"],
                  "vcardArray": ["vcard", [["version", {}, "text", "4.0"], ["fn", {}, "text", "Example Registrar, Inc."]]] }
              ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        var r = RdapClient.Parse(doc.RootElement);
        Assert.NotNull(r);
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 10, 20, 30, TimeSpan.Zero), r.CreatedAt);
        Assert.Equal("Example Registrar, Inc.", r.Registrar);
    }

    [Fact]
    public void An_answer_without_events_gives_nothing()
    {
        using var doc = JsonDocument.Parse("""{ "objectClassName": "domain" }""");
        Assert.Null(RdapClient.Parse(doc.RootElement));
    }
}
