using System.Security.Claims;
using AzSelfService.API.Contracts;
using AzSelfService.API.Controllers;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Tests;

public sealed class AdminCustomersControllerTests
{
    [Fact]
    public async Task OnboardCustomer_CreatesCustomerAndUser_WhenAdmin()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, username: "admin", role: "admin");

        var result = await controller.OnboardCustomer(
            new OnboardCustomerRequest
            {
                CustomerName = "Dummy Tenant",
                SubscriptionId = "sub-123",
                TenantId = "tenant-123",
                Username = "dummy-admin",
                Password = "Test@1234",
                SpClientSecretSecretRef = " customers/dummy/sp-client-secret "
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var payload = Assert.IsType<OnboardCustomerResponse>(created.Value);

        Assert.Equal("dummy-admin", payload.Username);
        Assert.Equal("customer", payload.Role);

        var customer = await db.Customers.SingleAsync(x => x.Id == payload.CustomerId);
        Assert.Equal("Dummy Tenant", customer.Name);
        Assert.Equal("sub-123", customer.SubscriptionId);
        Assert.Equal("customers/dummy/sp-client-secret", customer.SpClientSecretSecretRef);

        var user = await db.Users.SingleAsync(x => x.Id == payload.UserId);
        Assert.Equal(customer.Id, user.CustomerId);
        Assert.True(BCrypt.Net.BCrypt.Verify("Test@1234", user.PasswordHash));
    }

    [Fact]
    public async Task OnboardCustomer_ReturnsBadRequest_WhenSubscriptionExists()
    {
        await using var db = CreateDbContext();
        db.Customers.Add(new CustomerEntity
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            SubscriptionId = "sub-123",
            TenantId = "tenant-123",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var controller = CreateController(db, username: "admin", role: "admin");
        var result = await controller.OnboardCustomer(
            new OnboardCustomerRequest
            {
                CustomerName = "Dummy Tenant",
                SubscriptionId = "sub-123",
                TenantId = "tenant-999",
                Username = "dummy-admin-2",
                Password = "Test@1234"
            },
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task OnboardCustomer_ReturnsForbid_WhenNotAdmin()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, username: "standard-user", role: "customer");

        var result = await controller.OnboardCustomer(
            new OnboardCustomerRequest
            {
                CustomerName = "Dummy Tenant",
                SubscriptionId = "sub-123",
                TenantId = "tenant-123",
                Username = "dummy-admin",
                Password = "Test@1234"
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private static AdminCustomersController CreateController(AzSelfServiceDbContext db, string username, string role)
    {
        return new AdminCustomersController(db)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username, role)
                }
            }
        };
    }

    private static ClaimsPrincipal BuildPrincipal(string username, string role)
    {
        var userId = Guid.NewGuid();
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("username", username),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("role", role),
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