using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/software-packages")]
[Authorize]
public sealed class SoftwarePackagesController(AzSelfServiceDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<SoftwarePackageCatalogItemResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<SoftwarePackageCatalogItemResponse>>> GetCatalog(
        [FromQuery] string? scope,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "all" : scope.Trim().ToLowerInvariant();
        if (normalizedScope is not ("all" or "platform" or "customer"))
        {
            return BadRequest(new { message = "scope must be one of: all, platform, customer." });
        }

        var query = dbContext.SoftwarePackages
            .AsNoTracking()
            .Where(x => x.IsPublished)
            .AsQueryable();

        query = normalizedScope switch
        {
            "platform" => query.Where(x => x.Scope == "platform"),
            "customer" => query.Where(x => x.Scope == "customer" && x.CustomerId == customerId),
            _ => query.Where(x => x.Scope == "platform" || (x.Scope == "customer" && x.CustomerId == customerId))
        };

        var items = await query
            .OrderBy(x => x.PackageId)
            .ThenByDescending(x => x.Version)
            .Select(x => new SoftwarePackageCatalogItemResponse
            {
                Id = x.Id,
                Scope = x.Scope,
                CustomerId = x.CustomerId,
                PackageId = x.PackageId,
                Version = x.Version,
                DisplayName = x.DisplayName,
                Publisher = x.Publisher,
                Os = x.Os,
                Architecture = x.Architecture,
                InstallerType = x.InstallerType,
                BlobPath = x.BlobPath,
                ZipSha256 = x.ZipSha256,
                IsPublished = x.IsPublished,
                CreatedAt = x.CreatedAt,
                UpdatedAt = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(items);
    }
}
