using System.Text.Json;
using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Fudis.Payments.Infra;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Services;

public enum PaymentOutcome
{
    Processed,
    Duplicate,
    OrderNotFound,
    OrderAlreadyPaid,
    OrderNotPayable,
    AmountMismatch,
    CurrencyMismatch,
    InvalidAmount,
    EventIdConflict,
    EventPayloadMismatch,
    DriverNotAssigned,
    ConcurrencyConflict
}

/// <summary>Result of a payment confirmation attempt. <see cref="Code"/> is the stable, machine readable outcome.</summary>
public sealed record PaymentConfirmationResult(PaymentOutcome Outcome, int OrderId, string Code)
{
    public bool IsApplied => Outcome is PaymentOutcome.Processed or PaymentOutcome.Duplicate;
}

public interface IPaymentConfirmationService
{
    Task<PaymentConfirmationResult> ConfirmAsync(PaymentNotification notification, CancellationToken cancellationToken = default);

    /// <summary>Order id of the event if it was already applied, otherwise null.</summary>
    Task<int?> GetProcessedOrderIdAsync(string provider, string providerEventId, CancellationToken cancellationToken = default);
}

public class PaymentConfirmationService : IPaymentConfirmationService
{
    private readonly FudisDbContext _db;
    private readonly EarningsOptions _earnings;
    private readonly TimeProvider _clock;
    private readonly IPaymentEventLogWriter _eventLog;
    private readonly ILogger<PaymentConfirmationService> _logger;

    public PaymentConfirmationService(
        FudisDbContext db,
        IOptions<EarningsOptions> earnings,
        TimeProvider clock,
        IPaymentEventLogWriter eventLog,
        ILogger<PaymentConfirmationService> logger)
    {
        _db = db;
        _earnings = earnings.Value;
        _clock = clock;
        _eventLog = eventLog;
        _logger = logger;
    }

    public async Task<int?> GetProcessedOrderIdAsync(string provider, string providerEventId, CancellationToken cancellationToken = default)
        => await _db.PaymentInbox
               .Where(i => i.Provider == provider && i.ProviderEventId == providerEventId)
               .Select(i => (int?)i.OrderId)
               .FirstOrDefaultAsync(cancellationToken);

    /// <summary>The applied event, if any. Used to compare a replayed payload against the original one.</summary>
    public async Task<PaymentInbox?> GetProcessedEventAsync(string provider, string providerEventId, CancellationToken cancellationToken = default)
        => await _db.PaymentInbox
               .AsNoTracking()
               .FirstOrDefaultAsync(i => i.Provider == provider && i.ProviderEventId == providerEventId, cancellationToken);

