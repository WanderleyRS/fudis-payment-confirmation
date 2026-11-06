using System.Net;
using System.Net.Http.Json;
using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Fudis.Payments.Tests;

/// <summary>
/// The legacy endpoint is the original implementation: not authenticated, not idempotent under concurrency and
/// able to lose the restaurant notification. It must never be reachable in a deployed environment.
/// </summary>
[Collection("api")]
public class LegacyEndpointExposureTests : ApiTestBase
{
    public LegacyEndpointExposureTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task LegacyEndpoint_IsNotAvailableOutsideDevelopmentAndTesting()
    {
        using var productionFactory = new ProductionApiFactory();
        using var productionClient = productionFactory.CreateClient();

        var response = await productionClient.PostAsJsonAsync(
            "/api/legacy/payments/confirm-payment",
            new LegacyPaymentNotification { OrderId = 1 });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task NewEndpoint_IsAvailableInEveryEnvironment()
    {
        using var productionFactory = new ProductionApiFactory();
        using var productionClient = productionFactory.CreateClient();

        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);
        var signedAt = DateTimeOffset.UtcNow;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/confirm-payment")
        {
            Content = JsonContent.Create(notification)
        };

        request.Headers.Add("X-Fudis-Signature", GatewaySigner.Sign(FudisApiFactory.SharedSecret, notification, signedAt));
        request.Headers.Add("X-Fudis-Timestamp", signedAt.ToString("O"));

        var response = await productionClient.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

/// <summary>Boots the same application in a Production environment, against the same database.</summary>
public class ProductionApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Fudis"] = "Server=(localdb)\\MSSQLLocalDB;Database=FudisPaymentsTests;Trusted_Connection=True;MultipleActiveResultSets=True",
                ["Gateway:SharedSecret"] = FudisApiFactory.SharedSecret,
                ["Gateway:TimestampToleranceMinutes"] = "15",
                ["Earnings:DriverShareRate"] = "0.70",
                ["Outbox:MaxAttempts"] = "3",
                ["Restaurant:BaseUrl"] = "http://localhost:5080/"
            }));

        builder.ConfigureServices(services =>
        {
            var dbDescriptors = services.Where(d =>
                d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions<Fudis.Payments.Infra.FudisDbContext>) ||
                d.ServiceType == typeof(Microsoft.EntityFrameworkCore.DbContextOptions) ||
                d.ServiceType == typeof(Fudis.Payments.Infra.FudisDbContext)).ToList();

            foreach (var descriptor in dbDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<Fudis.Payments.Infra.FudisDbContext>(options =>
                options.UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=FudisPaymentsTests;Trusted_Connection=True;MultipleActiveResultSets=True"));
        });
    }
}
