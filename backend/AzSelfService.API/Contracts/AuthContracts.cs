namespace AzSelfService.API.Contracts;

public sealed class LoginRequest
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public sealed class LoginResponse
{
    public required string Token { get; init; }
    public required DateTime ExpiresAtUtc { get; init; }
    public required AuthenticatedUser User { get; init; }
}

public sealed class AuthenticatedUser
{
    public required Guid UserId { get; init; }
    public required Guid CustomerId { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public string? Email { get; init; }
}