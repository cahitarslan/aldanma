using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Aldanma.Api.Features.Blocklist;
using Aldanma.Api.Features.Check;
using Aldanma.Api.Features.Disputes;
using Aldanma.Api.Features.Feedback;
using Aldanma.Api.Features.Meta;
using Aldanma.Api.Features.Moderation;
using Aldanma.Api.Features.Reports;
using Aldanma.Api.Features.Submissions;

var builder = WebApplication.CreateBuilder(args);
var options = builder.Configuration.GetSection(AldanmaOptions.Section).Get<AldanmaOptions>() ?? new AldanmaOptions();
builder.Services.AddOptions<AldanmaOptions>()
    .Bind(builder.Configuration.GetSection(AldanmaOptions.Section))
    .Validate(o => o.HashKey.Length >= 32 || builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing"),
        "Aldanma:HashKey must be set (32+ characters) outside Development.")
    .Validate(o => o.Limits.IsValid(), "Aldanma:Limits has a value out of range (see LimitOptions).")
    .Validate(o => o.Rdap.IsValid(), "Aldanma:Rdap has a value out of range (see RdapOptions).")
    .ValidateOnStart();

// Connection-level limits. Requests are small JSON documents; anything bigger or slower is a mistake or an attack.
// MinRequestBodyDataRate keeps its default (240 bytes/s after a 5 s grace), which already drops slow-body senders.
builder.WebHost.ConfigureKestrel(k =>
{
    var l = options.Limits;
    k.Limits.MaxRequestBodySize = 64 * 1024;
    k.Limits.MaxConcurrentConnections = l.MaxConcurrentConnections;
    k.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(l.RequestHeadersTimeoutSeconds);
    k.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(l.KeepAliveTimeoutSeconds);
    k.Limits.MaxRequestHeadersTotalSize = l.MaxRequestHeadersTotalSizeKb * 1024;
    k.Limits.MaxRequestLineSize = l.MaxRequestLineSizeKb * 1024;
});

builder.AddAldanmaTelemetry();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
// Unreadable request bodies are thrown to ApiExceptionHandler, so they get the same error shape as everything else.
builder.Services.Configure<RouteHandlerOptions>(o => o.ThrowOnBadRequest = true);
builder.Services.AddProblemDetails();

builder.Services.AddDbContext<AppDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("Database")));
builder.Services.AddHealthChecks().AddDbContextCheck<AppDbContext>(tags: ["ready"]);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache();
builder.Services.AddOutputCache();

// Domain: the pure engine objects from Aldanma.Core. Stateless and thread-safe.
var engine = MessageEngine.Default();
var identifierRules = IdentifierRules.Embedded();
builder.Services.AddSingleton(engine);
builder.Services.AddSingleton(identifierRules);
builder.Services.AddSingleton(new InputParser(engine, identifierRules));
builder.Services.AddSingleton(new RiskScorer(engine, identifierRules));

// Infrastructure shared by the features.
builder.Services.AddSingleton<KeyedHasher>();
builder.Services.AddSingleton<ClientAddress>();
builder.Services.AddScoped<IdentifierStore>();
builder.Services.AddSingleton<RdapThrottle>();
builder.Services.AddScoped<RdapBudget>();
builder.Services.AddHttpClient<IDomainRegistrationLookup, RdapClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Aldanma/0.5 (+https://github.com/cahitarslan/aldanma)"));
builder.Services.AddHttpClient<IHumanCheck, TurnstileVerifier>(c => c.Timeout = TimeSpan.FromSeconds(10));

// Features.
builder.Services.AddScoped<ReputationService>();
builder.Services.AddScoped<ReportService>();
builder.Services.AddScoped<DisputeService>();
builder.Services.AddScoped<FeedbackService>();
builder.Services.AddScoped<SubmissionService>();
builder.Services.AddScoped<BlocklistImporter>();
builder.Services.AddHttpClient(nameof(UsomFeedService), c => c.Timeout = TimeSpan.FromMinutes(2));
builder.Services.AddHostedService<UsomFeedService>();

builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase);
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(options.AllowedOrigins).WithMethods("GET", "POST").WithHeaders("Content-Type")));
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    // Only the scheme is taken from the proxy. The client IP comes from ClientAddress, which trusts a header only when configured.
    o.ForwardedHeaders = ForwardedHeaders.XForwardedProto;
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Clear();
});
builder.Services.AddAldanmaRateLimits(options.Limits);
builder.Services.AddAldanmaRequestTimeouts(options.Limits);

var app = builder.Build();

if (options.MigrateOnStartup)
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
}

app.UseSecurityHeaders();
app.UseExceptionHandler();
app.UseForwardedHeaders();
app.UseCors();
app.UseRateLimiter();
app.UseRequestTimeouts();
app.UseOutputCache();

// Liveness answers without touching the database; readiness checks it.
app.MapHealthChecks("/health", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = c => c.Tags.Contains("ready") });

var api = app.MapGroup("/api");
api.MapCheckEndpoints();
api.MapReportEndpoints();
api.MapDisputeEndpoints();
api.MapMetaEndpoints();
api.MapFeedbackEndpoints();
api.MapSubmissionEndpoints();

var admin = api.MapAdminGroup();
admin.MapModerationEndpoints();
admin.MapFeedbackAdminEndpoints();
admin.MapSubmissionAdminEndpoints();

app.Run();

public partial class Program;
