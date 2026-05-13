using System.Text.Json;
using AzSelfService.Worker.Data;
using AzSelfService.Worker.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzSelfService.Worker.Services;

public sealed class DeploymentProcessor(
    IServiceScopeFactory scopeFactory,
    ILogger<DeploymentProcessor> logger,
    ServicePrincipalCredentialProvider credentialProvider,
    TerraformExecutionService terraformExecutionService,
    IOptions<WorkerOptions> options) : BackgroundService
{
    private readonly WorkerOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Worker started. Polling every {PollIntervalMs}ms", _options.PollIntervalMs);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProcessBatchAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Worker loop error.");
            }

            await Task.Delay(_options.PollIntervalMs, stoppingToken);
        }
    }

    private async Task ProcessBatchAsync(CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();

        var queuedDeployments = await db.Deployments
            .Where(x => x.Status == "QUEUED")
            .OrderBy(x => x.CreatedAt)
            .Take(_options.BatchSize)
            .ToListAsync(cancellationToken);

        foreach (var deployment in queuedDeployments)
        {
            await ProcessDeploymentAsync(deployment.Id, cancellationToken);
        }
    }

    private async Task ProcessDeploymentAsync(Guid deploymentId, CancellationToken cancellationToken)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();

        var deployment = await db.Deployments
            .Include(x => x.Module)
            .Include(x => x.Input)
            .SingleOrDefaultAsync(x => x.Id == deploymentId, cancellationToken);

        if (deployment is null)
        {
            logger.LogWarning("Deployment {DeploymentId} not found.", deploymentId);
            return;
        }

        var now = DateTime.UtcNow;
        deployment.Status = "RUNNING";
        deployment.UpdatedAt = now;
        await db.SaveChangesAsync(cancellationToken);

        await WriteLogAsync(db, deployment.Id, "INFO", "Worker picked deployment from queue.", new
        {
            deploymentId = deployment.Id,
            moduleId = deployment.ModuleId,
            module = deployment.Module?.Name,
            moduleVersion = deployment.Module?.Version
        }, cancellationToken);

        try
        {
            var customer = await db.Customers
                .SingleOrDefaultAsync(x => x.Id == deployment.CustomerId && x.IsActive, cancellationToken);

            if (customer is null)
            {
                throw new InvalidOperationException("Deployment customer record is missing or inactive.");
            }

            var credentialResolution = await credentialProvider.ResolveAsync(
                customer,
                _options.SecretExpiryWarningDays,
                cancellationToken);

            if (credentialResolution.SecretMetadata.ClientSecretExpired)
            {
                throw new InvalidOperationException("Customer service principal secret is expired.");
            }

            if (credentialResolution.SecretMetadata.ClientSecretNearExpiry)
            {
                await WriteLogAsync(db, deployment.Id, "WARN", "Customer service principal secret is nearing expiry.", new
                {
                    expiresOn = credentialResolution.SecretMetadata.ClientSecretExpiresOn,
                    warningWindowDays = _options.SecretExpiryWarningDays,
                    clientSecretRef = credentialResolution.SecretMetadata.ClientSecretRef
                }, cancellationToken);
            }

            await WriteLogAsync(db, deployment.Id, "INFO", "Credential preflight checks passed.", new
            {
                clientIdRef = credentialResolution.SecretMetadata.ClientIdRef,
                clientSecretRef = credentialResolution.SecretMetadata.ClientSecretRef,
                tenantIdRef = credentialResolution.SecretMetadata.TenantIdRef,
                subscriptionIdRef = credentialResolution.SecretMetadata.SubscriptionIdRef,
                clientSecretExpiresOn = credentialResolution.SecretMetadata.ClientSecretExpiresOn
            }, cancellationToken);

            await WriteLogAsync(db, deployment.Id, "INFO", "Terraform execution mode selected.", new
            {
                mode = _options.TerraformExecutionMode
            }, cancellationToken);

            var operation = ResolveOperation(deployment.Input?.Inputs);

            await WriteLogAsync(db, deployment.Id, "INFO", "Terraform operation selected.", new
            {
                operation
            }, cancellationToken);

            var outputPayload = IsRealExecutionMode(_options.TerraformExecutionMode)
                ? await terraformExecutionService.ExecuteAsync(
                    deployment,
                    operation,
                    credentialResolution.Credentials,
                    (level, message, context, ct) => WriteLogAsync(db, deployment.Id, level, message, context, ct),
                    cancellationToken)
                : await SimulateTerraformRunAsync(db, deployment, credentialResolution.Credentials, operation, cancellationToken);

            await UpsertDeploymentOutputAsync(db, deployment.Id, outputPayload, cancellationToken);

            deployment.Status = string.Equals(operation, TerraformExecutionService.OperationDestroy, StringComparison.OrdinalIgnoreCase)
                ? "ROLLED_BACK"
                : "SUCCEEDED";
            deployment.ErrorMessage = null;
            deployment.CompletedAt = DateTime.UtcNow;
            deployment.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(cancellationToken);

            await WriteLogAsync(db, deployment.Id, "INFO", "Deployment completed successfully.", null, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Deployment {DeploymentId} failed.", deployment.Id);

            deployment.RetryCount += 1;
            deployment.ErrorMessage = ex.Message;
            deployment.UpdatedAt = DateTime.UtcNow;

            if (deployment.RetryCount < _options.MaxRetries)
            {
                deployment.Status = "QUEUED";
                await db.SaveChangesAsync(cancellationToken);
                await WriteLogAsync(db, deployment.Id, "WARN", "Deployment failed, re-queued for retry.", new
                {
                    retries = deployment.RetryCount,
                    maxRetries = _options.MaxRetries
                }, cancellationToken);
            }
            else
            {
                deployment.Status = "FAILED";
                deployment.CompletedAt = DateTime.UtcNow;
                await db.SaveChangesAsync(cancellationToken);
                await WriteLogAsync(db, deployment.Id, "ERROR", "Deployment failed and reached max retries.", new
                {
                    retries = deployment.RetryCount,
                    maxRetries = _options.MaxRetries,
                    error = ex.Message
                }, cancellationToken);
            }
        }
    }

    private static async Task<string> SimulateTerraformRunAsync(
        WorkerDbContext db,
        DeploymentEntity deployment,
        ServicePrincipalCredentials credentials,
        string operation,
        CancellationToken cancellationToken)
    {
        await WriteLogAsync(db, deployment.Id, "INFO", "terraform init", null, cancellationToken);
        await Task.Delay(700, cancellationToken);

        if (string.Equals(operation, TerraformExecutionService.OperationDestroy, StringComparison.OrdinalIgnoreCase))
        {
            await WriteLogAsync(db, deployment.Id, "INFO", "terraform destroy -auto-approve", null, cancellationToken);
            await Task.Delay(1200, cancellationToken);

            return JsonSerializer.Serialize(new
            {
                deploymentId = deployment.Id,
                moduleId = deployment.ModuleId,
                simulated = true,
                operation,
                statePath = deployment.TerraformStatePath,
                destroyed = true
            });
        }

        await WriteLogAsync(db, deployment.Id, "INFO", "terraform plan", null, cancellationToken);
        await Task.Delay(700, cancellationToken);

        await WriteLogAsync(db, deployment.Id, "INFO", "terraform apply -auto-approve", null, cancellationToken);
        await Task.Delay(1200, cancellationToken);

        return JsonSerializer.Serialize(new
        {
            deploymentId = deployment.Id,
            moduleId = deployment.ModuleId,
            simulated = true,
            operation,
            resourceId = $"/subscriptions/{credentials.SubscriptionId}/resourceGroups/{deployment.Id:N}",
            statePath = deployment.TerraformStatePath
        });
    }

    private static async Task UpsertDeploymentOutputAsync(
        WorkerDbContext db,
        Guid deploymentId,
        string outputPayload,
        CancellationToken cancellationToken)
    {
        var existingOutput = await db.DeploymentOutputs
            .SingleOrDefaultAsync(x => x.DeploymentId == deploymentId, cancellationToken);

        if (existingOutput is null)
        {
            db.DeploymentOutputs.Add(new DeploymentOutputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = deploymentId,
                Outputs = outputPayload,
                CreatedAt = DateTime.UtcNow
            });
        }
        else
        {
            existingOutput.Outputs = outputPayload;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsRealExecutionMode(string? mode)
        => string.Equals(mode, "real", StringComparison.OrdinalIgnoreCase);

    private static string ResolveOperation(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return TerraformExecutionService.OperationCreate;
        }

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("__operation", out var operationElement)
                && operationElement.ValueKind == JsonValueKind.String)
            {
                var operation = operationElement.GetString();
                if (string.Equals(operation, TerraformExecutionService.OperationDestroy, StringComparison.OrdinalIgnoreCase))
                {
                    return TerraformExecutionService.OperationDestroy;
                }
            }
        }
        catch (JsonException)
        {
            // Keep default create operation if input is malformed.
        }

        return TerraformExecutionService.OperationCreate;
    }

    private static async Task WriteLogAsync(
        WorkerDbContext db,
        Guid deploymentId,
        string level,
        string message,
        object? context,
        CancellationToken cancellationToken)
    {
        db.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = deploymentId,
            Timestamp = DateTime.UtcNow,
            Level = level,
            Message = message,
            Context = context is null ? null : JsonSerializer.Serialize(context)
        });

        await db.SaveChangesAsync(cancellationToken);
    }
}