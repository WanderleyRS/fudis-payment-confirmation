using Fudis.Payments.Domain;

using Fudis.Payments.Contracts;

namespace Fudis.Payments.Domain;

/// <summary>
/// Append-only log of every gateway event the endpoint received, including the ones it rejected.
/// The inbox guards idempotency; this log answers "how many events did we receive for this order?"
/// and is the starting point for reconciliation when a payment was charged but not applied.
/// </summary>
public class PaymentEventLog
{
    public int Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string ProviderEventId { get; set; } = string.Empty;

    public int OrderId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    /// <summary>Applied | Rejected | RejectedAuth.</summary>
    public string Outcome { get; set; } = string.Empty;

    /// <summary>Machine readable reason, e.g. AMOUNT_MISMATCH.</summary>
    public string? Detail { get; set; }

    /// <summary>False when the signature or the timestamp was rejected.</summary>
    public bool Authenticated { get; set; }

    public static PaymentEventLog From(PaymentNotification notification, string outcome, string? detail, bool authenticated, DateTimeOffset receivedAt) => new()
    {
        Provider = notification.Provider,
        ProviderEventId = notification.ProviderEventId,
        OrderId = notification.OrderId,
        Amount = notification.Amount,
        Currency = notification.Currency.ToUpperInvariant(),
        ReceivedAt = receivedAt,
        Outcome = outcome,
        Detail = detail,
        Authenticated = authenticated
    };
}
