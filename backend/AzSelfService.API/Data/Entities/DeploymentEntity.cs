namespace AzSelfService.API.Data.Entities;

public sealed class DeploymentEntity
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Guid ModuleId { get; set; }
    public Guid RequestedBy { get; set; }
    public string Status { get; set; } = "QUEUED";
    public string? ErrorMessage { get; set; }
    public int RetryCount { get; set; }
    public string? TerraformStatePath { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public CustomerEntity? Customer { get; set; }
    public ModuleEntity? Module { get; set; }
    public UserEntity? RequestedByUser { get; set; }
    public DeploymentInputEntity? Input { get; set; }
    public DeploymentOutputEntity? Output { get; set; }
    public ICollection<DeploymentLogEntity> Logs { get; set; } = new List<DeploymentLogEntity>();
}