using EventService.Models;
using Microsoft.EntityFrameworkCore;

namespace EventService.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(EventDbContext db)
    {
        if (await db.Events.AnyAsync())
        {
            return;
        }

        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            Name = "Madison Square Garden",
            Address = "4 Pennsylvania Plaza, New York, NY",
            Capacity = 20000
        };

        var performer = new Performer
        {
            Id = Guid.NewGuid(),
            Name = "Taylor Swift",
            Type = "Artist"
        };

        var @event = new Event
        {
            Id = Guid.NewGuid(),
            Name = "The Eras Tour",
            Description = "Taylor Swift performing live",
            Date = DateTime.UtcNow.AddMonths(2),
            Venue = venue,
            Performer = performer,
            Tickets =
            [
                new Ticket { Id = Guid.NewGuid(), Seat = "A1", Price = 250m },
                new Ticket { Id = Guid.NewGuid(), Seat = "A2", Price = 250m },
                new Ticket { Id = Guid.NewGuid(), Seat = "B1", Price = 150m }
            ]
        };

        db.Events.Add(@event);
        await db.SaveChangesAsync();
    }
}
