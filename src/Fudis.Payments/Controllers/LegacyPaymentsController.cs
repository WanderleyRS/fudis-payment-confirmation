using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Fudis.Payments.Infra;
using Fudis.Payments.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fudis.Payments.Controllers;

/// <summary>
/// Reference implementation from the original brief, kept verbatim as a baseline for the defence.
/// DO NOT USE IN PRODUCTION: it is not idempotent under concurrency, it is not authenticated, and it can lose
/// the restaurant notification when the notification service fails. The route is only mapped outside
/// Production, so it can never be reached by a real gateway.
/// </summary>
[ApiController]
[Route("api/legacy/payments")]
public class LegacyPaymentsController : ControllerBase
{
    private readonly FudisDbContext _db;
    private readonly INotificationService _notificationService;
    private readonly IWebHostEnvironment _environment;

    public LegacyPaymentsController(
        FudisDbContext db,
        INotificationService notificationService,
        IWebHostEnvironment environment)
    {
        _db = db;
        _notificationService = notificationService;
        _environment = environment;
    }

    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment([FromBody] LegacyPaymentNotification request)
    {
        if (!_environment.IsDevelopment() && !_environment.IsEnvironment("Testing"))
        {
            // The legacy endpoint double pays the driver under concurrency and can lose the restaurant
            // notification. It must not exist in a deployed environment.
            return NotFound();
        }

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
