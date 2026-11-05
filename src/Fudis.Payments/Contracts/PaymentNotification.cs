using System.ComponentModel.DataAnnotations;

namespace Fudis.Payments.Contracts;

/// <summary>
/// Inbound gateway notification. This is the proposed input contract: the original fragment only carried
/// OrderId, which is not enough to authenticate the event, to audit it, or to validate the paid amount.
/// </summary>
public class PaymentNotification
{
    /// <summary>Payment provider identifier, e.g. "stripe". Part of the idempotency key.</summary>
    [Required]
    [StringLength(32)]
    public string Provider { get; set; } = string.Empty;

    /// <summary>Provider-scoped event id. Part of the idempotency key. Unique per provider.</summary>
    [Required]
    [StringLength(128)]
    public string ProviderEventId { get; set; } = string.Empty;

    public int OrderId { get; set; }

    /// <summary>Amount actually paid. Validated against the order total.</summary>
    /// <remarks>
    /// No Range attribute on purpose: it parses its bounds with the current culture, which breaks on machines
    /// whose decimal separator is not ".". The amount is validated in the service instead, culture free.
    /// </remarks>
    public decimal Amount { get; set; }

    /// <summary>ISO-4217 currency code. Validated against the order currency.</summary>
    [Required]
    [StringLength(3)]
    public string Currency { get; set; } = string.Empty;

    /// <summary>When the gateway captured the payment.</summary>
    public DateTimeOffset PaidAt { get; set; }
}
