using System.Text.Json;
using System.Text.RegularExpressions;
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
[Route("api/[controller]")]
[Authorize]
public sealed class DeploymentsController(
    AzSelfServiceDbContext dbContext,
    CustomerCredentialPreflightService preflightService,
    StorageAccountNameAvailabilityService? storageAccountNameAvailabilityService = null) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ManagedResourceResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ManagedResourceResponse>>> GetManagedResources(CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();

        var deployments = await dbContext.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .Include(x => x.Output)
            .Where(x => x.CustomerId == customerId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.UpdatedAt)
            .ToListAsync(cancellationToken);

        var managedResources = deployments
            .Where(x => !string.IsNullOrWhiteSpace(x.TerraformStatePath))
            .GroupBy(x => NormalizeStatePath(x.TerraformStatePath!), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Select(ToManagedResourceResponse)
            .ToList();

        return Ok(managedResources);
    }

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

        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
        {
            return BadRequest(new { message = "Customer is not active or does not exist." });
        }

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef)
            || string.IsNullOrWhiteSpace(customer.TenantId)
            || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return BadRequest(new
            {
                message = "Customer service principal Key Vault secret references are not configured.",
                required = new[]
                {
                    "sp_client_secret_secret_ref",
                    "tenant_id",
                    "subscription_id"
                }
            });
        }

        var module = await dbContext.Modules
            .SingleOrDefaultAsync(x => x.Id == request.ModuleId && x.IsPublished && !x.IsDeprecated, cancellationToken);

        if (module is null)
        {
            return NotFound(new { message = "Module not found." });
        }

        var validationError = ValidateInputsAgainstSchema(module.Schema, request.Inputs);
        if (validationError is not null)
        {
            return BadRequest(new { message = validationError });
        }

        // Fast-fail for globally unavailable storage account names before full credential preflight.
        if (string.Equals(module.Name, "storage-account", StringComparison.OrdinalIgnoreCase)
            && storageAccountNameAvailabilityService is not null
            && request.Inputs.TryGetProperty("name", out var storageAccountNameElement)
            && storageAccountNameElement.ValueKind == JsonValueKind.String)
        {
            var storageAccountName = storageAccountNameElement.GetString() ?? string.Empty;
            var nameAvailability = await storageAccountNameAvailabilityService.CheckAsync(
                customer,
                storageAccountName,
                cancellationToken);

            if (!nameAvailability.IsAvailable)
            {
                return BadRequest(new
                {
                    message = nameAvailability.Message ?? "Storage account name is not available globally."
                });
            }
        }

        var preflight = await preflightService.CheckAsync(customer, cancellationToken);
        if (!preflight.CanProceed)
        {
            return BadRequest(new
            {
                message = "Deployment blocked by credential preflight checks.",
                issues = preflight.Issues,
                warnings = preflight.Warnings
            });
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

    [HttpPost("check-storage-name")]
    [ProducesResponseType(typeof(StorageNameAvailabilityCheckResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<StorageNameAvailabilityCheckResponse>> CheckStorageAccountName(
        [FromBody] CheckStorageAccountNameRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            return BadRequest(new { message = "Storage account name is required." });
        }

        if (storageAccountNameAvailabilityService is null)
        {
            return BadRequest(new { message = "Storage account availability check is not configured." });
        }

        var customerId = User.GetRequiredCustomerId();
        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

        if (customer is null)
        {
            return BadRequest(new { message = "Customer is not active or does not exist." });
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) 
            || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return BadRequest(new
            {
                message = "Customer subscription/tenant configuration is incomplete."
            });
        }

        var result = await storageAccountNameAvailabilityService.CheckAsync(customer, request.Name, cancellationToken);
        
        return Ok(new StorageNameAvailabilityCheckResponse
        {
            IsAvailable = result.IsAvailable,
            Message = result.Message,
            NameChecked = request.Name
        });
    }

    private static string? ValidateInputsAgainstSchema(string? schemaJson, JsonElement inputs)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || inputs.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        using var schemaDocument = JsonDocument.Parse(schemaJson);
        var root = schemaDocument.RootElement;

        if (root.TryGetProperty("required", out var required)
            && required.ValueKind == JsonValueKind.Array)
        {
            foreach (var requiredField in required.EnumerateArray())
            {
                var fieldName = requiredField.GetString();
                if (string.IsNullOrWhiteSpace(fieldName))
                {
                    continue;
                }

                if (!inputs.TryGetProperty(fieldName, out var value)
                    || value.ValueKind == JsonValueKind.Null
                    || (value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())))
                {
                    return $"{fieldName} is required.";
                }
            }
        }

        if (!root.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var schemaProperty in properties.EnumerateObject())
        {
            if (!inputs.TryGetProperty(schemaProperty.Name, out var inputValue))
            {
                continue;
            }

            var propertySchema = schemaProperty.Value;

            if (propertySchema.TryGetProperty("type", out var typeElement))
            {
                var propertyType = typeElement.GetString();
                if (string.Equals(propertyType, "object", StringComparison.OrdinalIgnoreCase)
                    && inputValue.ValueKind != JsonValueKind.Object)
                {
                    return propertySchema.TryGetProperty("description", out var objectDescription)
                        ? objectDescription.GetString()
                        : $"{schemaProperty.Name} must be an object.";
                }
            }

            if (inputValue.ValueKind == JsonValueKind.String
                && propertySchema.TryGetProperty("pattern", out var patternElement))
            {
                var pattern = patternElement.GetString();
                var rawValue = inputValue.GetString() ?? string.Empty;

                if (!string.IsNullOrWhiteSpace(pattern) && !Regex.IsMatch(rawValue, pattern))
                {
                    if (propertySchema.TryGetProperty("validationMessage", out var validationMessage)
                        && !string.IsNullOrWhiteSpace(validationMessage.GetString()))
                    {
                        return validationMessage.GetString();
                    }

                    if (propertySchema.TryGetProperty("description", out var description)
                        && !string.IsNullOrWhiteSpace(description.GetString()))
                    {
                        return description.GetString();
                    }

                    return $"{schemaProperty.Name} is invalid.";
                }
            }
        }

        return null;
    }

    [HttpPost("{id:guid}/destroy")]
    [ProducesResponseType(typeof(DeploymentCreatedResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<DeploymentCreatedResponse>> DestroyDeployment(Guid id, CancellationToken cancellationToken)
    {
        var customerId = User.GetRequiredCustomerId();
        var userId = User.GetRequiredUserId();

        var target = await dbContext.Deployments
            .Include(x => x.Input)
            .Include(x => x.Module)
            .SingleOrDefaultAsync(x => x.Id == id && x.CustomerId == customerId, cancellationToken);

        if (target is null || target.Input is null || target.Module is null)
        {
            return NotFound(new { message = "Deployment not found." });
        }

        if (!string.Equals(target.Status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase))
        {
            return BadRequest(new { message = "Only succeeded deployments can be destroyed." });
        }

        if (string.IsNullOrWhiteSpace(target.TerraformStatePath))
        {
            return BadRequest(new { message = "Deployment has no terraform state path for destroy operation." });
        }

        using var sourceInput = JsonDocument.Parse(target.Input.Inputs);
        var sourceJson = sourceInput.RootElement;
        var payload = sourceJson.ValueKind == JsonValueKind.Object
            ? sourceJson.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.Clone())
            : new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        payload["__operation"] = JsonSerializer.SerializeToElement("destroy");
        payload["__targetDeploymentId"] = JsonSerializer.SerializeToElement(target.Id);

        var now = DateTime.UtcNow;
        var destroyDeployment = new DeploymentEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            ModuleId = target.ModuleId,
            RequestedBy = userId,
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = target.TerraformStatePath
        };

        dbContext.Deployments.Add(destroyDeployment);
        dbContext.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = destroyDeployment.Id,
            Inputs = JsonSerializer.Serialize(payload),
            CreatedAt = now
        });
        dbContext.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = destroyDeployment.Id,
            Timestamp = now,
            Level = "INFO",
            Message = "Destroy deployment queued.",
            Context = JsonSerializer.Serialize(new
            {
                targetDeploymentId = target.Id,
                module = target.Module.Name,
                moduleVersion = target.Module.Version,
                operation = "destroy"
            })
        });

        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(GetDeploymentById),
            new { id = destroyDeployment.Id },
            new DeploymentCreatedResponse
            {
                Id = destroyDeployment.Id,
                Status = destroyDeployment.Status,
                CreatedAtUtc = destroyDeployment.CreatedAt
            });
    }

    private static ManagedResourceResponse ToManagedResourceResponse(DeploymentEntity deployment)
    {
        var inputs = JsonHelpers.ParseNullableJson(deployment.Input?.Inputs);
        var outputs = JsonHelpers.ParseNullableJson(deployment.Output?.Outputs);

        var resourceName = GetJsonString(outputs, "name")
            ?? GetJsonString(inputs, "name");

        var resourceLocation = GetJsonString(outputs, "location")
            ?? GetJsonString(inputs, "location");

        var resourceId = GetJsonString(outputs, "id");

        return new ManagedResourceResponse
        {
            DeploymentId = deployment.Id,
            ModuleId = deployment.ModuleId,
            ModuleName = deployment.Module?.Name ?? string.Empty,
            ModuleVersion = deployment.Module?.Version ?? string.Empty,
            Status = deployment.Status,
            ResourceName = resourceName ?? deployment.Module?.Name ?? deployment.Id.ToString("N"),
            ResourceLocation = resourceLocation ?? string.Empty,
            ResourceId = resourceId ?? string.Empty,
            TerraformStatePath = deployment.TerraformStatePath,
            CreatedAtUtc = deployment.CreatedAt,
            UpdatedAtUtc = deployment.UpdatedAt,
            CompletedAtUtc = deployment.CompletedAt
        };
    }

    private static string NormalizeStatePath(string statePath)
        => statePath.Replace('\\', '/').Trim();

    private static string? GetJsonString(JsonElement? element, string propertyName)
    {
        if (element is null || element.Value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!element.Value.TryGetProperty(propertyName, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Object && property.TryGetProperty("value", out var valueElement))
        {
            if (valueElement.ValueKind == JsonValueKind.String)
            {
                return valueElement.GetString();
            }

            return valueElement.ToString();
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString();
        }

        return property.ToString();
    }
}