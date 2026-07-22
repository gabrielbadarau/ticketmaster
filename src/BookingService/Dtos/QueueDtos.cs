namespace BookingService.Dtos;

public record JoinQueueResponse(int Position, int QueueSize);

public record QueueStatusResponse(bool Admitted, int? Position, int? QueueSize);
