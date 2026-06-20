using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Services;
using AzSelfService.API.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ModulesController(
    AzSelfServiceDbContext dbContext,
    AllowedRegionCatalogService allowedRegionCatalogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModuleSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ModuleSummaryResponse>>> GetModules(CancellationToken cancellationToken)
    {
        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        var modules = await dbContext.Modules
            .Where(x => x.IsPublished && !x.IsDeprecated)
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.Version)
            .ToListAsync(cancellationToken);

        return Ok(modules.Select(x => ToResponse(x, allowedRegionCodes)).ToList());
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ModuleSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleSummaryResponse>> GetModuleById(Guid id, CancellationToken cancellationToken)
    {
        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == id && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

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