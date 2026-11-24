using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Aldanma.Api.Infrastructure;

namespace Aldanma.Tests;

public class ClientAddressTests
{
    private static HttpContext Request(string? header)
    {
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("192.0.2.10");
        if (header is not null) ctx.Request.Headers["CF-Connecting-IP"] = header;
        return ctx;
    }

    private static ClientAddress With(string header) => new(Options.Create(new AldanmaOptions { ClientIpHeader = header }));

    [Fact]
    public void A_proxy_header_is_ignored_unless_configured()
    {
        Assert.Equal("192.0.2.10", With("").Of(Request("203.0.113.5")));
    }

    [Fact]
    public void A_configured_proxy_header_wins_over_the_socket_address()
    {
        Assert.Equal("203.0.113.5", With("CF-Connecting-IP").Of(Request("203.0.113.5")));
        Assert.Equal("192.0.2.10", With("CF-Connecting-IP").Of(Request(null)));
    }

    [Theory]
    [InlineData("203.0.113.5", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.5", "203.0.113.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6", "20010DB800010002/64")]
    [InlineData("not-an-ip", "not-an-ip")]
    public void Reporters_are_grouped_by_network(string ip, string network)
    {
        Assert.Equal(network, KeyedHasher.NetworkOf(ip));
    }
}
