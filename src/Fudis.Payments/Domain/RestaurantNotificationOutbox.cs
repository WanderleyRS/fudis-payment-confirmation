namespace Fudis.Payments.Domain;

public enum OutboxStatus
{
    Pending = 0,
    Processing = 1,
    Delivered = 2,

    /// <summary>Terminal state: the retry budget was exhausted and the notification needs a human.</summary>
    Failed = 3
}

/// <summary>
/// Transactional outbox: the restaurant notification is written in the same transaction as the payment, so it can
/// never be lost by a notification outage. A background dispatcher delivers it with retries.
/// </summary>
public class RestaurantNotificationOutbox
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public string Type { get; set; } = "OrderPaid";

    public string Payload { get; set; } = string.Empty;

    public OutboxStatus Status { get; set; } = OutboxStatus.Pending;

    public int Attempts { get; set; }

    public DateTimeOffset NextAttemptAt { get; set; }

    public DateTimeOffset? DeliveredAt { get; set; }

    public DateTimeOffset? ProcessingStartedAt { get; set; }

    public string? LastError { get; set; }
}
