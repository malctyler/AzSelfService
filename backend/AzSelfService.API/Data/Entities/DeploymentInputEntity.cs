namespace AzSelfService.API.Data.Entities;

public sealed class DeploymentInputEntity
{
    public Guid Id { get; set; }
    public Guid DeploymentId { get; set; }
    public string Inputs { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }

    public DeploymentEntity? Deployment { get; set; }
}