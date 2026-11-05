using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Fudis.Payments.Services;

/// <summary>
/// Fire-and-forget HTTP notification to the restaurant service. Failures throw, so the outbox dispatcher
/// can retry. The consumer must be idempotent by order id (at-least-once delivery contract).
/// </summary>
public class HttpNotificationService : INotificationService
{
    private readonly HttpClient _httpClient;

    public HttpNotificationService(HttpClient httpClient, IOptions<RestaurantOptions> options)
    {
        _httpClient = httpClient;
        if (_httpClient.BaseAddress is null && !string.IsNullOrWhiteSpace(options.Value.BaseUrl))
        {
            _httpClient.BaseAddress = new Uri(options.Value.BaseUrl);
        }
    }

    public async Task NotifyRestaurant(int orderId, CancellationToken cancellationToken = default)
    {
        var response = await _httpClient.PostAsJsonAsync(
            $"internal/orders/{orderId}/paid",
            new { orderId },
            cancellationToken);

        response.EnsureSuccessStatusCode();
    }
}
