using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class ModulesController(AzSelfServiceDbContext dbContext) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ModuleSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ModuleSummaryResponse>>> GetModules(CancellationToken cancellationToken)
    {
        var modules = await dbContext.Modules
            .Where(x => x.IsPublished && !x.IsDeprecated)
            .OrderBy(x => x.Name)
            .ThenByDescending(x => x.Version)
            .Select(x => new ModuleSummaryResponse
            {
                Id = x.Id,
                Name = x.Name,
                Version = x.Version,
                TerraformPath = x.TerraformPath,
                Description = x.Description,
                IsPublished = x.IsPublished,
                IsDeprecated = x.IsDeprecated,
                Schema = JsonHelpers.ParseJsonOrEmpty(x.Schema),
                UiSchema = JsonHelpers.ParseNullableJson(x.UiSchema)
            })
            .ToListAsync(cancellationToken);

        return Ok(modules);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ModuleSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<ModuleSummaryResponse>> GetModuleById(Guid id, CancellationToken cancellationToken)
    {
        var module = await dbContext.Modules
            .Where(x => x.Id == id && x.IsPublished && !x.IsDeprecated)
            .Select(x => new ModuleSummaryResponse
            {
                Id = x.Id,
                Name = x.Name,
                Version = x.Version,
                TerraformPath = x.TerraformPath,
                Description = x.Description,
                IsPublished = x.IsPublished,
                IsDeprecated = x.IsDeprecated,
                Schema = JsonHelpers.ParseJsonOrEmpty(x.Schema),
                UiSchema = JsonHelpers.ParseNullableJson(x.UiSchema)
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        return Ok(module);
    }
}