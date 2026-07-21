namespace BookingService.Models;

// Booking Service's own minimal view of the Tickets table — which physically
// exists already, created by Event Service's migrations. This service only
// ever reads/updates existing columns (Status, UserId); it never creates or
// alters this table itself (see BookingDbContext's ExcludeFromMigrations).
public class Ticket
{
    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public decimal Price { get; set; }
    public TicketStatus Status { get; set; }
    public Guid? UserId { get; set; }
}
