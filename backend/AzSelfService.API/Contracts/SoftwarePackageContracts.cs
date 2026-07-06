using Microsoft.AspNetCore.Http;

namespace AzSelfService.API.Contracts;

public sealed class ValidateSoftwarePackageRequest
{
    public IFormFile? PackageFile { get; set; }
}

public sealed class UploadSoftwarePackageRequest
{
    public string Scope { get; set; } = "platform";
    public Guid? CustomerId { get; set; }
    public string StorageAccountName { get; set; } = string.Empty;
    public string ContainerName { get; set; } = "packages";
    public bool IsPublished { get; set; } = true;
    public IFormFile? PackageFile { get; set; }
}

public sealed class SoftwarePackageValidationResponse
{
    public bool IsValid { get; set; }
    public string? PackageId { get; set; }
    public string? Version { get; set; }
    public IReadOnlyList<string> Errors { get; set; } = [];
}

public sealed class PublishSoftwarePackageRequest
{
    public string Scope { get; set; } = "platform";
    public Guid? CustomerId { get; set; }
    public string PackageId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Publisher { get; set; } = string.Empty;
    public string Os { get; set; } = "windows";
    public string Architecture { get; set; } = "x64";
    public string InstallerType { get; set; } = "msi";
    public string BlobPath { get; set; } = string.Empty;
    public string ZipSha256 { get; set; } = string.Empty;
    public string? ManifestJson { get; set; }
    public bool IsPublished { get; set; } = true;
}

public sealed class SoftwarePackageCatalogItemResponse
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
    public bool IsPublished { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
