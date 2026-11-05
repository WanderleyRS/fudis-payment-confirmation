using System.Net.Http.Json;
using System.Text.Json;
using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fudis.Payments.Tests;

[CollectionDefinition("api")]
public class ApiCollection : ICollectionFixture<FudisApiFactory> { }

[Collection("api")]
public abstract class ApiTestBase : IAsyncLifetime
{
    protected FudisApiFactory Factory { get; }

    protected HttpClient Client { get; private set; } = default!;

    protected ApiTestBase(FudisApiFactory factory) => Factory = factory;

    public async Task InitializeAsync()
    {
        await Factory.ResetAsync();
        Client = Factory.CreateClient();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    protected async Task<Order> SeedOrderAsync(
        OrderStatus status = OrderStatus.PendingPayment,
        int driverId = 42,
        decimal deliveryFee = 30.00m,
        decimal totalAmount = 55.00m,
        string currency = "BRL")
    {
        await using var db = Factory.CreateDbContext();

        var order = new Order
        {
            Status = status,
            DriverId = driverId,
            DeliveryFee = deliveryFee,
            TotalAmount = totalAmount,
            Currency = currency
        };

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    protected static PaymentNotification BuildNotification(
        int orderId,
        decimal amount,
        string currency = "BRL",
        string providerEventId = "evt_1",
        string provider = "stripe",
        DateTimeOffset? paidAt = null) =>
        new()
        {
            Provider = provider,
            ProviderEventId = providerEventId,
            OrderId = orderId,
            Amount = amount,
            Currency = currency,
            PaidAt = paidAt ?? new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero)
        };

    protected async Task<HttpResponseMessage> PostPaymentAsync(
        PaymentNotification notification,
        DateTimeOffset? timestamp = null,
        string? signatureOverride = null,
        string sharedSecret = FudisApiFactory.SharedSecret)
    {
        var signedAt = timestamp ?? DateTimeOffset.UtcNow;
        var signature = signatureOverride ?? GatewaySigner.Sign(sharedSecret, notification, signedAt);

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/payments/confirm-payment")
        {
            Content = JsonContent.Create(notification)
        };

        request.Headers.Add("X-Fudis-Signature", signature);
        request.Headers.Add("X-Fudis-Timestamp", signedAt.ToString("O"));

        return await Client.SendAsync(request);
    }

    protected static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        return JsonDocument.Parse(payload).RootElement;
    }

    protected async Task<(OrderStatus Status, int Earnings, decimal EarningAmount, int Inbox, int Outbox)> ReadStateAsync(int orderId)
    {
        await using var db = Factory.CreateDbContext();

        var order = await db.Orders.AsNoTracking().FirstAsync(o => o.Id == orderId);
        var earnings = await db.DriverEarnings.AsNoTracking().Where(e => e.OrderId == orderId).ToListAsync();

        return (
            order.Status,
            earnings.Count,
            earnings.Sum(e => e.Amount),
            await db.PaymentInbox.AsNoTracking().CountAsync(i => i.OrderId == orderId),
            await db.RestaurantNotificationOutbox.AsNoTracking().CountAsync(o => o.OrderId == orderId));
    }
}
