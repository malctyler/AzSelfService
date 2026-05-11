namespace AzSelfService.API.Data.Entities;

public sealed class ModuleEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string TerraformPath { get; set; } = string.Empty;
    public string Schema { get; set; } = "{}";
    public string? UiSchema { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsPublished { get; set; }
    public bool IsDeprecated { get; set; }

    public ICollection<DeploymentEntity> Deployments { get; set; } = new List<DeploymentEntity>();
}