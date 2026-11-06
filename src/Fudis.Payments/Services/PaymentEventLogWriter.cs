using Fudis.Payments.Contracts;
using Fudis.Payments.Domain;
using Fudis.Payments.Infra;

namespace Fudis.Payments.Services;

public interface IPaymentEventLogWriter
{
    /// <summary>Writes a standalone log entry. Used when no money movement is involved.</summary>
    Task RecordAsync(PaymentNotification notification, string outcome, string? detail, bool authenticated, CancellationToken cancellationToken = default);
}

public class PaymentEventLogWriter : IPaymentEventLogWriter
{
    private readonly FudisDbContext _db;
    private readonly TimeProvider _clock;

    public PaymentEventLogWriter(FudisDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task RecordAsync(PaymentNotification notification, string outcome, string? detail, bool authenticated, CancellationToken cancellationToken = default)
    {
        _db.PaymentEventLog.Add(PaymentEventLog.From(notification, outcome, detail, authenticated, _clock.GetUtcNow()));
        await _db.SaveChangesAsync(cancellationToken);
    }
}