    public async Task<PaymentConfirmationResult> ConfirmAsync(
        PaymentNotification notification,
        CancellationToken cancellationToken = default)
    {
        // 1. Idempotency at event level: the same gateway event applied twice is a no-op.
        var processedEvent = await GetProcessedEventAsync(notification.Provider, notification.ProviderEventId, cancellationToken);
        if (processedEvent is not null)
        {
            // The event id was already used. If it refers to another order the request is contradictory:
            // answering "duplicate" would silently leave a paid order unconfirmed.
            if (processedEvent.OrderId != notification.OrderId)
            {
                await LogRejectedAsync(notification, "EVENT_ID_CONFLICT", cancellationToken);
                return new PaymentConfirmationResult(PaymentOutcome.EventIdConflict, notification.OrderId, "EVENT_ID_CONFLICT");
            }

            // Same event, same order, but a different payload: the gateway is inconsistent or someone tampered
            // with it. Answering "duplicate" would hide a money discrepancy.
            if (processedEvent.Amount != notification.Amount ||
                !string.Equals(processedEvent.Currency, notification.Currency, StringComparison.OrdinalIgnoreCase))
            {
                await LogRejectedAsync(notification, "EVENT_PAYLOAD_MISMATCH", cancellationToken);
                return new PaymentConfirmationResult(PaymentOutcome.EventPayloadMismatch, notification.OrderId, "EVENT_PAYLOAD_MISMATCH");
            }

            return new PaymentConfirmationResult(PaymentOutcome.Duplicate, notification.OrderId, "DUPLICATE_EVENT");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == notification.OrderId, cancellationToken);
        if (order is null)
        {
            return await RejectAsync(notification, PaymentOutcome.OrderNotFound, "ORDER_NOT_FOUND", cancellationToken);
        }

        // 2. State machine: only a payable order can be confirmed.
        if (order.Status != OrderStatus.PendingPayment)
        {
            return order.Status == OrderStatus.Paid
                ? await RejectAsync(notification, PaymentOutcome.OrderAlreadyPaid, "ORDER_ALREADY_PAID", cancellationToken)
                : await RejectAsync(notification, PaymentOutcome.OrderNotPayable, "ORDER_NOT_PAYABLE", cancellationToken);
        }

        // 3. The money must match what the order expects.
        if (notification.Amount <= 0)
        {
            return await RejectAsync(notification, PaymentOutcome.InvalidAmount, "INVALID_AMOUNT", cancellationToken);
        }

        if (!string.Equals(order.Currency, notification.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return await RejectAsync(notification, PaymentOutcome.CurrencyMismatch, "CURRENCY_MISMATCH", cancellationToken);
        }

        if (order.TotalAmount != notification.Amount)
        {
            return await RejectAsync(notification, PaymentOutcome.AmountMismatch, "AMOUNT_MISMATCH", cancellationToken);
        }

        // 4. There must be a driver to credit, otherwise the earning would be money nobody can claim.
        if (order.DriverId <= 0)
        {
            return await RejectAsync(notification, PaymentOutcome.DriverNotAssigned, "DRIVER_NOT_ASSIGNED", cancellationToken);
        }

        var now = _clock.GetUtcNow();
        var earningAmount = MoneyPolicy.Round(order.DeliveryFee * _earnings.DriverShareRate);

        order.Status = OrderStatus.Paid;

        _db.DriverEarnings.Add(new DriverEarning
        {
            OrderId = order.Id,
            DriverId = order.DriverId,
            Amount = earningAmount,
            Currency = order.Currency,
            CreatedAt = now
        });

        _db.PaymentInbox.Add(new PaymentInbox
        {
            Provider = notification.Provider,
            ProviderEventId = notification.ProviderEventId,
            OrderId = order.Id,
            Amount = notification.Amount,
            Currency = order.Currency.ToUpperInvariant(),
            ReceivedAt = now,
            ProcessedAt = now
        });

        _db.RestaurantNotificationOutbox.Add(new RestaurantNotificationOutbox
        {
            OrderId = order.Id,
            Type = "OrderPaid",
            Payload = JsonSerializer.Serialize(new OrderPaidNotification(order.Id, earningAmount, order.Currency, now)),
            Status = OutboxStatus.Pending,
            NextAttemptAt = now
        });

        // The audit trail of the applied event is committed in the same transaction as the money movement.
        _db.PaymentEventLog.Add(PaymentEventLog.From(notification, "Applied", null, true, now));

        try
        {
            // Single EF transaction: status + earning + inbox + outbox are applied together or not at all.
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Another concurrent delivery of the same payment won the race. The unique indexes (and the
            // rowversion on Order) are the guarantee; application code only has to interpret the outcome.
            _db.ChangeTracker.Clear();

            var winnerEvent = await GetProcessedEventAsync(notification.Provider, notification.ProviderEventId, cancellationToken);
            if (winnerEvent is not null)
            {
                if (winnerEvent.OrderId != notification.OrderId)
                {
                    await LogRejectedAsync(notification, "EVENT_ID_CONFLICT", cancellationToken);
                    return new PaymentConfirmationResult(PaymentOutcome.EventIdConflict, notification.OrderId, "EVENT_ID_CONFLICT");
                }

                return new PaymentConfirmationResult(PaymentOutcome.Duplicate, notification.OrderId, "DUPLICATE_EVENT");
            }

            var alreadyPaid = await _db.Orders
                .AnyAsync(o => o.Id == notification.OrderId && o.Status == OrderStatus.Paid, cancellationToken);

            if (alreadyPaid)
            {
                return await RejectAsync(notification, PaymentOutcome.OrderAlreadyPaid, "ORDER_ALREADY_PAID", cancellationToken);
            }

            _logger.LogWarning(ex, "Concurrent payment confirmation conflict for order {OrderId}", notification.OrderId);
            return new PaymentConfirmationResult(PaymentOutcome.ConcurrencyConflict, notification.OrderId, "CONCURRENCY_CONFLICT");
        }
        catch (DbUpdateConcurrencyException ex)
        {
            _db.ChangeTracker.Clear();

            var alreadyPaid = await _db.Orders
                .AnyAsync(o => o.Id == notification.OrderId && o.Status == OrderStatus.Paid, cancellationToken);

            if (alreadyPaid)
            {
                return await RejectAsync(notification, PaymentOutcome.OrderAlreadyPaid, "ORDER_ALREADY_PAID", cancellationToken);
            }

            _logger.LogWarning(ex, "Optimistic concurrency conflict for order {OrderId}", notification.OrderId);
            return new PaymentConfirmationResult(PaymentOutcome.ConcurrencyConflict, notification.OrderId, "CONCURRENCY_CONFLICT");
        }
        catch (DbUpdateException ex) when (IsTransientConflict(ex))
        {
            // Deadlock or lock timeout: SQL Server already rolled the transaction back. Answer with a retryable
            // conflict instead of a 500, so the gateway retries instead of the request dying as an error.
            _db.ChangeTracker.Clear();

            _logger.LogWarning(ex, "Transient database conflict for order {OrderId}", notification.OrderId);
            return new PaymentConfirmationResult(PaymentOutcome.ConcurrencyConflict, notification.OrderId, "CONCURRENCY_CONFLICT");
        }

        _logger.LogInformation(
            "Payment confirmed for order {OrderId}: event {ProviderEventId}, earning {EarningAmount} {Currency}",
            order.Id, notification.ProviderEventId, earningAmount, order.Currency);

        return new PaymentConfirmationResult(PaymentOutcome.Processed, order.Id, "PAYMENT_CONFIRMED");
    }

    /// <summary>Rejects the event and leaves a durable trace of it, so a charged-but-rejected payment can be reconciled.</summary>
    private async Task<PaymentConfirmationResult> RejectAsync(
        PaymentNotification notification,
        PaymentOutcome outcome,
        string code,
        CancellationToken cancellationToken)
    {
        await LogRejectedAsync(notification, code, cancellationToken);
        return new PaymentConfirmationResult(outcome, notification.OrderId, code);
    }

    private Task LogRejectedAsync(PaymentNotification notification, string code, CancellationToken cancellationToken)
        => _eventLog.RecordAsync(notification, "Rejected", code, authenticated: true, cancellationToken);

    /// <summary>Deadlock (1205), lock timeout (1222) and the Azure SQL transient set: retryable by the caller.</summary>
    private static bool IsTransientConflict(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException sqlException && sqlException.Number is 1205 or 1222 or 4060 or 40197 or 40501)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUniqueViolation(DbUpdateException exception)
    {
        for (var inner = exception.InnerException; inner is not null; inner = inner.InnerException)
        {
            if (inner is SqlException sqlException && sqlException.Number is 2601 or 2627)
            {
                return true;
            }

            if (inner.Message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase) ||
                inner.Message.Contains("UNIQUE constraint failed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
