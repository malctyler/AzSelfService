namespace AzSelfService.API.Data.Entities;

public sealed class SoftwarePackageEntity
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public Guid? CustomerId { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Os { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string InstallerType { get; set; } = string.Empty;
    public string BlobPath { get; set; } = string.Empty;
    public string ZipSha256 { get; set; } = string.Empty;
    public string? ManifestJson { get; set; }
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public CustomerEntity? Customer { get; set; }
}
