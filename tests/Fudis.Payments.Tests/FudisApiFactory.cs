using Fudis.Payments.Infra;
using Fudis.Payments.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fudis.Payments.Tests;

/// <summary>
/// Boots the real application (real controllers, real DI, real EF Core) against a real SQL Server instance
/// (LocalDB). The outbox background worker is disabled so the tests drive delivery explicitly, and the
/// notification service is replaced by a controllable fake.
/// </summary>
public class FudisApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string SharedSecret = "integration-test-secret";

    /// <summary>
    /// Defaults to SQL Server LocalDB (Windows). Set FUDIS_TEST_CONNECTION to run the same suite against any
    /// SQL Server, which is what the CI workflow does on ubuntu-latest.
    /// </summary>
    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable("FUDIS_TEST_CONNECTION")
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=FudisPaymentsTests;Trusted_Connection=True;MultipleActiveResultSets=True";

    public FakeNotificationService Notifier { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Fudis"] = ConnectionString,
                ["Gateway:SharedSecret"] = SharedSecret,
                ["Gateway:TimestampToleranceMinutes"] = "15",
                ["Earnings:DriverShareRate"] = "0.70",
                ["Outbox:MaxAttempts"] = "3",
                ["Restaurant:BaseUrl"] = "http://localhost:5080/"
            }));

        builder.ConfigureServices(services =>
        {
            var dbDescriptors = services.Where(d =>
                d.ServiceType == typeof(DbContextOptions<FudisDbContext>) ||
                d.ServiceType == typeof(DbContextOptions) ||
                d.ServiceType == typeof(FudisDbContext)).ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<FudisDbContext>(options => options.UseSqlServer(ConnectionString));

            var notifierDescriptors = services.Where(d => d.ServiceType == typeof(INotificationService)).ToList();
            foreach (var descriptor in notifierDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddSingleton<INotificationService>(Notifier);

            var hostedDescriptors = services
                .Where(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(OutboxDispatcherHostedService))
                .ToList();

            foreach (var descriptor in hostedDescriptors)
            {
                services.Remove(descriptor);
            }
        });
    }

    public FudisDbContext CreateDbContext() =>
        new(new DbContextOptionsBuilder<FudisDbContext>().UseSqlServer(ConnectionString).Options);

    public async Task InitializeAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.EnsureDeletedAsync();
        await db.Database.MigrateAsync();
    }

    async Task IAsyncLifetime.DisposeAsync() => await base.DisposeAsync();

    public async Task ResetAsync()
    {
        await using var db = CreateDbContext();
        await db.Database.ExecuteSqlRawAsync(
            "DELETE FROM PaymentEventLog; DELETE FROM PaymentInbox; DELETE FROM RestaurantNotificationOutbox; DELETE FROM DriverEarnings; DELETE FROM Orders;");

        Notifier.Reset();
    }
}
