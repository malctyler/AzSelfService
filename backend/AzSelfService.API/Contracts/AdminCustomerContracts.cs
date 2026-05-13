namespace AzSelfService.API.Contracts;

public sealed class OnboardCustomerRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? SpClientIdSecretRef { get; set; }
    public string? SpClientSecretSecretRef { get; set; }
    public string? SpTenantIdSecretRef { get; set; }
    public string? SpSubscriptionIdSecretRef { get; set; }
}

public sealed class OnboardCustomerResponse
{
    public required Guid CustomerId { get; init; }
    public required Guid UserId { get; init; }
    public required string Username { get; init; }
    public required string Role { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required string SpClientSecretSecretRefMasked { get; init; }
}