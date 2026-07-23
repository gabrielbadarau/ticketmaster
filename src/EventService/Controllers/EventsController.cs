using Elastic.Clients.Elasticsearch;
using EventService.Data;
using EventService.Dtos;
using EventService.Search;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EventService.Controllers;

[ApiController]
[Route("[controller]")]
public class EventsController(EventDbContext db, ElasticsearchClient esClient) : ControllerBase
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

    // Backed by Elasticsearch instead of Postgres -- the "events" index is kept in
    // sync via EventSearchSyncService's CDC pipeline. Postgres ILIKE with a leading
    // wildcard ("%keyword%") can't use a standard index at all, so every search was
    // a full table scan; Elasticsearch's inverted index is what actually gets this
    // toward the <500ms search latency the reference spec calls for as data grows.
    [HttpGet("search")]
    public async Task<ActionResult<IReadOnlyList<EventSummaryResponse>>> Search(
        [FromQuery] string? keyword,
        [FromQuery] DateTime? start,
        [FromQuery] DateTime? end,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20)
    {
        var response = await esClient.SearchAsync<EventSearchDocument>(s => s
            .Index(EventSearchSyncService.IndexName)
            .From((page - 1) * pageSize)
            .Size(pageSize)
            .Sort(so => so.Field(new Field("date")))
            .Query(q => q.Bool(b =>
            {
                if (!string.IsNullOrWhiteSpace(keyword))
                {
                    b.Must(m => m.Bool(inner => inner
                        .Should(
                            sh => sh.Match(mm => mm.Field(new Field("name")).Query(keyword)),
                            sh => sh.Match(mm => mm.Field(new Field("description")).Query(keyword)),
                            sh => sh.Match(mm => mm.Field(new Field("venueName")).Query(keyword)),
                            sh => sh.Match(mm => mm.Field(new Field("performerName")).Query(keyword)))
                        .MinimumShouldMatch(1)));
                }

                if (start is not null || end is not null)
                {
                    // Query string dates parse with Kind=Unspecified; treating the
                    // caller's date as UTC directly (not converting from local
                    // time) since there's no timezone info in the request itself.
                    b.Filter(f => f.Range(r => r.DateRange(dr =>
                    {
                        dr.Field(new Field("date"));
                        if (start is not null)
                        {
                            dr.Gte(DateTime.SpecifyKind(start.Value, DateTimeKind.Utc));
                        }

                        if (end is not null)
                        {
                            dr.Lte(DateTime.SpecifyKind(end.Value, DateTimeKind.Utc));
                        }
                    })));
                }
            })), HttpContext.RequestAborted);

        return response.Documents.Select(d => new EventSummaryResponse(
                d.Id,
                d.Name,
                d.Description,
                d.Date,
                new VenueResponse(d.VenueId, d.VenueName, d.VenueAddress, d.VenueCapacity),
                new PerformerResponse(d.PerformerId, d.PerformerName, d.PerformerType)))
            .ToList();
    }
}
