using System.Reflection;
using AzSelfService.Worker.Data;
using AzSelfService.Worker.Data.Entities;
using AzSelfService.Worker.Services;
using Azure.Security.KeyVault.Secrets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace AzSelfService.Worker.Tests;

public sealed class DeploymentProcessorTests
{
    [Fact]
    public async Task ProcessDeploymentAsync_Requeues_WhenFailureAndRetriesRemain()
    {
        var fixture = CreateFixture(maxRetries: 2);
        await SeedFailingDeploymentAsync(fixture.Db, fixture.CustomerId, fixture.DeploymentId);

        await InvokeProcessDeploymentAsync(fixture.Processor, fixture.DeploymentId);

        var deployment = await GetDeploymentAsync(fixture.ServiceProvider, fixture.DeploymentId);
        Assert.Equal("QUEUED", deployment.Status);
        Assert.Equal(1, deployment.RetryCount);
        Assert.NotNull(deployment.ErrorMessage);

        var warnLog = await GetLatestLogAsync(fixture.ServiceProvider, fixture.DeploymentId, "WARN");

        Assert.NotNull(warnLog);
        Assert.Contains("re-queued for retry", warnLog!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProcessDeploymentAsync_Fails_WhenMaxRetriesReached()
    {
        var fixture = CreateFixture(maxRetries: 1);
        await SeedFailingDeploymentAsync(fixture.Db, fixture.CustomerId, fixture.DeploymentId);

        await InvokeProcessDeploymentAsync(fixture.Processor, fixture.DeploymentId);

        var deployment = await GetDeploymentAsync(fixture.ServiceProvider, fixture.DeploymentId);
        Assert.Equal("FAILED", deployment.Status);
        Assert.Equal(1, deployment.RetryCount);
        Assert.NotNull(deployment.CompletedAt);
        Assert.Contains("client secret reference is not configured", deployment.ErrorMessage ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        var errorLog = await GetLatestLogAsync(fixture.ServiceProvider, fixture.DeploymentId, "ERROR");

        Assert.NotNull(errorLog);
        Assert.Contains("reached max retries", errorLog!.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<DeploymentEntity> GetDeploymentAsync(IServiceProvider provider, Guid deploymentId)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
        return await db.Deployments.AsNoTracking().SingleAsync(x => x.Id == deploymentId);
    }

    private static async Task<DeploymentLogEntity?> GetLatestLogAsync(IServiceProvider provider, Guid deploymentId, string level)
    {
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WorkerDbContext>();
        return await db.DeploymentLogs
            .AsNoTracking()
            .Where(x => x.DeploymentId == deploymentId && x.Level == level)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();
    }

    private static async Task InvokeProcessDeploymentAsync(DeploymentProcessor processor, Guid deploymentId)
    {
        var method = typeof(DeploymentProcessor).GetMethod("ProcessDeploymentAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = method!.Invoke(processor, new object[] { deploymentId, CancellationToken.None }) as Task;
        Assert.NotNull(task);
        await task!;
    }

    private static async Task SeedFailingDeploymentAsync(WorkerDbContext db, Guid customerId, Guid deploymentId)
    {
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Test Customer",
            IsActive = true,
            TenantId = "bf0465f4-f8c0-4ff4-978d-af5315afa795",
            SubscriptionId = "5b337264-50ba-4056-bc9f-1a926a433c18",
            SpClientSecretSecretRef = null
        });

        db.Modules.Add(new ModuleEntity
        {
            Id = moduleId,
            Name = "resource-group",
            Version = "1.0.0",
            IsPublished = true,
            IsDeprecated = false
        });

        db.Deployments.Add(new DeploymentEntity
        {
            Id = deploymentId,
            CustomerId = customerId,
            ModuleId = moduleId,
            RequestedBy = Guid.NewGuid(),
            Status = "QUEUED",
            RetryCount = 0,
            CreatedAt = now,
            UpdatedAt = now,
            TerraformStatePath = "tfstate/test.tfstate"
        });

        db.DeploymentInputs.Add(new DeploymentInputEntity
        {
            Id = Guid.NewGuid(),
            DeploymentId = deploymentId,
            Inputs = "{}",
            CreatedAt = now
        });

        await db.SaveChangesAsync();
    }

    private static (IServiceProvider ServiceProvider, WorkerDbContext Db, DeploymentProcessor Processor, Guid CustomerId, Guid DeploymentId) CreateFixture(int maxRetries)
    {
        var dbName = Guid.NewGuid().ToString("N");

        var services = new ServiceCollection();
        services.AddDbContext<WorkerDbContext>(options => options.UseInMemoryDatabase(dbName));

        var provider = services.BuildServiceProvider();

        var secretClient = new Mock<SecretClient>();
        var credentialProvider = new ServicePrincipalCredentialProvider(secretClient.Object);

        var processor = new DeploymentProcessor(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DeploymentProcessor>.Instance,
            credentialProvider,
            new TerraformExecutionService(
                Options.Create(new WorkerOptions
                {
                    TerraformExecutionMode = "simulate",
                    TerraformBinaryPath = "terraform",
                    RepositoryRootPath = "/app",
                    TerraformWorkingDirectory = "/tmp/terraform"
                }),
                NullLogger<TerraformExecutionService>.Instance),
            Options.Create(new WorkerOptions
            {
                PollIntervalMs = 10,
                MaxRetries = maxRetries,
                BatchSize = 5,
                SecretExpiryWarningDays = 30,
                TerraformExecutionMode = "simulate",
                TerraformBinaryPath = "terraform",
                RepositoryRootPath = "/app",
                TerraformWorkingDirectory = "/tmp/terraform"
            }));

        var db = provider.GetRequiredService<WorkerDbContext>();
        var customerId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        return (provider, db, processor, customerId, deploymentId);
    }
}
