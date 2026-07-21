namespace BookingService.Dtos;

public record ReserveBookingRequest(Guid[] TicketIds, Guid UserId);

public record ReserveBookingResponse(Guid BookingId);
