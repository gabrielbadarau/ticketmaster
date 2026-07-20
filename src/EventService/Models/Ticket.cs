namespace EventService.Models;

public class Ticket
{
    public Guid Id { get; set; }

    public Guid EventId { get; set; }
    public Event? Event { get; set; }

    public required string Seat { get; set; }
    public decimal Price { get; set; }

    // Availability here is just the DB's source of truth once a booking is
    // confirmed. The Booking Service (not yet built) is what actually prevents
    // double-booking during checkout, via a Redis TTL lock — not this column.
    public TicketStatus Status { get; set; } = TicketStatus.Available;

    public Guid? UserId { get; set; }
}
