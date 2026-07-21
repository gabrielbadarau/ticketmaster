namespace EventService.Dtos;

public record EventSummaryResponse(
    Guid Id,
    string Name,
    string? Description,
    DateTime Date,
    VenueResponse Venue,
    PerformerResponse Performer);
