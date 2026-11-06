using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Fudis.Payments.Tests;

[Collection("api")]
public class OutboxDeadLetterTests : ApiTestBase
{
    public OutboxDeadLetterTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task Notification_MovesToTheDeadLetterState_WhenTheRetryBudgetIsExhausted()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        // The test host configures Outbox:MaxAttempts = 3.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Factory.Notifier.FailNext(1);
            await DispatchAsync();

            await using (var scope = Factory.CreateDbContext())
            {
                await scope.RestaurantNotificationOutbox
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
        }

        await using var db = Factory.CreateDbContext();
        var row = await db.RestaurantNotificationOutbox.AsNoTracking().FirstAsync();

        Assert.Equal(OutboxStatus.Failed, row.Status);
        Assert.Equal(3, row.Attempts);

        // A failed notification is terminal: the dispatcher must not pick it up again.
        var delivered = await DispatchAsync();
        Assert.Equal(0, delivered);
        Assert.Equal(3, Factory.Notifier.Calls.Count);
    }

    [Fact]
    public async Task DeadLetteredNotification_DoesNotAffectThePayment()
    {
        var order = await SeedOrderAsync();
        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            Factory.Notifier.FailNext(1);
            await DispatchAsync();

            await using (var scope = Factory.CreateDbContext())
            {
                await scope.RestaurantNotificationOutbox
                    .ExecuteUpdateAsync(s => s.SetProperty(o => o.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            }
        }

        var state = await ReadStateAsync(order.Id);

        Assert.Equal(OrderStatus.Paid, state.Status);
        Assert.Equal(1, state.Earnings);
    }

    private async Task<int> DispatchAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var dispatcher = scope.ServiceProvider.GetRequiredService<Services.OutboxDispatcher>();
        return await dispatcher.DispatchPendingAsync();
    }
}
