namespace Fudis.Payments.Services;

/// <summary>Rounding policy for money. Fixed, documented and unit tested.</summary>
public static class MoneyPolicy
{
    public const int Scale = 2;

    /// <summary>
    /// Rounds to 2 decimals with MidpointRounding.AwayFromZero. Chosen because it matches the commercial
    /// rounding used by Brazilian payment processors (a half always rounds up, never to even), and because it is
    /// deterministic and symmetric: the platform never keeps a fraction the driver was credited with.
    /// </summary>
    public static decimal Round(decimal amount) => Math.Round(amount, Scale, MidpointRounding.AwayFromZero);
}

/// <summary>Notification sent to the restaurant that an order was paid.</summary>
public interface INotificationService
{
    Task NotifyRestaurant(int orderId, CancellationToken cancellationToken = default);
}
