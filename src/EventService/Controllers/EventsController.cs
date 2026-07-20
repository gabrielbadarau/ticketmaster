using EventService.Data;
using EventService.Dtos;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventService.Controllers;

[ApiController]
[Route("[controller]")]
public class EventsController(EventDbContext db) : ControllerBase
{
    [HttpGet("{id}")]
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
}
