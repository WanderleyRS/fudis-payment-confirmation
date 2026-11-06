namespace Fudis.Payments.Services;

public class GatewayOptions
{
    /// <summary>Shared secret used to sign/verify gateway notifications. Supply via environment variable in production.</summary>
    public string SharedSecret { get; set; } = string.Empty;

    /// <summary>Accepted clock skew for the signature timestamp, in minutes.</summary>
    public int TimestampToleranceMinutes { get; set; } = 15;
}

public class EarningsOptions
{
    /// <summary>Share of the delivery fee that belongs to the driver (0.70 = 70%).</summary>
    public decimal DriverShareRate { get; set; } = 0.70m;
}

public class RestaurantOptions
{
    /// <summary>Base address of the restaurant notification service.</summary>
    public string BaseUrl { get; set; } = "http://localhost:5080/";
}

public class OutboxOptions
{
    /// <summary>Retry budget. After this many failed attempts the notification is moved to the dead letter state.</summary>
    public int MaxAttempts { get; set; } = 10;
}
