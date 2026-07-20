namespace EventService.Models;

public class Venue
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Address { get; set; }
    public int Capacity { get; set; }

    // Raw JSON for now (sections/rows/seat coordinates) — not modeled as C# classes
    // yet since only the Booking Service's seat map UI will need to traverse its
    // internal structure. Revisit if/when that becomes necessary.
    public string? SeatMap { get; set; }

    public ICollection<Event> Events { get; set; } = [];
}
