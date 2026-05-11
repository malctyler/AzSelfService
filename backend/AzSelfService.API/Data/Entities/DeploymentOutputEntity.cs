namespace AzSelfService.API.Data.Entities;

public sealed class DeploymentOutputEntity
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public string Outputs { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }

    public DeploymentEntity? Deployment { get; set; }
}