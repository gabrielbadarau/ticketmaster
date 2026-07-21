using BookingService.Controllers;
using StackExchange.Redis;

namespace BookingService;

// Periodically lets the front of each active queue through, independent of
// any incoming HTTP request — this is what "admin-enabled virtual queue"
// actually runs on. Only ever talks to Redis, deliberately, so it never
// needs a scoped BookingDbContext inside a singleton-lifetime service.
public class QueueAdmissionService(
    IConnectionMultiplexer redis,
    IConfiguration config,
    ILogger<QueueAdmissionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(config.GetValue("Queue:AdmissionIntervalSeconds", 15));
        var batchSize = config.GetValue("Queue:AdmissionBatchSize", 2);
        var redisDb = redis.GetDatabase();

        while (!stoppingToken.IsCancellationRequested)
        {
            var activeEventIds = await redisDb.SetMembersAsync("active-queues");

            foreach (var eventIdValue in activeEventIds)
            {
                var eventId = eventIdValue.ToString();
                var queueKey = $"queue:{eventId}";

                var admittedEntries = await redisDb.SortedSetPopAsync(queueKey, batchSize);

                foreach (var entry in admittedEntries)
                {
                    var userId = entry.Element.ToString();
                    await redisDb.StringSetAsync($"admitted:{eventId}:{userId}", "1", QueueController.AdmissionWindow);
                    logger.LogInformation("Admitted user {UserId} into event {EventId}", userId, eventId);
                }

                if (await redisDb.SortedSetLengthAsync(queueKey) == 0)
                {
                    await redisDb.SetRemoveAsync("active-queues", eventId);
                }
            }

            await Task.Delay(interval, stoppingToken);
        }
    }
}
