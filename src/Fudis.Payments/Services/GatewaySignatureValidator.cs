using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fudis.Payments.Contracts;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Services;

public interface IGatewaySignatureValidator
{
    /// <summary>
    /// Validates the HMAC-SHA256 signature and the timestamp window of a gateway notification.
    /// </summary>
    /// <param name="error">Machine readable reason when validation fails.</param>
    bool TryValidate(PaymentNotification notification, string? signature, string? timestamp, out string error);
}

public class GatewaySignatureValidator : IGatewaySignatureValidator
{
    public const string SignatureHeader = "X-Fudis-Signature";
    public const string TimestampHeader = "X-Fudis-Timestamp";
    public const string StaleTimestampError = "STALE_TIMESTAMP";

    private readonly GatewayOptions _options;
    private readonly TimeProvider _clock;

    public GatewaySignatureValidator(IOptions<GatewayOptions> options, TimeProvider clock)
    {
        _options = options.Value;
        _clock = clock;
    }

    /// <summary>
    /// Canonical form of the signed payload. Both the gateway and this service must agree on it byte for byte;
    /// it is pinned by the test suite with an independent implementation.
    /// </summary>
    public static string CanonicalPayload(PaymentNotification n, DateTimeOffset timestamp) =>
        string.Join('|',
            n.Provider,
            n.ProviderEventId,
            n.OrderId.ToString(CultureInfo.InvariantCulture),
            n.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            n.Currency.ToUpperInvariant(),
            n.PaidAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            timestamp.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

    public static string ComputeSignature(string sharedSecret, PaymentNotification n, DateTimeOffset timestamp)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(sharedSecret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(CanonicalPayload(n, timestamp)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public bool TryValidate(PaymentNotification notification, string? signature, string? timestamp, out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(signature))
        {
            error = "MISSING_SIGNATURE";
            return false;
        }

        if (string.IsNullOrWhiteSpace(timestamp))
        {
            error = "MISSING_TIMESTAMP";
            return false;
        }

        if (!DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var signedAt))
        {
            error = "INVALID_TIMESTAMP";
            return false;
        }

        var tolerance = TimeSpan.FromMinutes(Math.Max(1, _options.TimestampToleranceMinutes));
        if (signedAt < _clock.GetUtcNow() - tolerance || signedAt > _clock.GetUtcNow() + tolerance)
        {
            error = StaleTimestampError;
            return false;
        }

        var expected = ComputeSignature(_options.SharedSecret, notification, signedAt);
        if (!FixedTimeEquals(expected, signature))
        {
            error = "INVALID_SIGNATURE";
            return false;
        }

        return true;
    }

    private static bool FixedTimeEquals(string expected, string actual)
    {
        byte[] expectedBytes;
        byte[] actualBytes;

        try
        {
            expectedBytes = Convert.FromHexString(expected);
            actualBytes = Convert.FromHexString(actual);
        }
        catch (FormatException)
        {
            return false;
        }

        if (expectedBytes.Length != 32 || actualBytes.Length != 32)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }
}
