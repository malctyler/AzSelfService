using System.Security.Claims;
using System.Text.Json;
using AzSelfService.API.Contracts;
using AzSelfService.API.Controllers;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AzSelfService.API.Tests;

public sealed class DeploymentsControllerTests
{
    [Fact]
    public async Task CreateDeployment_ReturnsBadRequest_WhenPreflightFails()
    {
        var customerId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await using var db = CreateDbContext();

        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Test Customer",
            IsActive = true,
            TenantId = "bf0465f4-f8c0-4ff4-978d-af5315afa795",
            SubscriptionId = "5b337264-50ba-4056-bc9f-1a926a433c18",
            SpClientSecretSecretRef = "customers-test-sp-client-secret"
        });

        await db.SaveChangesAsync();

        var preflightService = new CustomerCredentialPreflightService(
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CustomerCredentialPreflightService>.Instance);

        var controller = new DeploymentsController(db, preflightService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim("customer_id", customerId.ToString()),
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                                new Claim("sub", userId.ToString()),
                                new Claim("username", "admin")
                            },
                            authenticationType: "Test"))
                }
            }
        };

        var request = new CreateDeploymentRequest
        {
            ModuleId = Guid.NewGuid(),
            Inputs = JsonDocument.Parse("{}").RootElement.Clone()
        };

        var actionResult = await controller.CreateDeployment(request, CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(actionResult.Result);
        var payload = badRequest.Value?.ToString() ?? string.Empty;

        Assert.Contains("Deployment blocked by credential preflight checks", payload, StringComparison.Ordinal);
        Assert.Equal(0, await db.Deployments.CountAsync());
    }

    [Fact]
    public async Task GetDeploymentById_ReturnsNotFound_WhenDeploymentBelongsToAnotherCustomer()
    {
        var ownerCustomerId = Guid.NewGuid();
        var callerCustomerId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, ownerCustomerId, callerCustomerId, ownerUserId, callerUserId, moduleId, deploymentId, now);
        await db.SaveChangesAsync();

        var controller = CreateController(db, callerCustomerId, callerUserId);

        var result = await controller.GetDeploymentById(deploymentId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetDeploymentLogs_ReturnsNotFound_WhenDeploymentBelongsToAnotherCustomer()
    {
        var ownerCustomerId = Guid.NewGuid();
        var callerCustomerId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, ownerCustomerId, callerCustomerId, ownerUserId, callerUserId, moduleId, deploymentId, now);
        await db.SaveChangesAsync();

        var controller = CreateController(db, callerCustomerId, callerUserId);

        var result = await controller.GetDeploymentLogs(deploymentId, sinceId: null, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task DestroyDeployment_QueuesDestroyJob_WhenDeploymentSucceeded()
    {
        var customerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, customerId, Guid.NewGuid(), userId, Guid.NewGuid(), moduleId, deploymentId, now);
        await db.SaveChangesAsync();

        var deployment = await db.Deployments.SingleAsync(x => x.Id == deploymentId);
        deployment.Status = "SUCCEEDED";
        deployment.TerraformStatePath = "tfstate/customers/customer/module/state.tfstate";
        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.DestroyDeployment(deploymentId, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var payload = Assert.IsType<DeploymentCreatedResponse>(created.Value);

        Assert.NotEqual(deploymentId, payload.Id);
        Assert.Equal("QUEUED", payload.Status);

        var destroyDeployment = await db.Deployments.Include(x => x.Input)
            .SingleAsync(x => x.Id == payload.Id);

        Assert.Equal(customerId, destroyDeployment.CustomerId);
        Assert.Equal(moduleId, destroyDeployment.ModuleId);
        Assert.Equal("tfstate/customers/customer/module/state.tfstate", destroyDeployment.TerraformStatePath);

        using var inputDoc = JsonDocument.Parse(destroyDeployment.Input!.Inputs);
        Assert.True(inputDoc.RootElement.TryGetProperty("__operation", out var operation));
        Assert.Equal("destroy", operation.GetString());
    }

    [Fact]
    public async Task DestroyDeployment_ReturnsNotFound_WhenDeploymentBelongsToAnotherCustomer()
    {
        var ownerCustomerId = Guid.NewGuid();
        var callerCustomerId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var callerUserId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, ownerCustomerId, callerCustomerId, ownerUserId, callerUserId, moduleId, deploymentId, now);
        await db.SaveChangesAsync();

        var deployment = await db.Deployments.SingleAsync(x => x.Id == deploymentId);
        deployment.Status = "SUCCEEDED";
        deployment.TerraformStatePath = "tfstate/customers/owner/module/state.tfstate";
        await db.SaveChangesAsync();

        var controller = CreateController(db, callerCustomerId, callerUserId);
        var result = await controller.DestroyDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    private static DeploymentsController CreateController(AzSelfServiceDbContext db, Guid customerId, Guid userId)
    {
        var preflightService = new CustomerCredentialPreflightService(
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CustomerCredentialPreflightService>.Instance);

        return new DeploymentsController(db, preflightService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(
                        new ClaimsIdentity(
                            new[]
                            {
                                new Claim("customer_id", customerId.ToString()),
                                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                                new Claim("sub", userId.ToString()),
                                new Claim("username", "standard-user")
                            },
                            authenticationType: "Test"))
                }
            }
        };
    }

    private static void SeedCustomersUsersAndModule(
        AzSelfServiceDbContext db,
        Guid ownerCustomerId,
        Guid callerCustomerId,
        Guid ownerUserId,
        Guid callerUserId,
        Guid moduleId,
        Guid deploymentId,
        DateTime now)
    {
        db.Customers.AddRange(
            new CustomerEntity
            {
                Id = ownerCustomerId,
                Name = "Owner Customer",
                IsActive = true,
                TenantId = "tenant-owner",
                SubscriptionId = "sub-owner",
                SpClientSecretSecretRef = "customers/owner/sp-client-secret"
            },
            new CustomerEntity
            {
                Id = callerCustomerId,
                Name = "Caller Customer",
                IsActive = true,
                TenantId = "tenant-caller",
                SubscriptionId = "sub-caller",
                SpClientSecretSecretRef = "customers/caller/sp-client-secret"
            });

        db.Users.AddRange(
            new UserEntity
            {
                Id = ownerUserId,
                CustomerId = ownerCustomerId,
                Username = "owner-user",
                PasswordHash = "hash",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            },
            new UserEntity
            {
                Id = callerUserId,
                CustomerId = callerCustomerId,
                Username = "caller-user",
                PasswordHash = "hash",
                IsActive = true,
                CreatedAt = now,
                UpdatedAt = now
            });

        db.Modules.Add(new ModuleEntity
        {
            Id = moduleId,
            Name = "resource-group",
            Version = "1.0.0",
            TerraformPath = "terraform-modules/resource-group",
            Schema = "{}",
            IsPublished = true,
            IsDeprecated = false,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.Deployments.Add(new DeploymentEntity
        {
            Id = deploymentId,
            CustomerId = ownerCustomerId,
            ModuleId = moduleId,
            RequestedBy = ownerUserId,
            Status = "QUEUED",
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

        db.DeploymentLogs.Add(new DeploymentLogEntity
        {
            DeploymentId = deploymentId,
            Timestamp = now,
            Level = "INFO",
            Message = "seeded",
            Context = null
        });
    }

    private static AzSelfServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzSelfServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AzSelfServiceDbContext(options);
    }
}
