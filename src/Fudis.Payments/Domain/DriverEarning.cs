namespace Fudis.Payments.Domain;

public class DriverEarning
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public int DriverId { get; set; }

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "BRL";

    public DateTimeOffset CreatedAt { get; set; }
}
