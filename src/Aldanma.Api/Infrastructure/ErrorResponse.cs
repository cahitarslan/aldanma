namespace Aldanma.Api.Infrastructure;

/// <summary>The single error shape of the API. <see cref="Error"/> is a Turkish sentence the site shows as is.</summary>
public sealed record ErrorResponse(string Error, string? TraceId = null)
{
    public const string HumanCheckFailed = "İnsan doğrulaması tamamlanmadı. Sayfayı yenileyip tekrar dene.";
    public const string TooManyRequests = "Çok sık istek gönderdin. Biraz bekleyip tekrar dene.";
    public const string Unexpected = "Beklenmeyen bir hata oldu. Biraz sonra tekrar dene.";
    public const string InvalidRequest = "İstek okunamadı.";
    public const string Timeout = "İstek çok uzun sürdü. Biraz sonra tekrar dene.";
}
