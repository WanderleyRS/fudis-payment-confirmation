using System.Net;
using Fudis.Payments.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Fudis.Payments.Tests;

/// <summary>
/// Covers the gaps found in the external review: authentication must never leak an idempotent 200, a replayed
/// event with a different payload must be detected, and every received event must leave a trace.
/// </summary>
[Collection("api")]
public class PaymentEventAuditTests : ApiTestBase
{
    public PaymentEventAuditTests(FudisApiFactory factory) : base(factory) { }

    [Fact]
    public async Task ForgedSignature_OnAnAlreadyAppliedEvent_StillReturnsUnauthorized()
    {
        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);

        await PostPaymentAsync(notification);

        // The event was already applied, but the caller has no valid signature: it must not learn that.
        var forged = await PostPaymentAsync(notification, signatureOverride: new string('f', 64));

        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);

        var state = await ReadStateAsync(order.Id);
        Assert.Equal(1, state.Earnings);
    }

    [Fact]
    public async Task StaleTimestamp_OnAnAlreadyAppliedEvent_ReturnsDuplicate()
    {
        var order = await SeedOrderAsync();
        var notification = BuildNotification(order.Id, 55.00m);

        await PostPaymentAsync(notification);

        // A late legitimate retry of a settled payment: valid signature, expired window.
        var late = await PostPaymentAsync(notification, timestamp: DateTimeOffset.UtcNow.AddMinutes(-30));

        Assert.Equal(HttpStatusCode.OK, late.StatusCode);
        Assert.Equal("duplicate", (await ReadJsonAsync(late)).GetProperty("status").GetString());

        var state = await ReadStateAsync(order.Id);
        Assert.Equal(1, state.Earnings);
    }

    [Fact]
    public async Task SameEventIdWithADifferentAmount_IsRejectedAsPayloadMismatch()
    {
        var order = await SeedOrderAsync();

        await PostPaymentAsync(BuildNotification(order.Id, 55.00m, providerEventId: "evt_same"));

        // Same event id, same order, different amount: that is an inconsistency, not a duplicate.
        var tampered = await PostPaymentAsync(BuildNotification(order.Id, 99.00m, providerEventId: "evt_same"));

        Assert.Equal(HttpStatusCode.Conflict, tampered.StatusCode);
        Assert.Equal("EVENT_PAYLOAD_MISMATCH", (await ReadJsonAsync(tampered)).GetProperty("code").GetString());

        var state = await ReadStateAsync(order.Id);
        Assert.Equal(1, state.Earnings);
        Assert.Equal(21.00m, state.EarningAmount);
    }

    [Fact]
    public async Task RejectedEvent_LeavesADurableTraceForReconciliation()
    {
        var order = await SeedOrderAsync(totalAmount: 55.00m);

        var response = await PostPaymentAsync(BuildNotification(order.Id, 40.00m));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        await using var db = Factory.CreateDbContext();
        var entry = await db.PaymentEventLog.AsNoTracking().SingleAsync();

        Assert.Equal("Rejected", entry.Outcome);
        Assert.Equal("AMOUNT_MISMATCH", entry.Detail);
        Assert.True(entry.Authenticated);
        Assert.Equal(order.Id, entry.OrderId);
        Assert.Equal(40.00m, entry.Amount);
    }

    [Fact]
    public async Task ForgedSignature_IsLoggedAsUnauthenticated()
    {
        var order = await SeedOrderAsync();

        await PostPaymentAsync(BuildNotification(order.Id, 55.00m), signatureOverride: new string('f', 64));

        await using var db = Factory.CreateDbContext();
        var entry = await db.PaymentEventLog.AsNoTracking().SingleAsync();

        Assert.Equal("RejectedAuth", entry.Outcome);
        Assert.False(entry.Authenticated);
    }

    [Fact]
    public async Task AppliedEvent_IsLoggedInTheSameTransactionAsThePayment()
    {
        var order = await SeedOrderAsync();

        await PostPaymentAsync(BuildNotification(order.Id, 55.00m));

        await using var db = Factory.CreateDbContext();
        var entry = await db.PaymentEventLog.AsNoTracking().SingleAsync();

        Assert.Equal("Applied", entry.Outcome);
        Assert.Null(entry.Detail);
    }
}
