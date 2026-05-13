using System.Security.Claims;
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

public sealed class CustomerCredentialsControllerTests
{
    [Fact]
    public async Task UpsertCredentialReferences_ReturnsForbid_WhenUserIsNotAdmin()
    {
        var customerId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Tenant A",
            IsActive = true,
            TenantId = "tenant-a",
            SubscriptionId = "sub-a"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, username: "standard-user", customerId: customerId);

        var result = await controller.UpsertCredentialReferences(
            customerId,
            new UpsertCustomerCredentialReferencesRequest
            {
                SpClientSecretSecretRef = "customers/abc/sp-client-secret"
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task UpsertCredentialReferences_ReturnsBadRequest_WhenSecretReferenceMissing()
    {
        var customerId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Tenant A",
            IsActive = true,
            TenantId = "tenant-a",
            SubscriptionId = "sub-a"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, username: "admin", customerId: customerId);

        var result = await controller.UpsertCredentialReferences(
            customerId,
            new UpsertCustomerCredentialReferencesRequest
            {
                SpClientSecretSecretRef = "  "
            },
            CancellationToken.None);

        var badRequest = Assert.IsType<BadRequestObjectResult>(result.Result);
        var payload = badRequest.Value?.ToString() ?? string.Empty;
        Assert.Contains("spClientSecretSecretRef is required", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpsertCredentialReferences_ReturnsOk_AndPersistsReferences_WhenAdmin()
    {
        var customerId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Customers.Add(new CustomerEntity
        {
            Id = customerId,
            Name = "Tenant A",
            IsActive = true,
            TenantId = "tenant-a",
            SubscriptionId = "sub-a"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, username: "admin", customerId: customerId);

        var result = await controller.UpsertCredentialReferences(
            customerId,
            new UpsertCustomerCredentialReferencesRequest
            {
                SpClientSecretSecretRef = " customers/new/sp-client-secret ",
                SpClientIdSecretRef = " customers/new/sp-client-id ",
                SpTenantIdSecretRef = "customers/new/sp-tenant-id",
                SpSubscriptionIdSecretRef = "customers/new/sp-subscription-id"
            },
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.IsType<CustomerCredentialReferencesResponse>(ok.Value);

        var updated = await db.Customers.SingleAsync(x => x.Id == customerId);
        Assert.Equal("customers/new/sp-client-secret", updated.SpClientSecretSecretRef);
        Assert.Equal("customers/new/sp-client-id", updated.SpClientIdSecretRef);
        Assert.Equal("customers/new/sp-tenant-id", updated.SpTenantIdSecretRef);
        Assert.Equal("customers/new/sp-subscription-id", updated.SpSubscriptionIdSecretRef);
    }

    [Fact]
    public async Task GetCredentialPreflight_ReturnsForbid_WhenNotAdminAndCrossCustomer()
    {
        var callerCustomerId = Guid.NewGuid();
        var targetCustomerId = Guid.NewGuid();

        await using var db = CreateDbContext();
        db.Customers.Add(new CustomerEntity
        {
            Id = targetCustomerId,
            Name = "Target",
            IsActive = true,
            TenantId = "tenant-target",
            SubscriptionId = "sub-target",
            SpClientSecretSecretRef = "customers/target/sp-client-secret"
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, username: "standard-user", customerId: callerCustomerId);

        var result = await controller.GetCredentialPreflight(targetCustomerId, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private static CustomerCredentialsController CreateController(AzSelfServiceDbContext db, string username, Guid customerId)
    {
        var preflightService = new CustomerCredentialPreflightService(
            new ConfigurationBuilder().Build(),
            new ServiceCollection().BuildServiceProvider(),
            NullLogger<CustomerCredentialPreflightService>.Instance);

        var controller = new CustomerCredentialsController(db, preflightService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username, customerId)
                }
            }
        };

        return controller;
    }

    private static ClaimsPrincipal BuildPrincipal(string username, Guid customerId)
    {
        var userId = Guid.NewGuid();
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("username", username),
                    new Claim("customer_id", customerId.ToString()),
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
