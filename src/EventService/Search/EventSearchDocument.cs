namespace EventService.Search;

// Denormalized on purpose: Elasticsearch has no concept of a SQL join, so the
// Venue/Performer fields a search result needs are flattened directly onto the
// event document instead of referenced by id. The document id is the Event's
// own Id, so re-indexing an event is a plain upsert (index-by-id), and deleting
// an event is a plain delete-by-id.
public class EventSearchDocument
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime Date { get; set; }

    public Guid VenueId { get; set; }
    public required string VenueName { get; set; }
    public required string VenueAddress { get; set; }
    public int VenueCapacity { get; set; }

    public Guid PerformerId { get; set; }
    public required string PerformerName { get; set; }
    public required string PerformerType { get; set; }
}
