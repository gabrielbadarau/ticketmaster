namespace BookingService.Dtos;

public record JoinQueueRequest(Guid UserId);

public record JoinQueueResponse(int Position, int QueueSize);

public record QueueStatusResponse(bool Admitted, int? Position, int? QueueSize);
