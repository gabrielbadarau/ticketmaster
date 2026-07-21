using BookingService.Data;
using BookingService.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace BookingService.Controllers;

[ApiController]
[Route("queue")]
public class QueueController(BookingDbContext db, IConnectionMultiplexer redis) : ControllerBase
{
    // How long an admitted user has to complete a reservation before someone
    // else gets let in instead. Reuses the same window length as the ticket
    // lock TTL for consistency, though the two are otherwise unrelated.
    public static readonly TimeSpan AdmissionWindow = TimeSpan.FromMinutes(10);

    // Stands in for the spec's "admin-enabled" virtual queue — we have no
    // real admin/auth system yet, so this is just a plain endpoint instead of
    // something gated behind one. Most events never call this and skip
    // queueing entirely; only ones expected to be extremely popular would.
    [HttpPost("{eventId:guid}/enable")]
    public async Task<IActionResult> Enable(Guid eventId)
    {
        var eventExists = await db.Tickets.AnyAsync(t => t.EventId == eventId);
        if (!eventExists)
        {
            return NotFound("No tickets found for this event.");
        }

        await redis.GetDatabase().SetAddAsync("queue-enabled-events", eventId.ToString());
        return Ok();
    }

    [HttpPost("{eventId:guid}/join")]
    public async Task<ActionResult<JoinQueueResponse>> Join(Guid eventId, JoinQueueRequest request)
    {
        var redisDb = redis.GetDatabase();
        var queueKey = $"queue:{eventId}";

        // NotExists: joining twice (e.g. a page refresh) doesn't reset your
        // place in line — only the first join call actually sets your score.
        await redisDb.SortedSetAddAsync(
            queueKey,
            request.UserId.ToString(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            SortedSetWhen.NotExists);

        await redisDb.SetAddAsync("active-queues", eventId.ToString());

        var rank = await redisDb.SortedSetRankAsync(queueKey, request.UserId.ToString());
        var size = await redisDb.SortedSetLengthAsync(queueKey);

        return new JoinQueueResponse((int)(rank ?? 0) + 1, (int)size);
    }

    [HttpGet("{eventId:guid}/status")]
    public async Task<ActionResult<QueueStatusResponse>> Status(Guid eventId, [FromQuery] Guid userId)
    {
        var redisDb = redis.GetDatabase();

        if (await redisDb.KeyExistsAsync($"admitted:{eventId}:{userId}"))
        {
            return new QueueStatusResponse(true, null, null);
        }

        var queueKey = $"queue:{eventId}";
        var rank = await redisDb.SortedSetRankAsync(queueKey, userId.ToString());

        if (rank is null)
        {
            return NotFound("Not in queue for this event.");
        }

        var size = await redisDb.SortedSetLengthAsync(queueKey);
        return new QueueStatusResponse(false, (int)rank + 1, (int)size);
    }
}
