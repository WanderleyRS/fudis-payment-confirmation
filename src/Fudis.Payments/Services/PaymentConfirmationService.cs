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
    private readonly ILogger<PaymentConfirmationService> _logger;

    public PaymentConfirmationService(
        FudisDbContext db,
        IOptions<EarningsOptions> earnings,
        TimeProvider clock,
        ILogger<PaymentConfirmationService> logger)
    {
        _db = db;
        _earnings = earnings.Value;
        _clock = clock;
        _logger = logger;
    }

    public async Task<int?> GetProcessedOrderIdAsync(string provider, string providerEventId, CancellationToken cancellationToken = default)
        => await _db.PaymentInbox
               .Where(i => i.Provider == provider && i.ProviderEventId == providerEventId)
               .Select(i => (int?)i.OrderId)
               .FirstOrDefaultAsync(cancellationToken);

    public async Task<PaymentConfirmationResult> ConfirmAsync(
        PaymentNotification notification,
        CancellationToken cancellationToken = default)
    {
        // 1. Idempotency at event level: the same gateway event applied twice is a no-op.
        var processedOrderId = await GetProcessedOrderIdAsync(notification.Provider, notification.ProviderEventId, cancellationToken);
        if (processedOrderId.HasValue)
        {
            // The event id was already used. If it refers to another order the request is contradictory:
            // answering "duplicate" would silently leave a paid order unconfirmed.
            return processedOrderId.Value == notification.OrderId
                ? new PaymentConfirmationResult(PaymentOutcome.Duplicate, notification.OrderId, "DUPLICATE_EVENT")
                : new PaymentConfirmationResult(PaymentOutcome.EventIdConflict, notification.OrderId, "EVENT_ID_CONFLICT");
        }

        var order = await _db.Orders.FirstOrDefaultAsync(o => o.Id == notification.OrderId, cancellationToken);
        if (order is null)
        {
            return new PaymentConfirmationResult(PaymentOutcome.OrderNotFound, notification.OrderId, "ORDER_NOT_FOUND");
        }

        // 2. State machine: only a payable order can be confirmed.
        if (order.Status != OrderStatus.PendingPayment)
        {
            return order.Status == OrderStatus.Paid
                ? new PaymentConfirmationResult(PaymentOutcome.OrderAlreadyPaid, order.Id, "ORDER_ALREADY_PAID")
                : new PaymentConfirmationResult(PaymentOutcome.OrderNotPayable, order.Id, "ORDER_NOT_PAYABLE");
        }

        // 3. The money must match what the order expects.
        if (notification.Amount <= 0)
        {
            return new PaymentConfirmationResult(PaymentOutcome.InvalidAmount, order.Id, "INVALID_AMOUNT");
        }

        if (!string.Equals(order.Currency, notification.Currency, StringComparison.OrdinalIgnoreCase))
        {
            return new PaymentConfirmationResult(PaymentOutcome.CurrencyMismatch, order.Id, "CURRENCY_MISMATCH");
        }

        if (order.TotalAmount != notification.Amount)
        {
            return new PaymentConfirmationResult(PaymentOutcome.AmountMismatch, order.Id, "AMOUNT_MISMATCH");
        }

        // 4. There must be a driver to credit, otherwise the earning would be money nobody can claim.
        if (order.DriverId <= 0)
        {
            return new PaymentConfirmationResult(PaymentOutcome.DriverNotAssigned, order.Id, "DRIVER_NOT_ASSIGNED");
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
            Payload = $"{{\"orderId\":{order.Id},\"paidAt\":\"{now:O}\",\"amount\":{earningAmount.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}",
            Status = OutboxStatus.Pending,
            NextAttemptAt = now
        });

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

            var winnerOrderId = await GetProcessedOrderIdAsync(notification.Provider, notification.ProviderEventId, cancellationToken);
            if (winnerOrderId.HasValue)
            {
                return winnerOrderId.Value == notification.OrderId
                    ? new PaymentConfirmationResult(PaymentOutcome.Duplicate, notification.OrderId, "DUPLICATE_EVENT")
                    : new PaymentConfirmationResult(PaymentOutcome.EventIdConflict, notification.OrderId, "EVENT_ID_CONFLICT");
            }

            var alreadyPaid = await _db.Orders
                .AnyAsync(o => o.Id == notification.OrderId && o.Status == OrderStatus.Paid, cancellationToken);

            if (alreadyPaid)
            {
                return new PaymentConfirmationResult(PaymentOutcome.OrderAlreadyPaid, notification.OrderId, "ORDER_ALREADY_PAID");
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
                return new PaymentConfirmationResult(PaymentOutcome.OrderAlreadyPaid, notification.OrderId, "ORDER_ALREADY_PAID");
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
