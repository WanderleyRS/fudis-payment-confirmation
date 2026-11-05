namespace Fudis.Payments.Domain;

/// <summary>Lifecycle of an order. Only <see cref="PendingPayment"/> may transition to <see cref="Paid"/>.</summary>
public enum OrderStatus
{
    PendingPayment = 0,
    Paid = 1,
    Accepted = 2,
    Preparing = 3,
    OutForDelivery = 4,
    Delivered = 5,
    Cancelled = 6
}
