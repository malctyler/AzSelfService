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

            await SimulateTerraformRunAsync(db, deployment, credentialResolution.Credentials, cancellationToken);

            deployment.Status = "SUCCEEDED";
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

    private static async Task SimulateTerraformRunAsync(
        WorkerDbContext db,
        DeploymentEntity deployment,
        ServicePrincipalCredentials credentials,
        CancellationToken cancellationToken)
    {
        await WriteLogAsync(db, deployment.Id, "INFO", "terraform init", null, cancellationToken);
        await Task.Delay(700, cancellationToken);

        await WriteLogAsync(db, deployment.Id, "INFO", "terraform plan", null, cancellationToken);
        await Task.Delay(700, cancellationToken);

        await WriteLogAsync(db, deployment.Id, "INFO", "terraform apply -auto-approve", null, cancellationToken);
        await Task.Delay(1200, cancellationToken);

        var outputPayload = JsonSerializer.Serialize(new
        {
            deploymentId = deployment.Id,
            moduleId = deployment.ModuleId,
            simulated = true,
            resourceId = $"/subscriptions/{credentials.SubscriptionId}/resourceGroups/{deployment.Id:N}",
            statePath = deployment.TerraformStatePath
        });

        var existingOutput = await db.DeploymentOutputs
            .SingleOrDefaultAsync(x => x.DeploymentId == deployment.Id, cancellationToken);

        if (existingOutput is null)
        {
            db.DeploymentOutputs.Add(new DeploymentOutputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment.Id,
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