// Demo worker that produces a realistic mix of logs for trying out freelog.
// With FreeLog__Dsn set it ships via the Sentry SDK; without it, it only writes to the console.
using FreeLog.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Sentry;

var builder = Host.CreateApplicationBuilder(args);
builder.AddFreeLog();
builder.Services.AddHostedService<OrderSimulator>();
builder.Build().Run();

internal sealed class OrderSimulator(ILogger<OrderSimulator> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var orderId = 1000;
        while (!stoppingToken.IsCancellationRequested)
        {
            orderId++;
            using (logger.BeginScope(new Dictionary<string, object> { ["RequestId"] = Guid.NewGuid().ToString("N")[..12] }))
            {
                logger.LogInformation("Order {OrderId} placed by {Customer} for {Amount:F2}", orderId, "customer-" + orderId % 7, orderId % 97 + 0.99);

                // Custom metrics (no-ops when FreeLog:Dsn isn't set).
                var region = orderId % 3 == 0 ? "us" : "eu";
                SentrySdk.Metrics.EmitCounter("orders.placed", 1, [new KeyValuePair<string, object>("region", region)], null);
                SentrySdk.Metrics.EmitDistribution("checkout.duration", 80 + orderId % 400, "millisecond");

                if (orderId % 5 == 0)
                    logger.LogWarning("Payment provider slow for order {OrderId}: {ElapsedMs} ms", orderId, 1500 + orderId % 300);

                if (orderId % 9 == 0)
                {
                    try
                    {
                        throw new InvalidOperationException($"Card declined for order {orderId}");
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Checkout failed for order {OrderId}", orderId);
                    }
                }
            }

            await Task.Delay(2000, stoppingToken);
        }
    }
}
