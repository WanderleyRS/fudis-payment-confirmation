using Fudis.Payments.Contracts;
using Fudis.Payments.Services;
using Microsoft.AspNetCore.Mvc;

namespace Fudis.Payments.Controllers;

[ApiController]
[Route("api/payments")]
public class PaymentsController : ControllerBase
{
    private readonly IPaymentConfirmationService _paymentService;
    private readonly IGatewaySignatureValidator _signatureValidator;

    public PaymentsController(
        IPaymentConfirmationService paymentService,
        IGatewaySignatureValidator signatureValidator)
    {
        _paymentService = paymentService;
        _signatureValidator = signatureValidator;
    }

    /// <summary>
    /// Confirms a payment notification coming from the payment gateway.
    /// Idempotent, authenticated and atomic. See README for the contract.
    /// </summary>
    [HttpPost("confirm-payment")]
    public async Task<IActionResult> ConfirmPayment(
        [FromBody] PaymentNotification request,
        CancellationToken cancellationToken)
    {
        var signature = Request.Headers[GatewaySignatureValidator.SignatureHeader].FirstOrDefault();
        var timestamp = Request.Headers[GatewaySignatureValidator.TimestampHeader].FirstOrDefault();

        if (!_signatureValidator.TryValidate(request, signature, timestamp, out var error))
        {
            // A legitimate gateway retry can arrive after the timestamp window. If the event was already
            // applied to this same order we answer idempotently instead of 401, so the gateway stops
            // retrying a settled payment.
            var processedOrderId = await _paymentService.GetProcessedOrderIdAsync(request.Provider, request.ProviderEventId, cancellationToken);
            if (processedOrderId == request.OrderId)
            {
                return Ok(new { status = "duplicate", orderId = request.OrderId });
            }

            return Unauthorized(new { code = error, message = "Invalid gateway signature." });
        }

        var result = await _paymentService.ConfirmAsync(request, cancellationToken);

        return result.Outcome switch
        {
            PaymentOutcome.Processed => Ok(new { status = "processed", orderId = result.OrderId }),
            PaymentOutcome.Duplicate => Ok(new { status = "duplicate", orderId = result.OrderId }),
            PaymentOutcome.OrderNotFound => NotFound(new { code = result.Code, message = "Order not found." }),
            PaymentOutcome.OrderAlreadyPaid => Conflict(new { code = result.Code, message = "Order is already paid." }),
            PaymentOutcome.OrderNotPayable => Conflict(new { code = result.Code, message = "Order is not in a payable state." }),
            PaymentOutcome.AmountMismatch => Conflict(new { code = result.Code, message = "Paid amount does not match the order total." }),
            PaymentOutcome.CurrencyMismatch => Conflict(new { code = result.Code, message = "Currency does not match the order currency." }),
            PaymentOutcome.InvalidAmount => BadRequest(new { code = result.Code, message = "Paid amount must be greater than zero." }),
            PaymentOutcome.EventIdConflict => Conflict(new { code = result.Code, message = "Gateway event id was already used for a different order." }),
            PaymentOutcome.DriverNotAssigned => Conflict(new { code = result.Code, message = "Order has no assigned driver." }),
            _ => StatusCode(StatusCodes.Status500InternalServerError, new { code = result.Code })
        };
    }
}
