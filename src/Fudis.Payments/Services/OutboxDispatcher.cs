using System.Text.Json;
using Fudis.Payments.Domain;
using Fudis.Payments.Infra;
using Microsoft.EntityFrameworkCore;

namespace Fudis.Payments.Services;

/// <summary>
/// Delivers pending outbox rows to the restaurant service. Guarantees at-least-once delivery: a failed
/// notification is retried with exponential backoff and is never lost, because the row was committed together
/// with the payment.
/// </summary>
public class OutboxDispatcher
{
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromMinutes(5);

    private readonly FudisDbContext _db;
    private readonly INotificationService _notifier;
    private readonly TimeProvider _clock;
    private readonly ILogger<OutboxDispatcher> _logger;

    public OutboxDispatcher(
        FudisDbContext db,
        INotificationService notifier,
        TimeProvider clock,
        ILogger<OutboxDispatcher> logger)
    {
        _db = db;
        _notifier = notifier;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int> DispatchPendingAsync(int batchSize = 50, CancellationToken cancellationToken = default)
    {
        var now = _clock.GetUtcNow();

        // Recover rows that were claimed by a dispatcher instance that died mid-flight.
        await _db.RestaurantNotificationOutbox
            .Where(o => o.Status == OutboxStatus.Processing && o.ProcessingStartedAt < now - ClaimTimeout)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OutboxStatus.Pending), cancellationToken);

        var candidateIds = await _db.RestaurantNotificationOutbox
            .Where(o => o.Status == OutboxStatus.Pending && o.NextAttemptAt <= now)
            .OrderBy(o => o.Id)
            .Take(batchSize)
            .Select(o => o.Id)
            .ToListAsync(cancellationToken);

        var delivered = 0;

        foreach (var id in candidateIds)
        {
            // Atomic claim, one row at a time: only the dispatcher whose UPDATE actually flips the row from
            // Pending to Processing is allowed to deliver it. A batch claim would let two dispatcher
            // instances select the same ids and both deliver them.
            var claimed = await _db.RestaurantNotificationOutbox
                .Where(o => o.Id == id && o.Status == OutboxStatus.Pending)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(o => o.Status, OutboxStatus.Processing)
                          .SetProperty(o => o.ProcessingStartedAt, now),
                    cancellationToken);

            if (claimed == 0)
            {
                continue; // another dispatcher instance claimed this row first
            }

            _db.ChangeTracker.Clear();

            var row = await _db.RestaurantNotificationOutbox.FirstAsync(o => o.Id == id, cancellationToken);

            try
            {
                await _notifier.NotifyRestaurant(row.OrderId, cancellationToken);
                row.Status = OutboxStatus.Delivered;
                row.DeliveredAt = now;
                row.LastError = null;
                delivered++;
            }
            catch (Exception ex)
            {
                row.Status = OutboxStatus.Pending;
                row.Attempts += 1;
                row.NextAttemptAt = now + Backoff(row.Attempts);
                row.LastError = ex.Message;
                _logger.LogWarning(ex, "Restaurant notification failed for order {OrderId}, attempt {Attempt}", row.OrderId, row.Attempts);
            }

            await _db.SaveChangesAsync(cancellationToken);
        }

        return delivered;
    }

    private static TimeSpan Backoff(int attempts)
        => TimeSpan.FromSeconds(Math.Min(300, Math.Pow(2, Math.Min(attempts, 8))));
}
