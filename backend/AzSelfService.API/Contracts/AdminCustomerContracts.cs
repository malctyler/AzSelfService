namespace AzSelfService.API.Contracts;

public sealed class OnboardCustomerRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string SpClientId { get; set; } = string.Empty;
    public string SpClientSecret { get; set; } = string.Empty;
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

public sealed class AdminCustomerSummaryResponse
{
    public required Guid CustomerId { get; init; }
    public required string CustomerName { get; init; }
    public required string SubscriptionId { get; init; }
    public required string TenantId { get; init; }
    public required bool IsActive { get; init; }
    public required string? Username { get; init; }
    public required string? Email { get; init; }
    public required string? SpClientIdSecretRef { get; init; }
    public required string? SpClientSecretSecretRefMasked { get; init; }
    public required string? SpTenantIdSecretRef { get; init; }
    public required string? SpSubscriptionIdSecretRef { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
}

public sealed class UpdateCustomerRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public string? Email { get; set; }
    public string? SpClientId { get; set; }
    public string? SpClientSecret { get; set; }
    public string? SpClientIdSecretRef { get; set; }
    public string? SpClientSecretSecretRef { get; set; }
    public string? SpTenantIdSecretRef { get; set; }
    public string? SpSubscriptionIdSecretRef { get; set; }
}