using BookingService.Data;
using BookingService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using Stripe;

namespace BookingService.Controllers;

[ApiController]
[Route("webhooks/stripe")]
public class StripeWebhookController(BookingDbContext db, IConnectionMultiplexer redis, IConfiguration config)
    : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Handle()
    {
        using var reader = new StreamReader(Request.Body);
        var json = await reader.ReadToEndAsync();

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(
                json,
                Request.Headers["Stripe-Signature"],
                config["Stripe:WebhookSecret"],
                throwOnApiVersionMismatch: false);
        }
        catch (StripeException)
        {
            return BadRequest("Invalid Stripe signature.");
        }

        if (stripeEvent.Type != "payment_intent.succeeded")
        {
            // We don't care about other event types, but Stripe expects a 2xx
            // response regardless, or it will keep retrying delivery.
            return Ok();
        }

        var paymentIntent = (PaymentIntent)stripeEvent.Data.Object;

        if (!paymentIntent.Metadata.TryGetValue("bookingId", out var bookingIdText) ||
            !Guid.TryParse(bookingIdText, out var bookingId))
        {
            return Ok();
        }

        var booking = await db.Bookings.FirstOrDefaultAsync(b => b.Id == bookingId);

        if (booking is null || booking.Status == BookingStatus.Confirmed)
        {
            // Already handled (Stripe may deliver the same event more than once)
            // or refers to a booking we don't know about — either way, nothing to do.
            return Ok();
        }

        var ticketIds = await db.BookingTickets
            .Where(bt => bt.BookingId == booking.Id)
            .Select(bt => bt.TicketId)
            .ToListAsync();

        var tickets = await db.Tickets
            .Where(t => ticketIds.Contains(t.Id))
            .ToListAsync();

        foreach (var ticket in tickets)
        {
            ticket.Status = TicketStatus.Booked;
            ticket.UserId = booking.UserId;
        }

        booking.Status = BookingStatus.Confirmed;
        await db.SaveChangesAsync();

        var redisDb = redis.GetDatabase();
        foreach (var ticketId in ticketIds)
        {
            await redisDb.KeyDeleteAsync($"ticket-lock:{ticketId}");
        }

        return Ok();
    }
}
