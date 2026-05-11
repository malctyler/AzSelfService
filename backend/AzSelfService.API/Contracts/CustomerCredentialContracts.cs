namespace AzSelfService.API.Contracts;

public sealed class UpsertCustomerCredentialReferencesRequest
{
    public string? SpClientIdSecretRef { get; set; }
    public string SpClientSecretSecretRef { get; set; } = string.Empty;
    public string? SpTenantIdSecretRef { get; set; }
    public string? SpSubscriptionIdSecretRef { get; set; }
}

public sealed class CustomerCredentialReferencesResponse
{
    public required Guid CustomerId { get; init; }
    public string? SpClientIdSecretRefMasked { get; init; }
    public string? SpClientSecretSecretRefMasked { get; init; }
    public string? SpTenantIdSecretRefMasked { get; init; }
    public string? SpSubscriptionIdSecretRefMasked { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
}

public sealed class CustomerCredentialPreflightResponse
{
    public required Guid CustomerId { get; init; }
    public required bool CanProceed { get; init; }
    public required string Status { get; init; }
    public required IReadOnlyList<string> Issues { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public SecretExpirySummaryResponse? SecretExpiry { get; init; }
    public DateTime CheckedAtUtc { get; init; }
}

public sealed class SecretExpirySummaryResponse
{
    public DateTimeOffset? ClientSecretExpiresOn { get; init; }
    public bool ClientSecretExpired { get; init; }
    public bool ClientSecretNearExpiry { get; init; }
    public int WarningThresholdDays { get; init; }
}