using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Aldanma.Api.Features.Notifications;

public sealed record Email(string Subject, string Text);

public interface IEmailSender
{
    /// <returns>False when the mail could not be handed over; the caller decides whether to try again.</returns>
    Task<bool> SendAsync(Email email, CancellationToken ct);
}

/// <summary>Sends through the Resend HTTP API (https://resend.com/docs/api-reference/emails/send-email).</summary>
public sealed class ResendEmailSender(HttpClient http, IOptions<AldanmaOptions> options, ILogger<ResendEmailSender> log) : IEmailSender
{
    public const string Endpoint = "https://api.resend.com/emails";

    public async Task<bool> SendAsync(Email email, CancellationToken ct)
    {
        var o = options.Value.Notify;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new { from = o.From, to = new[] { o.To }, subject = email.Subject, text = email.Text }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", o.ResendApiKey);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return true;
            // The body names the problem (bad key, unverified domain) and never echoes the key.
            var body = await response.Content.ReadAsStringAsync(ct);
            log.LogWarning("Resend refused the mail: {Status} {Body}", (int)response.StatusCode, body.Length > 300 ? body[..300] : body);
            return false;
        }
        catch (Exception e) when (e is HttpRequestException || (e is TaskCanceledException && !ct.IsCancellationRequested))
        {
            log.LogWarning(e, "Resend could not be reached");
            return false;
        }
    }
}
