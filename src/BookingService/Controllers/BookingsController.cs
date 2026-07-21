using BookingService.Data;
using BookingService.Dtos;
using BookingService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Stripe;

namespace BookingService.Controllers;

[ApiController]
[Route("[controller]")]
public class BookingsController(BookingDbContext db, IConnectionMultiplexer redis, PaymentIntentService payments) : ControllerBase
{
    private static readonly TimeSpan LockTtl = TimeSpan.FromMinutes(10);

    [HttpPost("reserve")]
    public async Task<ActionResult<ReserveBookingResponse>> Reserve(ReserveBookingRequest request)
    {
        if (request.TicketIds.Length == 0)
        {
            return BadRequest("At least one ticket id is required.");
        }

        var redisDb = redis.GetDatabase();
        var acquiredLockKeys = new List<string>();

        foreach (var ticketId in request.TicketIds)
        {
            var lockKey = $"ticket-lock:{ticketId}";
            var acquired = await redisDb.StringSetAsync(
                lockKey, request.UserId.ToString(), LockTtl, When.NotExists);

            if (!acquired)
            {
                await ReleaseLocksAsync(redisDb, acquiredLockKeys);
                return Conflict($"Ticket {ticketId} is currently locked by another booking attempt.");
            }

            acquiredLockKeys.Add(lockKey);
        }

        var tickets = await db.Tickets
            .Where(t => request.TicketIds.Contains(t.Id))
            .ToListAsync();

        if (tickets.Count != request.TicketIds.Length || tickets.Any(t => t.Status == TicketStatus.Booked))
        {
            await ReleaseLocksAsync(redisDb, acquiredLockKeys);
            return Conflict("One or more tickets are unavailable.");
        }

        var booking = new Booking
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            Status = BookingStatus.InProgress,
            TotalPrice = tickets.Sum(t => t.Price),
            BookingTickets = request.TicketIds.Select(id => new BookingTicket { TicketId = id }).ToList()
        };

        db.Bookings.Add(booking);
        await db.SaveChangesAsync();

        return new ReserveBookingResponse(booking.Id);
    }

    // Stands in for what a real frontend would do with Stripe.js/Elements:
    // collect card details, then confirm the payment client-side. We have no
    // frontend yet, so this creates AND confirms the PaymentIntent server-side
    // in one call, using Stripe's dedicated always-succeeds test card token.
    // The response here is purely informational — the webhook below is the
    // actual source of truth that flips the booking to Confirmed.
    [HttpPost("{id:guid}/pay")]
    public async Task<ActionResult<PayBookingResponse>> Pay(Guid id)
    {
        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == id);

        if (booking is null)
        {
            return NotFound();
        }

        if (booking.Status == BookingStatus.Confirmed)
        {
            return BadRequest("This booking is already confirmed.");
        }

        var paymentIntent = await payments.CreateAsync(new PaymentIntentCreateOptions
        {
            Amount = (long)(booking.TotalPrice * 100), // Stripe amounts are in cents, not dollars
            Currency = "usd",
            PaymentMethod = "pm_card_visa", // Stripe's built-in always-succeeds test card
            PaymentMethodTypes = ["card"],
            Confirm = true,
            Metadata = new Dictionary<string, string> { ["bookingId"] = booking.Id.ToString() }
        });

        return new PayBookingResponse(paymentIntent.Id, paymentIntent.Status);
    }

    private static async Task ReleaseLocksAsync(IDatabase redisDb, IEnumerable<string> lockKeys)
    {
        foreach (var key in lockKeys)
        {
            await redisDb.KeyDeleteAsync(key);
        }
    }
}
