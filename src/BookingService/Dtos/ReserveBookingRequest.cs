namespace BookingService.Dtos;

public record ReserveBookingRequest(Guid[] TicketIds);

public record ReserveBookingResponse(Guid BookingId);
