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
[Authorize]
public sealed class AdminModulesController(
    AzSelfServiceDbContext dbContext,
    ModuleManifestLoader manifestLoader) : ControllerBase
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

        var modules = await dbContext.Modules
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.Version)
            .Select(x => ToResponse(x))
            .ToListAsync(cancellationToken);

        return Ok(modules);
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
            module.Schema = manifest.SchemaJson;
            module.UiSchema = manifest.UiSchemaJson;
            module.Description = manifest.Description;
            module.IsPublished = true;
            module.IsDeprecated = false;
            module.UpdatedAt = now;

            await dbContext.SaveChangesAsync(cancellationToken);

            return Ok(ToResponse(module));
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
        return Ok(ToResponse(module));
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
        return Ok(ToResponse(module));
    }

    private static ModuleSummaryResponse ToResponse(ModuleEntity module)
    {
        return new ModuleSummaryResponse
        {
            Id = module.Id,
            Name = module.Name,
            Version = module.Version,
            TerraformPath = module.TerraformPath,
            Description = module.Description,
            IsPublished = module.IsPublished,
            IsDeprecated = module.IsDeprecated,
            Schema = JsonHelpers.ParseJsonOrEmpty(module.Schema),
            UiSchema = JsonHelpers.ParseNullableJson(module.UiSchema)
        };
    }
}