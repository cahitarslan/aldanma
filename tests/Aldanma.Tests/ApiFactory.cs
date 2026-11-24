using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Aldanma.Api.Data;
using Aldanma.Api.Infrastructure;
using Aldanma.Core.Scoring;

namespace Aldanma.Tests;

/// <summary>
/// Runs only when ALDANMA_TEST_DATABASE points at a PostgreSQL server the tests may create databases on,
/// e.g. "Host=localhost;Username=aldanma;Password=dev-password". Each test class gets a fresh database.
/// </summary>
public sealed class DatabaseFactAttribute : FactAttribute
{
    public static readonly string? ConnectionString = Environment.GetEnvironmentVariable("ALDANMA_TEST_DATABASE");

    public DatabaseFactAttribute()
    {
        if (string.IsNullOrEmpty(ConnectionString)) Skip = "Set ALDANMA_TEST_DATABASE to run database tests.";
    }
}

public sealed class FakeRegistrations : IDomainRegistrationLookup
{
    public Dictionary<string, DomainRegistration> Known { get; } = new(StringComparer.Ordinal);

    public Task<DomainRegistration?> GetAsync(string domain, CancellationToken ct) =>
        Task.FromResult(Known.TryGetValue(domain, out var r) ? r : null);
}

public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _database = "aldanma_test_" + Guid.NewGuid().ToString("N")[..12];
    public FakeRegistrations Registrations { get; } = new();

    private string ConnectionString => new NpgsqlConnectionStringBuilder(DatabaseFactAttribute.ConnectionString) { Database = _database }.ConnectionString;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Database", ConnectionString);
        builder.UseSetting("Aldanma:HashKey", "test-key");
        builder.UseSetting("Aldanma:AdminKey", "test-admin");
        builder.UseSetting("Aldanma:Usom:Enabled", "false");
        builder.UseSetting("Aldanma:ClientIpHeader", "CF-Connecting-IP");
        builder.ConfigureServices(s =>
        {
            s.RemoveAll<IDomainRegistrationLookup>();
            s.AddSingleton<IDomainRegistrationLookup>(Registrations);
            s.RemoveAll<IHostedService>();
        });
    }

    public async Task<T> WithDb<T>(Func<AppDbContext, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await DisposeAsync();
        if (DatabaseFactAttribute.ConnectionString is null) return;
        NpgsqlConnection.ClearAllPools();
        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(DatabaseFactAttribute.ConnectionString) { Database = "postgres" }.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", conn);
        await cmd.ExecuteNonQueryAsync();
    }
}
