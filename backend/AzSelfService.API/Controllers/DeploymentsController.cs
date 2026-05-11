using System.Text.Json;
using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class DeploymentsController(AzSelfServiceDbContext dbContext) : ControllerBase
{
    [HttpPost]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> CreateDeployment(
        [FromBody] CreateDeploymentRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == request.ModuleId && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        var now = DateTime.UtcNow;
        var deployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = module.Id,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = $"tfstate/customers/{customerId}/{module.Id}/{Guid.NewGuid()}.tfstate"
        };

        var input = new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deployment.Id,
            Inputs = JsonSerializer.Serialize(request.Inputs),
            CreatedAt = now
        };

        var initialLog = new DeploymentLogEntity
        {
            DeploymentId = deployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Deployment queued.",
            Context = JsonSerializer.Serialize(new { module = module.Name, moduleVersion = module.Version })
        };

        dbContext.Deployments.Add(deployment);
        dbContext.DeploymentInputs.Add(input);
        dbContext.DeploymentLogs.Add(initialLog);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = deployment.Id },
            new DeploymentCreatedResponse
            {
                Id = deployment.Id,
                Status = deployment.Status,
                CreatedAtUtc = deployment.CreatedAt
            });
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DeploymentDetailsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentDetailsResponse>> GetDeploymentById(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deployment = await dbContext.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .Include(x => x.Output)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (deployment is null || deployment.Module is null || deployment.Input is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        return Ok(new DeploymentDetailsResponse
        {
            Id = deployment.Id,
            ModuleId = deployment.ModuleId,
            ModuleName = deployment.Module.Name,
            ModuleVersion = deployment.Module.Version,
            Status = deployment.Status,
            ErrorMessage = deployment.ErrorMessage,
            RetryCount = deployment.RetryCount,
            TerraformStatePath = deployment.TerraformStatePath,
            CreatedAtUtc = deployment.CreatedAt,
            UpdatedAtUtc = deployment.UpdatedAt,
            CompletedAtUtc = deployment.CompletedAt,
            Inputs = JsonHelpers.ParseJsonOrEmpty(deployment.Input.Inputs),
            Outputs = JsonHelpers.ParseNullableJson(deployment.Output?.Outputs)
        });
    }

    [HttpGet("{id:guid}/logs")]
    [ProducesResponseType(typeof(IReadOnlyList<DeploymentLogResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<IReadOnlyList<DeploymentLogResponse>>> GetDeploymentLogs(
        Guid id,
        [FromQuery] long? sinceId,
        CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deploymentExists = await dbContext.Deployments
            .AnyAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (!deploymentExists)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        var logsQuery = dbContext.DeploymentLogs
            .Where(x => x.DeploymentId == id);

        if (sinceId.HasValue)
        {
            logsQuery = logsQuery.Where(x => x.Id > sinceId.Value);
        }

        var logs = await logsQuery
            .OrderBy(x => x.Id)
            .Take(200)
            .Select(x => new DeploymentLogResponse
            {
                Id = x.Id,
                TimestampUtc = x.Timestamp,
                Level = x.Level,
                Message = x.Message,
                Context = JsonHelpers.ParseNullableJson(x.Context)
            })
            .ToListAsync(cancellationToken);

        return Ok(logs);
    }
}