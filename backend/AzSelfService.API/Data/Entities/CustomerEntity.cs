namespace AzSelfService.API.Data.Entities;

public sealed class CustomerEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string SubscriptionId { get; set; } = string.Empty;
    public string TenantId { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }

    public ICollection<UserEntity> Users { get; set; } = new List<UserEntity>();
    public ICollection<DeploymentEntity> Deployments { get; set; } = new List<DeploymentEntity>();
}