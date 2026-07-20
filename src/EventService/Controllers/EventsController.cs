using EventService.Data;
using EventService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventService.Controllers;

[ApiController]
[Route("[controller]")]
public class EventsController(EventDbContext db) : ControllerBase
{
    [HttpGet("{id}")]
    public async Task<ActionResult<Event>> GetById(Guid id)
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

        return @event;
    }
}
