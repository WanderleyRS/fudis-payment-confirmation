using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Fudis.Payments.Infra;
using Fudis.Payments.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fudis.Payments.Controllers;

/// <summary>
/// Reference implementation from the original brief, kept verbatim as a baseline for the defence.
/// DO NOT USE IN PRODUCTION: it is not idempotent under concurrency, it is not authenticated, and it can lose
/// the restaurant notification when the notification service fails.
/// </summary>
[ApiController]
[Route("api/legacy/payments")]
public class LegacyPaymentsController : ControllerBase
{
    private readonly FudisDbContext _db;
    private readonly INotificationService _notificationService;

    public LegacyPaymentsController(FudisDbContext db, INotificationService notificationService)
    {
        _db = db;
        _notificationService = notificationService;
    }

    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment([FromBody] LegacyPaymentNotification request)
    {
        var order = await _db.Orders
            .FindAsync(request.OrderId);

        if (order == null)
            return NotFound();

        if (order.Status == OrderStatus.Paid)
            return Ok();

        order.Status = OrderStatus.Paid;

        _db.DriverEarnings.Add(new DriverEarning
        {
            OrderId = order.Id,
            DriverId = order.DriverId,
            Amount = order.DeliveryFee * 0.70m
        });

        await _db.SaveChangesAsync();

        await _notificationService
            .NotifyRestaurant(order.Id);

        return Ok();
    }
}
