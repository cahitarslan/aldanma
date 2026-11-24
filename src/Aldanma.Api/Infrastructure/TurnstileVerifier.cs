using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Infrastructure;

public interface IHumanCheck
{
    Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct);
}

/// <summary>Cloudflare Turnstile server-side verification. Without a secret every request passes (development).</summary>
public sealed class TurnstileVerifier(HttpClient http, IOptions<AldanmaOptions> options) : IHumanCheck
{
    public async Task<bool> VerifyAsync(string? token, string? remoteIp, CancellationToken ct)
    {
        var secret = options.Value.TurnstileSecret;
        if (string.IsNullOrEmpty(secret)) return true;
        if (string.IsNullOrEmpty(token)) return false;
        var form = new Dictionary<string, string> { ["secret"] = secret, ["response"] = token };
        if (remoteIp is not null) form["remoteip"] = remoteIp;
        try
        {
            using var response = await http.PostAsync("https://challenges.cloudflare.com/turnstile/v0/siteverify", new FormUrlEncodedContent(form), ct);
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch (Exception e) when (e is HttpRequestException or JsonException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            // Cloudflare unreachable or slow: fail closed, the user can retry.
            return false;
        }
    }
}
