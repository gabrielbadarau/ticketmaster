namespace BookingService.Models;

// Join entity linking a Booking to the tickets it covers (a booking can span
// multiple seats in one transaction). Deliberately no navigation property to
// Ticket and no DB-level foreign key constraint against Tickets — that table
// is owned by Event Service's migrations, so this is an application-level
// reference only, not one EF Core would try to enforce or migrate.
public class BookingTicket
{
    public Guid BookingId { get; set; }
    public Booking? Booking { get; set; }

    public Guid TicketId { get; set; }
}
