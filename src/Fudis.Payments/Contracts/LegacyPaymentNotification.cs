namespace Fudis.Payments.Contracts;

/// <summary>
/// Original contract of the fragment, kept only so the legacy endpoint compiles verbatim for comparison.
/// The production contract is <see cref="PaymentNotification"/>.
/// </summary>
public class LegacyPaymentNotification
{
    public int OrderId { get; set; }
}
