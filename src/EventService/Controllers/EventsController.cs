using EventService.Data;
using EventService.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventService.Controllers;

[ApiController]
[Route("[controller]")]
public class EventsController(EventDbContext db) : ControllerBase
{
    [HttpGet("{id:guid}")]
    public async Task<ActionResult<EventResponse>> GetById(Guid id)
    {
        var @event = await db.Events
            .AsNoTracking()
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .Include(e => e.Tickets)
            .FirstOrDefaultAsync(e => e.Id == id);

        if (@event is null)
        {
            return NotFound();
        }

        return new EventResponse(
            @event.Id,
            @event.Name,
            @event.Description,
            @event.Date,
            new VenueResponse(@event.Venue!.Id, @event.Venue.Name, @event.Venue.Address, @event.Venue.Capacity),
            new PerformerResponse(@event.Performer!.Id, @event.Performer.Name, @event.Performer.Type),
            @event.Tickets.Select(t => new TicketResponse(t.Id, t.Seat, t.Price, t.Status)).ToList());
    }

    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<EventSummaryResponse>>> Search(
        [FromQuery] string? keyword,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var query = db.Events.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            query = query.Where(e =>
                EF.Functions.ILike(e.Name, $"%{keyword}%") ||
                (e.Description != null && EF.Functions.ILike(e.Description, $"%{keyword}%")));
        }

        if (start is not null)
        {
            // Query string dates parse with Kind=Unspecified; Npgsql requires UTC to
            // compare against a "timestamp with time zone" column. Treating the
            // caller's date as UTC directly (not converting from local time) since
            // there's no timezone info in the request to convert from.
            var startUtc = DateTime.SpecifyKind(start.Value, DateTimeKind.Utc);
            query = query.Where(e => e.Date >= startUtc);
        }

        if (end is not null)
        {
            var endUtc = DateTime.SpecifyKind(end.Value, DateTimeKind.Utc);
            query = query.Where(e => e.Date <= endUtc);
        }

        var events = await query
            .Include(e => e.Venue)
            .Include(e => e.Performer)
            .OrderBy(e => e.Date)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(e => new EventSummaryResponse(
                e.Id,
                e.Name,
                e.Description,
                e.Date,
                new VenueResponse(e.Venue!.Id, e.Venue.Name, e.Venue.Address, e.Venue.Capacity),
                new PerformerResponse(e.Performer!.Id, e.Performer.Name, e.Performer.Type)))
            .ToListAsync();

        return events;
    }
}
