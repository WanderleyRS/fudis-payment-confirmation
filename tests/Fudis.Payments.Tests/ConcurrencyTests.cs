using System.Net;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fudis.Payments.Tests;

[Collection("api")]
public class ConcurrencyTests : ApiTestBase
{
    public ConcurrencyTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task ConcurrentDeliveriesOfTheSameEvent_CreateExactlyOneDriverEarning()
    {
        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => PostPaymentAsync(notification)));

        Assert.All(responses, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(1, state.Inbox);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task ConcurrentDeliveriesOfDifferentEventsForTheSameOrder_CreateExactlyOneDriverEarning()
    {
        var order = await SeedOrderAsync();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 6).Select(i => PostPaymentAsync(BuildNotification(order.Id, 55.00m, providerEventId: $"evt_{i}"))));

        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.All(
            responses.Where(response => response.StatusCode != HttpStatusCode.OK),
            response => Assert.Equal(HttpStatusCode.Conflict, response.StatusCode));

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(1, state.Inbox);
        Assert.Equal(1, state.Outbox);
    }

    [Fact]
    public async Task ConcurrentDeliveries_NeverReturnServerError()
    {
        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => PostPaymentAsync(notification)));

        Assert.All(responses, response => Assert.True(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict,
            $"Unexpected status code {response.StatusCode}"));
    }

    /// <summary>
    /// Reproduces the original defect on the original schema: without the unique index, two deliveries of the
    /// same payment both pass the "is it already PAID?" check and both credit the driver.
    /// </summary>
    [Fact]
    public async Task WithoutTheUniqueIndex_TheOriginalSchemaPaysTheDriverTwice()
    {
        var order = await SeedOrderAsync();

        // The check-then-act window: two concurrent readers both see the order as pending.
        await using (var readerA = Factory.CreateDbContext())
        await using (var readerB = Factory.CreateDbContext())
        {
            var orderA = await readerA.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);
            var orderB = await readerB.Orders.AsNoTracking().FirstAsync(o => o.Id == order.Id);

            Assert.Equal(OrderStatus.PendingPayment, orderA.Status);
            Assert.Equal(OrderStatus.PendingPayment, orderB.Status);
        }

        await using (var db = Factory.CreateDbContext())
        {
            const string insert = "INSERT INTO DriverEarnings (OrderId, DriverId, Amount, Currency, CreatedAt) VALUES ({0}, 42, 21.00, 'BRL', SYSUTCDATETIME());";

            // First delivery: accepted.
            await db.Database.ExecuteSqlRawAsync(insert, order.Id);

            // Second delivery of the same payment: the new schema blocks it at the database level.
            await Assert.ThrowsAnyAsync<Exception>(() => db.Database.ExecuteSqlRawAsync(insert, order.Id));

            await db.Database.ExecuteSqlRawAsync("DROP INDEX [IX_DriverEarnings_OrderId] ON [DriverEarnings];");

            try
            {
                // The original schema accepts it: the driver is credited twice for one order.
                await db.Database.ExecuteSqlRawAsync(insert, order.Id);

                var state = await ReadStateAsync(order.Id);
                Assert.Equal(2, state.Earnings);
            }
            finally
            {
                // The duplicated rows have to go before the unique index can be recreated.
                await db.Database.ExecuteSqlRawAsync("DELETE FROM DriverEarnings WHERE OrderId = {0};", order.Id);
                await db.Database.ExecuteSqlRawAsync("CREATE UNIQUE INDEX [IX_DriverEarnings_OrderId] ON [DriverEarnings] ([OrderId]);");
            }
        }
    }
}
