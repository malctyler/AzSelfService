namespace AzSelfService.Worker.Data.Entities;

public sealed class CustomerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public string? SpClientIdSecretRef { get; set; }
    public string? SpClientSecretSecretRef { get; set; }
    public string? SpTenantIdSecretRef { get; set; }
    public string? SpSubscriptionIdSecretRef { get; set; }
    public bool IsActive { get; set; }
}