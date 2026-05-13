using System.Security.Claims;
using AzSelfService.API.Controllers;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Tests;

public sealed class ModulesControllerTests
{
    [Fact]
    public async Task GetModuleById_ReturnsOk_WhenPublishedModuleExists()
    {
        var moduleId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Modules.Add(new ModuleEntity
        {
            Id = moduleId,
            Name = "resource-group",
            Version = "1.0.0",
            TerraformPath = "terraform-modules/resource-group",
            Schema = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\"}}}",
            UiSchema = "{\"layout\":\"vertical\"}",
            Description = "RG",
            IsPublished = true,
            IsDeprecated = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);

        var result = await controller.GetModuleById(moduleId, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<AzSelfService.API.Contracts.ModuleSummaryResponse>(ok.Value);
        Assert.Equal(moduleId, payload.Id);
        Assert.Equal("resource-group", payload.Name);
    }

    [Fact]
    public async Task GetModuleById_ReturnsNotFound_WhenModuleMissingOrUnpublished()
    {
        await using var db = CreateDbContext();
        db.Modules.Add(new ModuleEntity
        {
            Id = Guid.NewGuid(),
            Name = "hidden-module",
            Version = "1.0.0",
            TerraformPath = "terraform-modules/hidden-module",
            Schema = "{}",
            IsPublished = false,
            IsDeprecated = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db);
        var result = await controller.GetModuleById(Guid.NewGuid(), CancellationToken.None);

        Assert.IsType<NotFoundObjectResult>(result.Result);
    }

    private static ModulesController CreateController(AzSelfServiceDbContext db)
    {
        return new ModulesController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal()
                }
            }
        };
    }

    private static ClaimsPrincipal BuildPrincipal()
    {
        var userId = Guid.NewGuid();
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("username", "admin"),
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
}