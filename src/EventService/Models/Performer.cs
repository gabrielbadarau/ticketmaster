namespace EventService.Models;

public class Performer
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string Type { get; set; }

    public ICollection<Event> Events { get; set; } = [];
}
