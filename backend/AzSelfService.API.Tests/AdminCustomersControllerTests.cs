using System.Security.Claims;
using AzSelfService.API.Contracts;
using AzSelfService.API.Controllers;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using AzSelfService.API.Services;

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
                SpClientId = "11111111-1111-1111-1111-111111111111",
                SpClientSecret = "very-secret-value",
                Username = "dummy-admin",
                Password = "Test@1234",
                SpClientSecretSecretRef = " customers-dummy-sp-client-secret "
            },
            CancellationToken.None);

        var created = Assert.IsType<CreatedAtActionResult>(result.Result);
        var payload = Assert.IsType<OnboardCustomerResponse>(created.Value);

        Assert.Equal("dummy-admin", payload.Username);
        Assert.Equal("customer", payload.Role);

        var customer = await db.Customers.SingleAsync(x => x.Id == payload.CustomerId);
        Assert.Equal("Dummy Tenant", customer.Name);
        Assert.Equal("sub-123", customer.SubscriptionId);
        Assert.Equal("customers-dummy-sp-client-secret", customer.SpClientSecretSecretRef);

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
                SpClientId = "11111111-1111-1111-1111-111111111111",
                SpClientSecret = "very-secret-value",
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
                SpClientId = "11111111-1111-1111-1111-111111111111",
                SpClientSecret = "very-secret-value",
                Username = "dummy-admin",
                Password = "Test@1234"
            },
            CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    private static AdminCustomersController CreateController(AzSelfServiceDbContext db, string username, string role)
    {
        return new AdminCustomersController(db, new FakeCustomerCredentialProvisioningService())
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

    private sealed class FakeCustomerCredentialProvisioningService : ICustomerCredentialProvisioningService
    {
        public Task<CustomerCredentialProvisioningResult> ProvisionAsync(
            Guid customerId,
            string spClientId,
            string spClientSecret,
            string tenantId,
            string subscriptionId,
            string? spClientIdSecretRef,
            string? spClientSecretSecretRef,
            string? spTenantIdSecretRef,
            string? spSubscriptionIdSecretRef,
            CancellationToken cancellationToken)
        {
            var prefix = $"customer-{customerId:N}";
            return Task.FromResult(
                CustomerCredentialProvisioningResult.Success(
                    string.IsNullOrWhiteSpace(spClientIdSecretRef) ? $"{prefix}-sp-client-id" : spClientIdSecretRef.Trim(),
                    string.IsNullOrWhiteSpace(spClientSecretSecretRef) ? $"{prefix}-sp-client-secret" : spClientSecretSecretRef.Trim(),
                    string.IsNullOrWhiteSpace(spTenantIdSecretRef) ? $"{prefix}-sp-tenant-id" : spTenantIdSecretRef.Trim(),
                    string.IsNullOrWhiteSpace(spSubscriptionIdSecretRef) ? $"{prefix}-sp-subscription-id" : spSubscriptionIdSecretRef.Trim()));
        }
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