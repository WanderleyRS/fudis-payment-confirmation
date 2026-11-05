using System.Net;
using System.Net.Http.Json;
using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Xunit;

namespace Fudis.Payments.Tests;

/// <summary>
/// Baseline behaviour of the original fragment, kept executable so the differences are demonstrable
/// instead of only described.
/// </summary>
[Collection("api")]
public class LegacyEndpointTests : ApiTestBase
{
    public LegacyEndpointTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task LegacyEndpoint_AppliesThePaymentAndCreatesTheEarning()
    {
        var order = await SeedOrderAsync();

        var response = await Client.PostAsJsonAsync(
            "/api/legacy/payments/confirm-payment",
            new LegacyPaymentNotification { OrderId = order.Id });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(21.00m, state.EarningAmount);
        Assert.Equal(new[] { order.Id }, Factory.Notifier.Calls);
        Assert.Equal(0, state.Outbox); // nothing is queued: the notification is sent inline
    }

    [Fact]
    public async Task LegacyEndpoint_WhenTheRestaurantServiceFails_LosesTheNotification_AndFailsTheRequest()
    {
        var order = await SeedOrderAsync();
        Factory.Notifier.FailNext(1);

        var statusCode = await PostLegacyAsync(order.Id);

        Assert.Equal(HttpStatusCode.InternalServerError, statusCode);

        var state = await ReadStateAsync(order.Id);

        // The money is already applied, but the restaurant was never notified and nothing was queued to retry.
        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(0, state.Outbox);
    }

    [Fact]
    public async Task NewEndpoint_WhenTheRestaurantServiceFails_KeepsThePaymentAndQueuesTheNotification()
    {
        var order = await SeedOrderAsync();
        Factory.Notifier.FailNext(1);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(1, state.Outbox); // the notification survives the outage and will be retried
    }

    private async Task<HttpStatusCode> PostLegacyAsync(int orderId)
    {
        try
        {
            var response = await Client.PostAsJsonAsync(
                "/api/legacy/payments/confirm-payment",
                new LegacyPaymentNotification { OrderId = orderId });

            return response.StatusCode;
        }
        catch (Exception)
        {
            // The test server surfaces the unhandled exception instead of the 500 response.
            return HttpStatusCode.InternalServerError;
        }
    }
}
