using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using AzSelfService.API.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/admin/modules")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminModulesController(
    AzSelfServiceDbContext dbContext,
    ModuleManifestLoader manifestLoader,
    AllowedRegionCatalogService allowedRegionCatalogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModuleSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<ModuleSummaryResponse>>> GetAllModules(CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        var modules = await dbContext.Modules
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

        return Ok(modules.Select(x => ToResponse(x, allowedRegionCodes)).ToList());
    }

    [HttpPost("register")]
    [ProducesResponseType(typeof(ModuleSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<ModuleSummaryResponse>> RegisterModule(
        [FromBody] RegisterModuleRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        try
        {
            var manifest = await manifestLoader.LoadAsync(request.ModulePath, cancellationToken);
            var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);

            var module = await dbContext.Modules.SingleOrDefaultAsync(
                x => x.Name == manifest.Name && x.Version == manifest.Version,
                cancellationToken);

            var now = DateTime.UtcNow;
            if (module is null)
            {
                module = new ModuleEntity
                {
                    Id = Guid.NewGuid(),
                    Name = manifest.Name,
                    Version = manifest.Version,
                    CreatedAt = now
                };
                dbContext.Modules.Add(module);
            }

            module.TerraformPath = manifest.TerraformPath;
            module.Schema = allowedRegionCatalogService.ApplyAllowedRegionsToSchemaJson(manifest.SchemaJson, allowedRegionCodes) ?? manifest.SchemaJson;
            module.UiSchema = manifest.UiSchemaJson;
            module.Description = manifest.Description;
            module.IsPublished = manifest.IsPublished;
            module.IsDeprecated = manifest.IsDeprecated;
            module.UpdatedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken);

            return Ok(ToResponse(module, allowedRegionCodes));
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }

    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(typeof(ModuleSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleSummaryResponse>> PublishModule(Guid id, CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var module = await dbContext.Modules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        module.IsPublished = true;
        module.IsDeprecated = false;
        module.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        return Ok(ToResponse(module, allowedRegionCodes));
    }

    [HttpPost("{id:guid}/deprecate")]
    [ProducesResponseType(typeof(ModuleSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleSummaryResponse>> DeprecateModule(Guid id, CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var module = await dbContext.Modules.SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        module.IsPublished = false;
        module.IsDeprecated = true;
        module.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);
        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        return Ok(ToResponse(module, allowedRegionCodes));
    }

    private ModuleSummaryResponse ToResponse(ModuleEntity module, IReadOnlyList<string> allowedRegionCodes)
    {
        var schemaJson = allowedRegionCatalogService.ApplyAllowedRegionsToSchemaJson(module.Schema, allowedRegionCodes) ?? module.Schema;

        return new ModuleSummaryResponse
        {
            Id = module.Id,
            Name = module.Name,
            Version = module.Version,
            TerraformPath = module.TerraformPath,
            Description = module.Description,
            IsPublished = module.IsPublished,
            IsDeprecated = module.IsDeprecated,
            Schema = JsonHelpers.ParseJsonOrEmpty(schemaJson),
            UiSchema = JsonHelpers.ParseNullableJson(module.UiSchema)
        };
    }
}