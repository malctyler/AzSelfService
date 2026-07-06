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
        var moduleId = Guid.NewGuid();

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
        db.Modules.Add(new ModuleEntity
        {
            Id = moduleId,
            Name = "resource-group",
            Version = "1.0.0",
            TerraformPath = "terraform-modules/resource-group",
            Schema = "{}",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            IsPublished = true,
            IsDeprecated = false
        });

        await db.SaveChangesAsync();

        var preflightService = new CustomerCredentialPreflightService(
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CustomerCredentialPreflightService>.Instance);

        var controller = new DeploymentsController(db, preflightService, new FakeSoftwarePackageBlobStorageService())
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
            ModuleId = moduleId,
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

    [Fact]
    public async Task DeleteFailedDeployment_RemovesDeployment_WhenFailed()
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
        deployment.Status = "FAILED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.DeleteFailedDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<NoContentResult>(result);
        Assert.False(await db.Deployments.AnyAsync(x => x.Id == deploymentId));
    }

    [Fact]
    public async Task DeleteFailedDeployment_ReturnsBadRequest_WhenStatusIsNotFailed()
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
        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.DeleteFailedDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result);
        Assert.True(await db.Deployments.AnyAsync(x => x.Id == deploymentId));
    }

    [Fact]
    public async Task DeleteFailedDeployment_ReturnsNotFound_WhenDeploymentBelongsToAnotherCustomer()
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
        deployment.Status = "FAILED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, callerCustomerId, callerUserId);
        var result = await controller.DeleteFailedDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result);
    }

    [Fact]
    public async Task RebuildDeployment_QueuesDestroyThenRedeploy_WhenDeploymentSucceeded()
    {
        var customerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var deploymentId = Guid.NewGuid();
        var moduleId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, customerId, Guid.NewGuid(), userId, Guid.NewGuid(), moduleId, deploymentId, now);
        await db.SaveChangesAsync();

        var deployment = await db.Deployments.Include(x => x.Input).SingleAsync(x => x.Id == deploymentId);
        var module = await db.Modules.SingleAsync(x => x.Id == moduleId);
        module.Name = "windows-server-marketplace";
        module.TerraformPath = "terraform-modules/windows-server-marketplace";

        db.SoftwarePackages.Add(new SoftwarePackageEntity
        {
            Id = Guid.NewGuid(),
            Scope = "platform",
            PackageId = "winscp.winscp",
            Version = "6.5.1",
            DisplayName = "WinSCP",
            Publisher = "WinSCP",
            Os = "windows",
            Architecture = "x64",
            InstallerType = "zip",
            BlobPath = "catalog/platform/winscp.winscp/6.5.1/winscp.zip",
            ZipSha256 = "abc123",
            IsPublished = true,
            CreatedAt = now,
            UpdatedAt = now
        });
        deployment.Status = "SUCCEEDED";
        deployment.TerraformStatePath = "tfstate/customers/customer/module/state.tfstate";
        deployment.Input!.Inputs = "{\"name\":\"demo-rg\",\"software_package_ids\":[\"winscp.winscp\"]}";
        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.RebuildDeployment(deploymentId, CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var payload = Assert.IsType<RebuildDeploymentResponse>(created.Value);

        var queued = await db.Deployments
            .Where(x => x.Id == payload.DestroyDeploymentId || x.Id == payload.RedeployDeploymentId)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        Assert.Equal(2, queued.Count);
        Assert.Equal(payload.DestroyDeploymentId, queued[0].Id);
        Assert.Equal(payload.RedeployDeploymentId, queued[1].Id);
        Assert.All(queued, x => Assert.Equal("QUEUED", x.Status));

        var destroyInput = await db.DeploymentInputs.SingleAsync(x => x.DeploymentId == payload.DestroyDeploymentId);
        using var destroyDoc = JsonDocument.Parse(destroyInput.Inputs);
        Assert.Equal("destroy", destroyDoc.RootElement.GetProperty("__operation").GetString());

        var redeployInput = await db.DeploymentInputs.SingleAsync(x => x.DeploymentId == payload.RedeployDeploymentId);
        using var redeployDoc = JsonDocument.Parse(redeployInput.Inputs);
        Assert.Equal("demo-rg", redeployDoc.RootElement.GetProperty("name").GetString());
        Assert.Equal("winscp.winscp", redeployDoc.RootElement.GetProperty("software_package_ids")[0].GetString());
        Assert.Equal("winscp.winscp", redeployDoc.RootElement.GetProperty("software_package_catalog_packages")[0].GetProperty("package_id").GetString());
        Assert.Equal("azselfservicesoftware01", redeployDoc.RootElement.GetProperty("software_storage_account_name").GetString());
        Assert.Equal("packages", redeployDoc.RootElement.GetProperty("software_storage_container_name").GetString());
        Assert.True(redeployDoc.RootElement.GetProperty("software_package_catalog_packages")[0].TryGetProperty("download_url", out var downloadUrl));
        Assert.False(string.IsNullOrWhiteSpace(downloadUrl.GetString()));
        Assert.True(redeployDoc.RootElement.TryGetProperty("post_install_script_uri", out var scriptUri));
        Assert.False(string.IsNullOrWhiteSpace(scriptUri.GetString()));
        Assert.False(redeployDoc.RootElement.TryGetProperty("__operation", out _));
    }

    [Fact]
    public async Task RebuildDeployment_ReturnsBadRequest_WhenDeploymentNotSucceeded()
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
        deployment.Status = "FAILED";
        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.RebuildDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task RebuildDeployment_ReturnsNotFound_WhenDeploymentBelongsToAnotherCustomer()
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
        var result = await controller.RebuildDeployment(deploymentId, CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    [Fact]
    public async Task RebuildAllDeployments_QueuesDestroyReverseAndRedeployForward()
    {
        var customerId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTime.UtcNow;

        await using var db = CreateDbContext();
        SeedCustomersUsersAndModule(db, customerId, Guid.NewGuid(), userId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), now.AddMinutes(-30));

        var deployment1 = db.Deployments.Local.Single();
        deployment1.Status = "SUCCEEDED";
        deployment1.CompletedAt = now.AddMinutes(-26);
        deployment1.UpdatedAt = now.AddMinutes(-26);

        var module2Id = Guid.NewGuid();
        var module3Id = Guid.NewGuid();
        db.Modules.AddRange(
            new ModuleEntity
            {
                Id = module2Id,
                Name = "storage-account",
                Version = "1.0.0",
                TerraformPath = "terraform-modules/storage-account",
                Schema = "{}",
                IsPublished = true,
                IsDeprecated = false,
                CreatedAt = now.AddMinutes(-29),
                UpdatedAt = now.AddMinutes(-29)
            },
            new ModuleEntity
            {
                Id = module3Id,
                Name = "keyvault",
                Version = "1.0.0",
                TerraformPath = "terraform-modules/keyvault",
                Schema = "{}",
                IsPublished = true,
                IsDeprecated = false,
                CreatedAt = now.AddMinutes(-28),
                UpdatedAt = now.AddMinutes(-28)
            });

        var deployment1Id = deployment1.Id;
        var deployment2Id = Guid.NewGuid();
        var deployment3Id = Guid.NewGuid();

        var baseModuleId = db.Modules.Local.Single(x => x.Name == "resource-group").Id;
        deployment1.CustomerId = customerId;
        deployment1.ModuleId = baseModuleId;
        deployment1.RequestedBy = userId;
        deployment1.Status = "SUCCEEDED";
        deployment1.CreatedAt = now.AddMinutes(-27);
        deployment1.UpdatedAt = now.AddMinutes(-27);
        deployment1.CompletedAt = now.AddMinutes(-26);
        deployment1.TerraformStatePath = "tfstate/customers/customer/one.tfstate";

        db.DeploymentInputs.Local.Single().Inputs = "{\"name\":\"one\"}";

        db.Deployments.AddRange(
            new DeploymentEntity
            {
                Id = deployment2Id,
                CustomerId = customerId,
                ModuleId = module2Id,
                RequestedBy = userId,
                Status = "SUCCEEDED",
                CreatedAt = now.AddMinutes(-20),
                UpdatedAt = now.AddMinutes(-20),
                CompletedAt = now.AddMinutes(-19),
                TerraformStatePath = "tfstate/customers/customer/two.tfstate"
            },
            new DeploymentEntity
            {
                Id = deployment3Id,
                CustomerId = customerId,
                ModuleId = module3Id,
                RequestedBy = userId,
                Status = "SUCCEEDED",
                CreatedAt = now.AddMinutes(-10),
                UpdatedAt = now.AddMinutes(-10),
                CompletedAt = now.AddMinutes(-9),
                TerraformStatePath = "tfstate/customers/customer/three.tfstate"
            });

        db.DeploymentInputs.AddRange(
            new DeploymentInputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment1Id,
                Inputs = "{\"name\":\"one\"}",
                CreatedAt = now.AddMinutes(-27)
            },
            new DeploymentInputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment2Id,
                Inputs = "{\"name\":\"two\"}",
                CreatedAt = now.AddMinutes(-20)
            },
            new DeploymentInputEntity
            {
                Id = Guid.NewGuid(),
                DeploymentId = deployment3Id,
                Inputs = "{\"name\":\"three\"}",
                CreatedAt = now.AddMinutes(-10)
            });

        await db.SaveChangesAsync();

        var controller = CreateController(db, customerId, userId);

        var result = await controller.RebuildAllDeployments(CancellationToken.None);

        var accepted = Assert.IsType<AcceptedResult>(result.Result);
        var payload = Assert.IsType<RebuildAllResponse>(accepted.Value);

        Assert.Equal(3, payload.DeploymentCount);
        Assert.Equal(3, payload.DestroyCount);
        Assert.Equal(3, payload.RedeployCount);

        var queuedDeployments = await db.Deployments
            .Include(x => x.Input)
            .Where(x => x.CustomerId == customerId && x.Status == "QUEUED")
            .OrderBy(x => x.CreatedAt)
            .ToListAsync();

        Assert.Equal(6, queuedDeployments.Count);

        var destroyDeployments = queuedDeployments.Take(3).ToList();
        var redeployDeployments = queuedDeployments.Skip(3).ToList();

        var originalSources = new[]
        {
            deployment1Id,
            deployment2Id,
            deployment3Id
        };

        // Destroy queue should be reverse build order.
        for (var i = 0; i < destroyDeployments.Count; i += 1)
        {
            using var destroyDoc = JsonDocument.Parse(destroyDeployments[i].Input!.Inputs);
            Assert.Equal(originalSources[2 - i], destroyDoc.RootElement.GetProperty("__targetDeploymentId").GetGuid());
            Assert.Equal("destroy", destroyDoc.RootElement.GetProperty("__operation").GetString());
        }

        // Redeploy queue should be original build order.
        for (var i = 0; i < redeployDeployments.Count; i += 1)
        {
            using var redeployDoc = JsonDocument.Parse(redeployDeployments[i].Input!.Inputs);
            Assert.Equal(i switch
            {
                0 => "one",
                1 => "two",
                2 => "three",
                _ => string.Empty
            }, redeployDoc.RootElement.GetProperty("name").GetString());
            Assert.False(redeployDoc.RootElement.TryGetProperty("__operation", out _));
        }
    }

    private static DeploymentsController CreateController(AzSelfServiceDbContext db, Guid customerId, Guid userId)
    {
        var preflightService = new CustomerCredentialPreflightService(
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CustomerCredentialPreflightService>.Instance);

        return new DeploymentsController(db, preflightService, new FakeSoftwarePackageBlobStorageService())
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

    private sealed class FakeSoftwarePackageBlobStorageService : ISoftwarePackageBlobStorageService
    {
        public Task UploadAsync(string storageAccountName, string containerName, string blobPath, Stream content, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task DeleteIfExistsAsync(string storageAccountName, string containerName, string blobPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<Uri> CreateReadUriAsync(string storageAccountName, string containerName, string blobPath, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            return Task.FromResult(new Uri($"https://example.invalid/{containerName}/{blobPath}?sig=test"));
        }
    }
}
