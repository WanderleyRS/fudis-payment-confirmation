using Fudis.Payments.Services;

namespace Fudis.Payments.Tests;

/// <summary>Test double for the restaurant notification service, able to simulate outages.</summary>
public class FakeNotificationService : INotificationService
{
    private readonly object _sync = new();
    private readonly List<int> _calls = new();
    private int _failuresRemaining;

    public IReadOnlyList<int> Calls
    {
        get { lock (_sync) { return _calls.ToArray(); } }
    }

    public void FailNext(int times)
    {
        lock (_sync) { _failuresRemaining = times; }
    }

    public void Reset()
    {
        lock (_sync) { _calls.Clear(); _failuresRemaining = 0; }
    }

    public Task NotifyRestaurant(int orderId, CancellationToken cancellationToken = default)
    {
        lock (_sync)
        {
            _calls.Add(orderId);

            if (_failuresRemaining > 0)
            {
                _failuresRemaining--;
                throw new HttpRequestException("restaurant notification service unavailable");
            }
        }

        return Task.CompletedTask;
    }
}
