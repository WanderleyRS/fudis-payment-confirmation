using Fudis.Payments.Infra;
using Fudis.Payments.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<FudisDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("Fudis")));

builder.Services.Configure<GatewayOptions>(builder.Configuration.GetSection("Gateway"));
builder.Services.Configure<EarningsOptions>(builder.Configuration.GetSection("Earnings"));
builder.Services.Configure<RestaurantOptions>(builder.Configuration.GetSection("Restaurant"));

builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddScoped<IGatewaySignatureValidator, GatewaySignatureValidator>();
builder.Services.AddScoped<IPaymentConfirmationService, PaymentConfirmationService>();
builder.Services.AddScoped<OutboxDispatcher>();

builder.Services.AddHttpClient<INotificationService, HttpNotificationService>();
builder.Services.AddHostedService<OutboxDispatcherHostedService>();

var app = builder.Build();

app.MapControllers();

app.Run();

/// <summary>Exposed so the integration tests can boot the real application through WebApplicationFactory.</summary>
public partial class Program { }
