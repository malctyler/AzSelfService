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

    private static AzSelfServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzSelfServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AzSelfServiceDbContext(options);
    }
}
