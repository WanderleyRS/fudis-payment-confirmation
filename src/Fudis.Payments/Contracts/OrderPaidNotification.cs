namespace Fudis.Payments.Contracts;

/// <summary>Payload delivered to the restaurant service when an order is paid.</summary>
public record OrderPaidNotification(int OrderId, decimal DriverEarning, string Currency, DateTimeOffset PaidAt);
