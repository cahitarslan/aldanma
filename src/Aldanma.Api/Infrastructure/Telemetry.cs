using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Aldanma.Api.Infrastructure;

/// <summary>
/// Logs, traces and metrics. Logs are structured (JSON outside Development). Traces and metrics go to any
/// OpenTelemetry collector when OTEL_EXPORTER_OTLP_ENDPOINT is set, and cost nothing when it is not.
/// </summary>
public static class Telemetry
{
    public static WebApplicationBuilder AddAldanmaTelemetry(this WebApplicationBuilder builder)
    {
        if (!builder.Environment.IsDevelopment())
        {
            builder.Logging.ClearProviders();
            builder.Logging.AddJsonConsole(o =>
            {
                o.IncludeScopes = true;
                o.UseUtcTimestamp = true;
            });
        }

        builder.Services.AddSingleton<AldanmaMetrics>();

        var otlp = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);
        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("aldanma-api", serviceVersion: typeof(Telemetry).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                // Health probes would drown real traffic.
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health"))
                .AddHttpClientInstrumentation()
                .AddNpgsql())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddMeter(AldanmaMetrics.MeterName, "Npgsql"));
        if (otlp) otel.UseOtlpExporter();
        return builder;
    }
}
