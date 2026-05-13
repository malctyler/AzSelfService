namespace AzSelfService.Worker.Data.Entities;

public sealed class ModuleEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string TerraformPath { get; set; } = string.Empty;
    public bool IsPublished { get; set; }
    public bool IsDeprecated { get; set; }
}