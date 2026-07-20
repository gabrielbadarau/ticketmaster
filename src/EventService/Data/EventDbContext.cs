using EventService.Models;
using Microsoft.EntityFrameworkCore;

namespace EventService.Data;

public class EventDbContext(DbContextOptions<EventDbContext> options) : DbContext(options)
{
    public DbSet<Event> Events => Set<Event>();
    public DbSet<Venue> Venues => Set<Venue>();
    public DbSet<Performer> Performers => Set<Performer>();
    public DbSet<Ticket> Tickets => Set<Ticket>();
}
