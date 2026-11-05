namespace Fudis.Payments.Domain;

/// <summary>
/// Idempotency ledger of gateway payment events. The unique index on (Provider, ProviderEventId) is the
/// database-level guarantee that one gateway event is applied at most once, even under concurrent delivery.
/// </summary>
public class PaymentInbox
{
    public int Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    public string ProviderEventId { get; set; } = string.Empty;

    public int OrderId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = string.Empty;

    public DateTimeOffset ReceivedAt { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
