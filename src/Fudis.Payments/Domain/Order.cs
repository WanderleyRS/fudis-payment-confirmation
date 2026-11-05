namespace Fudis.Payments.Domain;

public class Order
{
    public int Id { get; set; }

    public OrderStatus Status { get; set; } = OrderStatus.PendingPayment;

    /// <summary>Assigned driver. Zero means "not assigned yet" and blocks the driver earning.</summary>
    public int DriverId { get; set; }

    /// <summary>Shipping fee charged to the customer. The driver earns 70% of it.</summary>
    public decimal DeliveryFee { get; set; }

    /// <summary>Expected amount to be paid, used to validate the gateway notification amount.</summary>
    public decimal TotalAmount { get; set; }

    public string Currency { get; set; } = "BRL";

    /// <summary>Optimistic concurrency token (SQL Server rowversion).</summary>
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
