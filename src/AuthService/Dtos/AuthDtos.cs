namespace AuthService.Dtos;

public record RegisterRequest(string Email, string Password);

public record RegisterResponse(Guid UserId);

public record LoginRequest(string Email, string Password);

public record LoginResponse(string Token, DateTime ExpiresAt);
