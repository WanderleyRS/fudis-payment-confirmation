using System.Net;
using Fudis.Payments.Domain;
using Fudis.Payments.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fudis.Payments.Tests;

[Collection("api")]
public class OutboxDispatcherTests : ApiTestBase
{
    public OutboxDispatcherTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task TwoDispatchersRunningAtTheSameTime_DeliverTheNotificationOnlyOnce()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        // Two independent dispatcher instances (two scopes) draining the same outbox at the same time.
        using var scopeA = Factory.Services.CreateScope();
        using var scopeB = Factory.Services.CreateScope();

        var dispatcherA = scopeA.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        var dispatcherB = scopeB.ServiceProvider.GetRequiredService<OutboxDispatcher>();

        await Task.WhenAll(dispatcherA.DispatchPendingAsync(), dispatcherB.DispatchPendingAsync());

        // The row is claimed atomically, so the restaurant is notified once, not twice.
        Assert.Single(Factory.Notifier.Calls);

        await using var db = Factory.CreateDbContext();
        var row = await db.RestaurantNotificationOutbox.AsNoTracking().FirstAsync();

        Assert.Equal(OutboxStatus.Delivered, row.Status);
    }

    [Fact]
    public async Task Dispatcher_DeliversPendingNotification_AndMarksItDelivered()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        var delivered = await DispatchAsync();

        Assert.Equal(1, delivered);
        Assert.Equal(new[] { order.Id }, Factory.Notifier.Calls);

        await using var db = Factory.CreateDbContext();
        var row = await db.RestaurantNotificationOutbox.AsNoTracking().FirstAsync();

        Assert.Equal(OutboxStatus.Delivered, row.Status);
        Assert.NotNull(row.DeliveredAt);
        Assert.Equal(0, row.Attempts);
    }

    [Fact]
    public async Task Dispatcher_WhenTheRestaurantServiceFails_KeepsThePaymentApplied_AndSchedulesARetry()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Factory.Notifier.FailNext(1);

        var delivered = await DispatchAsync();

        Assert.Equal(0, delivered);

        var state = await ReadStateAsync(order.Id);
        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings); // the money movement is not affected by the notification outage

        await using var db = Factory.CreateDbContext();
        var row = await db.RestaurantNotificationOutbox.AsNoTracking().FirstAsync();

        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(1, row.Attempts);
        Assert.NotNull(row.LastError);
        Assert.True(row.NextAttemptAt > DateTimeOffset.UtcNow, "the retry must be scheduled in the future");
    }

    [Fact]
    public async Task Dispatcher_DeliversOnRetry_AfterATransientFailure()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Factory.Notifier.FailNext(1);
        await DispatchAsync();

        // Simulate the backoff window elapsing.
        await using (var db = Factory.CreateDbContext())
        {
            await db.RestaurantNotificationOutbox
                .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        var delivered = await DispatchAsync();

        Assert.Equal(1, delivered);
        Assert.Equal(2, Factory.Notifier.Calls.Count);

        await using var check = Factory.CreateDbContext();
        var row = await check.RestaurantNotificationOutbox.AsNoTracking().FirstAsync();

        Assert.Equal(OutboxStatus.Delivered, row.Status);
    }

    [Fact]
    public async Task NewEndpoint_QueuesTheNotification_InsteadOfCallingTheRestaurantInline()
    {
        var order = await SeedOrderAsync();

        var response = await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Factory.Notifier.Calls); // the request path does not depend on the restaurant service

        var state = await ReadStateAsync(order.Id);
        Assert.Equal(1, state.Outbox);
    }

    private async Task<int> DispatchAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<OutboxDispatcher>();
        return await dispatcher.DispatchPendingAsync();
    }
}
