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

            return Ok(new ModuleSummaryResponse
            {
                Id = module.Id,
                Name = module.Name,
                Version = module.Version,
                TerraformPath = module.TerraformPath,
                Description = module.Description,
                Schema = JsonHelpers.ParseJsonOrEmpty(module.Schema),
                UiSchema = JsonHelpers.ParseNullableJson(module.UiSchema)
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}