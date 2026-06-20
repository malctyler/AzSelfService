using AzSelfService.API.Contracts;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/admin/regions")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminRegionsController(AllowedRegionCatalogService allowedRegionCatalogService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AllowedRegionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AllowedRegionResponse>>> GetAllowedRegions(CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var regions = await allowedRegionCatalogService.GetAllowedRegionsAsync(cancellationToken);
        return Ok(regions);
    }

    [HttpPut]
    [ProducesResponseType(typeof(IReadOnlyList<AllowedRegionResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<IReadOnlyList<AllowedRegionResponse>>> ReplaceAllowedRegions(
        [FromBody] UpdateAllowedRegionsRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        try
        {
            var regions = await allowedRegionCatalogService.ReplaceAllowedRegionsAsync(request.Codes, cancellationToken);
            return Ok(regions);
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { message = ex.Message });
        }
    }
}