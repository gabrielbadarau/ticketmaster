namespace EventService.Models;

public class Event
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime Date { get; set; }

    public Guid VenueId { get; set; }
    public Venue? Venue { get; set; }

    public Guid PerformerId { get; set; }
    public Performer? Performer { get; set; }

    public ICollection<Ticket> Tickets { get; set; } = [];
}
