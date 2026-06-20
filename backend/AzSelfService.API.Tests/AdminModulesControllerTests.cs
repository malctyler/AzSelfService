using System.Security.Claims;
using AzSelfService.API.Contracts;
using AzSelfService.API.Controllers;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace AzSelfService.API.Tests;

public sealed class AdminModulesControllerTests
{
    [Fact]
    public async Task GetAllModules_ReturnsForbid_WhenCallerIsNotAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "standard-user");

            var result = await controller.GetAllModules(CancellationToken.None);

            Assert.IsType<ForbidResult>(result.Result);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task GetAllModules_ReturnsPublishedAndDeprecated_WhenCallerIsAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Modules.AddRange(
                new ModuleEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "resource-group",
                    Version = "1.0.0",
                    TerraformPath = "terraform-modules/resource-group",
                    Schema = "{}",
                    IsPublished = true,
                    IsDeprecated = false,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                },
                new ModuleEntity
                {
                    Id = Guid.NewGuid(),
                    Name = "storage-account",
                    Version = "1.0.0",
                    TerraformPath = "terraform-modules/storage-account",
                    Schema = "{}",
                    IsPublished = false,
                    IsDeprecated = true,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();

            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "admin");

            var result = await controller.GetAllModules(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsAssignableFrom<IReadOnlyList<ModuleSummaryResponse>>(ok.Value);
            Assert.Equal(2, payload.Count);
            Assert.Contains(payload, x => x.Name == "resource-group");
            Assert.Contains(payload, x => x.Name == "storage-account");
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterModule_ReturnsForbid_WhenCallerIsNotAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "standard-user");

            var result = await controller.RegisterModule(
                new RegisterModuleRequest { ModulePath = "terraform-modules/resource-group" },
                CancellationToken.None);

            Assert.IsType<ForbidResult>(result.Result);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task RegisterModule_UpsertsManifestData_WhenCallerIsAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            var moduleDirectory = Path.Combine(rootPath, "terraform-modules", "resource-group");
            Directory.CreateDirectory(moduleDirectory);
            var manifestPath = Path.Combine(moduleDirectory, "module.yaml");

            await File.WriteAllTextAsync(
                manifestPath,
                """
                name: "resource-group"
                version: "1.0.0"
                description: "RG module"
                terraform_path: "terraform-modules/resource-group"
                variables:
                  - name: "name"
                    type: "string"
                    required: true
                  - name: "location"
                    type: "string"
                    required: true
                    enum: ["eastus", "westeurope"]
                ui_schema:
                  layout: "vertical"
                """);

            await using var db = CreateDbContext();
            db.AllowedRegions.AddRange(
                new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow },
                new AllowedRegionEntity { Code = "uksouth", SortOrder = 1, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "admin");

            var result = await controller.RegisterModule(
                new RegisterModuleRequest { ModulePath = "terraform-modules/resource-group" },
                CancellationToken.None);

            var okResult = Assert.IsType<OkObjectResult>(result.Result);
            var payload = Assert.IsType<ModuleSummaryResponse>(okResult.Value);

            Assert.Equal("resource-group", payload.Name);
            Assert.Equal("1.0.0", payload.Version);
            Assert.Equal("terraform-modules/resource-group", payload.TerraformPath);

            var module = await db.Modules.SingleAsync(x => x.Name == "resource-group" && x.Version == "1.0.0");
            Assert.True(module.IsPublished);
            Assert.False(module.IsDeprecated);

            using var schemaDoc = System.Text.Json.JsonDocument.Parse(module.Schema);
            var root = schemaDoc.RootElement;
            Assert.True(root.TryGetProperty("properties", out var properties));
            Assert.True(properties.TryGetProperty("name", out _));
            Assert.True(properties.TryGetProperty("location", out _));
            Assert.Equal(["eastus", "uksouth"], properties.GetProperty("location").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).ToArray());
            Assert.True(root.TryGetProperty("required", out var required));
            Assert.Contains("name", required.EnumerateArray().Select(x => x.GetString()));
            Assert.Contains("location", required.EnumerateArray().Select(x => x.GetString()));

            await File.WriteAllTextAsync(
                manifestPath,
                """
                name: "resource-group"
                version: "1.0.0"
                description: "RG module updated"
                terraform_path: "terraform-modules/resource-group"
                variables:
                  - name: "name"
                    type: "string"
                    required: true
                """);

            var secondResult = await controller.RegisterModule(
                new RegisterModuleRequest { ModulePath = "terraform-modules/resource-group" },
                CancellationToken.None);

            Assert.IsType<OkObjectResult>(secondResult.Result);
            Assert.Equal(1, await db.Modules.CountAsync(x => x.Name == "resource-group" && x.Version == "1.0.0"));

            var updated = await db.Modules.SingleAsync(x => x.Name == "resource-group" && x.Version == "1.0.0");
            Assert.Equal("RG module updated", updated.Description);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PublishModule_ReturnsForbid_WhenCallerIsNotAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "standard-user");

            var result = await controller.PublishModule(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<ForbidResult>(result.Result);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task PublishAndDeprecateModule_UpdateLifecycleFlags_WhenCallerIsAdmin()
    {
        var rootPath = CreateTempRoot();

        try
        {
            var moduleId = Guid.NewGuid();

            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            db.Modules.Add(new ModuleEntity
            {
                Id = moduleId,
                Name = "resource-group",
                Version = "1.0.0",
                TerraformPath = "terraform-modules/resource-group",
                Schema = "{}",
                IsPublished = false,
                IsDeprecated = false,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();

            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "admin");

            var publishResult = await controller.PublishModule(moduleId, CancellationToken.None);
            var publishOk = Assert.IsType<OkObjectResult>(publishResult.Result);
            Assert.IsType<ModuleSummaryResponse>(publishOk.Value);

            var published = await db.Modules.SingleAsync(x => x.Id == moduleId);
            Assert.True(published.IsPublished);
            Assert.False(published.IsDeprecated);

            var deprecateResult = await controller.DeprecateModule(moduleId, CancellationToken.None);
            var deprecateOk = Assert.IsType<OkObjectResult>(deprecateResult.Result);
            Assert.IsType<ModuleSummaryResponse>(deprecateOk.Value);

            var deprecated = await db.Modules.SingleAsync(x => x.Id == moduleId);
            Assert.False(deprecated.IsPublished);
            Assert.True(deprecated.IsDeprecated);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    [Fact]
    public async Task DeprecateModule_ReturnsNotFound_WhenModuleMissing()
    {
        var rootPath = CreateTempRoot();

        try
        {
            await using var db = CreateDbContext();
            db.AllowedRegions.Add(new AllowedRegionEntity { Code = "eastus", SortOrder = 0, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
            var loader = new ModuleManifestLoader(new TestHostEnvironment(rootPath));
            var controller = CreateController(db, loader, username: "admin");

            var result = await controller.DeprecateModule(Guid.NewGuid(), CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result.Result);
        }
        finally
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    private static AdminModulesController CreateController(
        AzSelfServiceDbContext db,
        ModuleManifestLoader loader,
        string username)
    {
        return new AdminModulesController(db, loader, new AllowedRegionCatalogService(db))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username)
                }
            }
        };
    }

    private static ClaimsPrincipal BuildPrincipal(string username)
    {
        var userId = Guid.NewGuid();
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("username", username),
                    new Claim("customer_id", Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString())
                },
                authenticationType: "Test"));
    }

    private static AzSelfServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzSelfServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AzSelfServiceDbContext(options);
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "azselfservice-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "AzSelfService.Tests";
        public string ContentRootPath { get; set; } = contentRootPath;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(contentRootPath);
    }
}