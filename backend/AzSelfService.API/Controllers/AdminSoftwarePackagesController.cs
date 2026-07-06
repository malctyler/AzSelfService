using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/admin/software-packages")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminSoftwarePackagesController(
    AzSelfServiceDbContext dbContext,
    SoftwarePackageValidationService validationService,
    ISoftwarePackageBlobStorageService blobStorageService) : ControllerBase
{
    private static readonly Regex PackageIdRegex = new("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SemverRegex = new("^\\d+\\.\\d+\\.\\d+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex Sha256Regex = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [HttpPost("validate")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(SoftwarePackageValidationResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SoftwarePackageValidationResponse>> Validate(
        [FromForm] ValidateSoftwarePackageRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        if (request.PackageFile is null || request.PackageFile.Length == 0)
        {
            return BadRequest(new { message = "PackageFile is required." });
        }

        await using var stream = request.PackageFile.OpenReadStream();
        var result = await validationService.ValidateAsync(
            stream,
            request.PackageFile.FileName,
            cancellationToken);

        return Ok(new SoftwarePackageValidationResponse
        {
            IsValid = result.IsValid,
            PackageId = result.PackageId,
            Version = result.Version,
            Errors = result.Errors
        });
    }

    [HttpPost("upload")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(SoftwarePackageCatalogItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SoftwarePackageCatalogItemResponse>> Upload(
        [FromForm] UploadSoftwarePackageRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        if (request.PackageFile is null || request.PackageFile.Length == 0)
        {
            return BadRequest(new { message = "PackageFile is required." });
        }

        if (string.IsNullOrWhiteSpace(request.StorageAccountName))
        {
            return BadRequest(new { message = "StorageAccountName is required." });
        }

        if (string.IsNullOrWhiteSpace(request.ContainerName))
        {
            return BadRequest(new { message = "ContainerName is required." });
        }

        var normalizedScope = request.Scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("platform" or "customer"))
        {
            return BadRequest(new { message = "Scope must be either 'platform' or 'customer'." });
        }

        if (normalizedScope == "customer" && request.CustomerId is null)
        {
            return BadRequest(new { message = "CustomerId is required when scope is 'customer'." });
        }

        if (normalizedScope == "platform" && request.CustomerId is not null)
        {
            return BadRequest(new { message = "CustomerId must be null when scope is 'platform'." });
        }

        await using var memory = new MemoryStream();
        await request.PackageFile.CopyToAsync(memory, cancellationToken);

        memory.Position = 0;
        var validation = await validationService.ValidateAsync(memory, request.PackageFile.FileName, cancellationToken);
        if (!validation.IsValid)
        {
            return BadRequest(new SoftwarePackageValidationResponse
            {
                IsValid = false,
                PackageId = validation.PackageId,
                Version = validation.Version,
                Errors = validation.Errors
            });
        }

        if (string.IsNullOrWhiteSpace(validation.PackageId) || !PackageIdRegex.IsMatch(validation.PackageId))
        {
            return BadRequest(new { message = "Validated package is missing a valid packageId." });
        }

        if (string.IsNullOrWhiteSpace(validation.Version) || !SemverRegex.IsMatch(validation.Version))
        {
            return BadRequest(new { message = "Validated package is missing a valid semver version." });
        }

        var zipSha256 = ComputeSha256(memory);
        var blobPath = normalizedScope == "platform"
            ? $"catalog/platform/{validation.PackageId}/{validation.Version}/{request.PackageFile.FileName}"
            : $"catalog/customers/{request.CustomerId}/{validation.PackageId}/{validation.Version}/{request.PackageFile.FileName}";

        await blobStorageService.UploadAsync(
            request.StorageAccountName,
            request.ContainerName,
            blobPath,
            memory,
            cancellationToken);

        try
        {
            var entity = await UpsertPackageAsync(
                scope: normalizedScope,
                customerId: request.CustomerId,
                packageId: validation.PackageId,
                version: validation.Version,
                displayName: validation.DisplayName ?? validation.PackageId,
                publisher: validation.Publisher ?? "Unknown Publisher",
                os: validation.Os ?? "windows",
                architecture: validation.Architecture ?? "x64",
                installerType: validation.InstallerType ?? "msi",
                blobPath: blobPath,
                zipSha256: zipSha256,
                manifestJson: validation.ManifestJson,
                isPublished: request.IsPublished,
                cancellationToken: cancellationToken);

            return Ok(ToCatalogResponse(entity));
        }
        catch
        {
            await blobStorageService.DeleteIfExistsAsync(
                request.StorageAccountName,
                request.ContainerName,
                blobPath,
                cancellationToken);
            throw;
        }
    }

    [HttpPost("publish")]
    [ProducesResponseType(typeof(SoftwarePackageCatalogItemResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SoftwarePackageCatalogItemResponse>> Publish(
        [FromBody] PublishSoftwarePackageRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var scope = request.Scope.Trim().ToLowerInvariant();
        if (scope is not ("platform" or "customer"))
        {
            return BadRequest(new { message = "Scope must be either 'platform' or 'customer'." });
        }

        if (scope == "customer" && request.CustomerId is null)
        {
            return BadRequest(new { message = "CustomerId is required when scope is 'customer'." });
        }

        if (scope == "platform" && request.CustomerId is not null)
        {
            return BadRequest(new { message = "CustomerId must be null when scope is 'platform'." });
        }

        if (string.IsNullOrWhiteSpace(request.PackageId) || !PackageIdRegex.IsMatch(request.PackageId))
        {
            return BadRequest(new { message = "PackageId must use lowercase letters/numbers with optional dot or hyphen separators." });
        }

        if (string.IsNullOrWhiteSpace(request.Version) || !SemverRegex.IsMatch(request.Version))
        {
            return BadRequest(new { message = "Version must follow semver major.minor.patch." });
        }

        if (string.IsNullOrWhiteSpace(request.ZipSha256) || !Sha256Regex.IsMatch(request.ZipSha256))
        {
            return BadRequest(new { message = "ZipSha256 must be a 64-character hex string." });
        }

        if (string.IsNullOrWhiteSpace(request.BlobPath))
        {
            return BadRequest(new { message = "BlobPath is required." });
        }

        var expectedPrefix = scope == "platform"
            ? $"catalog/platform/{request.PackageId}/{request.Version}/"
            : $"catalog/customers/{request.CustomerId}/{request.PackageId}/{request.Version}/";

        if (!request.BlobPath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = $"BlobPath must start with '{expectedPrefix}'." });
        }

        var entity = await UpsertPackageAsync(
            scope,
            request.CustomerId,
            request.PackageId,
            request.Version,
            request.DisplayName,
            request.Publisher,
            request.Os,
            request.Architecture,
            request.InstallerType,
            request.BlobPath,
            request.ZipSha256,
            request.ManifestJson,
            request.IsPublished,
            cancellationToken);

        return Ok(ToCatalogResponse(entity));
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SoftwarePackageCatalogItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<SoftwarePackageCatalogItemResponse>>> GetCatalog(
        [FromQuery] string? scope,
        [FromQuery] Guid? customerId,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var query = dbContext.SoftwarePackages.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(scope))
        {
            var normalizedScope = scope.Trim().ToLowerInvariant();
            query = query.Where(x => x.Scope == normalizedScope);
        }

        if (customerId is not null)
        {
            query = query.Where(x => x.CustomerId == customerId);
        }

        var items = await query
            .OrderBy(x => x.PackageId)
            .ThenByDescending(x => x.Version)
            .Select(x => ToCatalogResponse(x))
            .ToListAsync(cancellationToken);

        return Ok(items);
    }

    private static SoftwarePackageCatalogItemResponse ToCatalogResponse(SoftwarePackageEntity entity)
    {
        return new SoftwarePackageCatalogItemResponse
        {
            Id = entity.Id,
            Scope = entity.Scope,
            CustomerId = entity.CustomerId,
            PackageId = entity.PackageId,
            Version = entity.Version,
            DisplayName = entity.DisplayName,
            Publisher = entity.Publisher,
            Os = entity.Os,
            Architecture = entity.Architecture,
            InstallerType = entity.InstallerType,
            BlobPath = entity.BlobPath,
            ZipSha256 = entity.ZipSha256,
            IsPublished = entity.IsPublished,
            CreatedAt = entity.CreatedAt,
            UpdatedAt = entity.UpdatedAt
        };
    }

    private async Task<SoftwarePackageEntity> UpsertPackageAsync(
        string scope,
        Guid? customerId,
        string packageId,
        string version,
        string displayName,
        string publisher,
        string os,
        string architecture,
        string installerType,
        string blobPath,
        string zipSha256,
        string? manifestJson,
        bool isPublished,
        CancellationToken cancellationToken)
    {
        var entity = await dbContext.SoftwarePackages.SingleOrDefaultAsync(
            x => x.Scope == scope
                && x.CustomerId == customerId
                && x.PackageId == packageId
                && x.Version == version,
            cancellationToken);

        var now = DateTime.UtcNow;
        if (entity is null)
        {
            entity = new SoftwarePackageEntity
            {
                Id = Guid.NewGuid(),
                Scope = scope,
                CustomerId = customerId,
                PackageId = packageId,
                Version = version,
                CreatedAt = now
            };
            dbContext.SoftwarePackages.Add(entity);
        }

        entity.DisplayName = displayName;
        entity.Publisher = publisher;
        entity.Os = os;
        entity.Architecture = architecture;
        entity.InstallerType = installerType;
        entity.BlobPath = blobPath;
        entity.ZipSha256 = zipSha256.ToLowerInvariant();
        entity.ManifestJson = manifestJson;
        entity.IsPublished = isPublished;
        entity.UpdatedAt = now;

        await dbContext.SaveChangesAsync(cancellationToken);
        return entity;
    }

    private static string ComputeSha256(Stream stream)
    {
        stream.Position = 0;
        var hash = SHA256.HashData(stream);
        stream.Position = 0;
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
