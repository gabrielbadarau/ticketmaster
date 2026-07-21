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

        var events = new[]
        {
            BuildEvent(
                "The Eras Tour", "Taylor Swift performing live", DateTime.UtcNow.AddMonths(2),
                "Madison Square Garden", "4 Pennsylvania Plaza, New York, NY", 20000,
                "Taylor Swift", "Artist",
                [("A1", 250m), ("A2", 250m), ("B1", 150m)]),

            BuildEvent(
                "Renaissance World Tour", "Beyoncé performing live", DateTime.UtcNow.AddMonths(3),
                "SoFi Stadium", "1001 Stadium Dr, Inglewood, CA", 70000,
                "Beyoncé", "Artist",
                [("A1", 300m), ("A2", 300m), ("B1", 180m)]),

            BuildEvent(
                "World Series Game 7", "Championship deciding game", DateTime.UtcNow.AddMonths(1),
                "Yankee Stadium", "1 E 161st St, Bronx, NY", 47000,
                "New York Yankees", "Team",
                [("C1", 500m), ("C2", 500m), ("D1", 220m)]),

            BuildEvent(
                "Hamilton", "The story of America then, told by America now", DateTime.UtcNow.AddMonths(4),
                "Richard Rodgers Theatre", "226 W 46th St, New York, NY", 1400,
                "Hamilton Cast", "Theater",
                [("Orch-A1", 350m), ("Orch-A2", 350m), ("Mezz-B1", 150m)]),

            BuildEvent(
                "Coachella 2026, Weekend 1", "Annual music and arts festival", DateTime.UtcNow.AddMonths(5),
                "Empire Polo Club", "81-800 Ave 51, Indio, CA", 125000,
                "Various Artists", "Festival",
                [("GA-1", 500m), ("GA-2", 500m), ("VIP-1", 1200m)])
        };

        db.Events.AddRange(events);
        await db.SaveChangesAsync();
    }

    private static Event BuildEvent(
        string name, string description, DateTime date,
        string venueName, string venueAddress, int venueCapacity,
        string performerName, string performerType,
        (string Seat, decimal Price)[] tickets) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Description = description,
            Date = date,
            Venue = new Venue
            {
                Id = Guid.NewGuid(),
                Name = venueName,
                Address = venueAddress,
                Capacity = venueCapacity
            },
            Performer = new Performer
            {
                Id = Guid.NewGuid(),
                Name = performerName,
                Type = performerType
            },
            Tickets = tickets.Select(t => new Ticket
            {
                Id = Guid.NewGuid(),
                Seat = t.Seat,
                Price = t.Price
            }).ToList()
        };
}
