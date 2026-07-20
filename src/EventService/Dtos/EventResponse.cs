using EventService.Models;

namespace EventService.Dtos;

public record EventResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTime Date,
    VenueResponse Venue,
    PerformerResponse Performer,
    IReadOnlyList<TicketResponse> Tickets);

public record VenueResponse(Guid Id, string Name, string Address, int Capacity);

public record PerformerResponse(Guid Id, string Name, string Type);

public record TicketResponse(Guid Id, string Seat, decimal Price, TicketStatus Status);
