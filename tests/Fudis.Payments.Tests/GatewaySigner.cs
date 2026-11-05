using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fudis.Payments.Contracts;

namespace Fudis.Payments.Tests;

/// <summary>
/// Independent implementation of the gateway signing contract. The test suite deliberately does NOT reuse the
/// production helper: if the canonical payload format changes, these tests fail instead of silently passing.
/// </summary>
public static class GatewaySigner
{
    public static string Sign(string sharedSecret, PaymentNotification notification, DateTimeOffset timestamp)
    {
        var canonical = string.Join('|',
            notification.Provider,
            notification.ProviderEventId,
            notification.OrderId.ToString(CultureInfo.InvariantCulture),
            notification.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            notification.Currency.ToUpperInvariant(),
            notification.PaidAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
