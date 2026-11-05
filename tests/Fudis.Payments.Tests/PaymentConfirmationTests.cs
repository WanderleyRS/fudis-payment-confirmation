using System.Net;
using Fudis.Payments.Domain;
using Xunit;

namespace Fudis.Payments.Tests;

[Collection("api")]
public class PaymentConfirmationTests : ApiTestBase
{
    public PaymentConfirmationTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task ConfirmedPayment_AppliesStatusEarningInboxAndOutbox_Together()
    {
        var order = await SeedOrderAsync(deliveryFee: 30.00m, totalAmount: 55.00m);
        var notification = BuildNotification(order.Id, 55.00m);

        var response = await PostPaymentAsync(notification);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("processed", (await ReadJsonAsync(response)).GetProperty("status").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(21.00m, state.EarningAmount); // 70% of the 30.00 delivery fee
        Assert.Equal(1, state.Inbox);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task DriverEarning_IsSeventyPercent_RoundedAwayFromZero()
    {
        var order = await SeedOrderAsync(deliveryFee: 10.05m, totalAmount: 55.00m);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        // 10.05 * 0.70 = 7.035 -> 7.04 (MidpointRounding.AwayFromZero)
        Assert.Equal(7.04m, state.EarningAmount);
    }

    [Fact]
    public async Task SameGatewayEventDeliveredTwice_DoesNotPayTheDriverTwice()
    {
        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);

        var first = await PostPaymentAsync(notification);
        var second = await PostPaymentAsync(notification);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal("duplicate", (await ReadJsonAsync(second)).GetProperty("status").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(1, state.Inbox);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task ForgedSignature_IsRejected_AndChangesNothing()
    {
        var order = await SeedOrderAsync();

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m), signatureOverride: new string('a', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
        Assert.Equal(0, state.Inbox);
        Assert.Equal(0, state.Outbox);
    }

    [Fact]
    public async Task SignatureSignedWithAnotherSecret_IsRejected_AndChangesNothing()
    {
        var order = await SeedOrderAsync();

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m), sharedSecret: "attacker-secret");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task StaleTimestamp_IsRejected_AndChangesNothing()
    {
        var order = await SeedOrderAsync();

        var response = await PostPaymentAsync(
            BuildNotification(order.Id, 55.00m),
            timestamp: DateTimeOffset.UtcNow.AddMinutes(-30));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task AmountMismatch_IsRejected_AndNothingIsApplied()
    {
        var order = await SeedOrderAsync(totalAmount: 55.00m);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 40.00m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("AMOUNT_MISMATCH", (await ReadJsonAsync(response)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
        Assert.Equal(0, state.Inbox);
    }

    [Fact]
    public async Task CurrencyMismatch_IsRejected_AndNothingIsApplied()
    {
        var order = await SeedOrderAsync(currency: "BRL");

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m, currency: "USD"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("CURRENCY_MISMATCH", (await ReadJsonAsync(response)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task OrderThatIsNotPayable_IsRejected_AndNothingIsApplied()
    {
        var order = await SeedOrderAsync(status: OrderStatus.Cancelled);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("ORDER_NOT_PAYABLE", (await ReadJsonAsync(response)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Cancelled, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task OrderWithoutAssignedDriver_IsRejected_AndNoEarningIsCreated()
    {
        var order = await SeedOrderAsync(driverId: 0);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("DRIVER_NOT_ASSIGNED", (await ReadJsonAsync(response)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task SecondGatewayEventForAnAlreadyPaidOrder_IsRejectedAsDoubleCharge()
    {
        var order = await SeedOrderAsync();

        var first = await PostPaymentAsync(BuildNotification(order.Id, 55.00m, providerEventId: "evt_1"));
        var second = await PostPaymentAsync(BuildNotification(order.Id, 55.00m, providerEventId: "evt_2"));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("ORDER_ALREADY_PAID", (await ReadJsonAsync(second)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(1, state.Earnings);
        Assert.Equal(1, state.Inbox);
    }

    [Fact]
    public async Task SameGatewayEventIdForADifferentOrder_IsRejected_AndTheSecondOrderIsNotLeftUnpaid()
    {
        var first = await SeedOrderAsync();
        var second = await SeedOrderAsync();

        var applied = await PostPaymentAsync(BuildNotification(first.Id, 55.00m, providerEventId: "evt_shared"));
        var conflicting = await PostPaymentAsync(BuildNotification(second.Id, 55.00m, providerEventId: "evt_shared"));

        Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, conflicting.StatusCode);
        Assert.Equal("EVENT_ID_CONFLICT", (await ReadJsonAsync(conflicting)).GetProperty("code").GetString());

        // The second order must NOT be silently left unpaid while the gateway is told "duplicate".
        var state = await ReadStateAsync(second.Id);
        Assert.Equal(OrderStatus.PendingPayment, state.Status);
        Assert.Equal(0, state.Earnings);
    }

    [Fact]
    public async Task UnknownOrder_ReturnsNotFound()
    {
        var response = await PostPaymentAsync(BuildNotification(orderId: 999_999, amount: 55.00m));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("ORDER_NOT_FOUND", (await ReadJsonAsync(response)).GetProperty("code").GetString());
    }
}
