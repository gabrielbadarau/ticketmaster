using BookingService.Models;
using Microsoft.EntityFrameworkCore;

namespace BookingService.Data;

public class BookingDbContext(DbContextOptions<BookingDbContext> options) : DbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<BookingTicket> BookingTickets => Set<BookingTicket>();
    public DbSet<Ticket> Tickets => Set<Ticket>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<BookingTicket>().HasKey(bt => new { bt.BookingId, bt.TicketId });

        // Tickets already exists as a real table, created by Event Service's own
        // migrations. ExcludeFromMigrations tells EF Core "map to this table for
        // queries/updates, but never generate CREATE/ALTER statements for it" —
        // otherwise this context's first migration would try to CREATE TABLE
        // "Tickets" and fail, since it already exists.
        modelBuilder.Entity<Ticket>().ToTable("Tickets", t => t.ExcludeFromMigrations());
    }
}
