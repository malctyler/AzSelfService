using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/admin/customers")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminCustomersController(AzSelfServiceDbContext dbContext) : ControllerBase
{
    [HttpPost("onboard")]
    [ProducesResponseType(typeof(OnboardCustomerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<OnboardCustomerResponse>> OnboardCustomer(
        [FromBody] OnboardCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.CustomerName)
            || string.IsNullOrWhiteSpace(request.SubscriptionId)
            || string.IsNullOrWhiteSpace(request.TenantId)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "customerName, subscriptionId, tenantId, username, and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return BadRequest(new { message = "Password must be at least 8 characters." });
        }

        var normalizedSubscription = request.SubscriptionId.Trim();
        var normalizedTenant = request.TenantId.Trim();
        var normalizedUsername = request.Username.Trim();

        var existingCustomer = await dbContext.Customers
            .AnyAsync(x => x.SubscriptionId == normalizedSubscription, cancellationToken);

        if (existingCustomer)
        {
            return BadRequest(new { message = "A customer for this subscription already exists." });
        }

        // Login currently resolves users globally by username, so keep usernames globally unique.
        var existingUser = await dbContext.Users
            .AnyAsync(x => x.Username == normalizedUsername, cancellationToken);

        if (existingUser)
        {
            return BadRequest(new { message = "Username already exists." });
        }

        var now = DateTime.UtcNow;
        var customerId = Guid.NewGuid();

        var customer = new CustomerEntity
        {
            Id = customerId,
            Name = request.CustomerName.Trim(),
            SubscriptionId = normalizedSubscription,
            TenantId = normalizedTenant,
            SpClientIdSecretRef = NormalizeOrDefault(request.SpClientIdSecretRef, $"customers/{customerId}/sp-client-id"),
            SpClientSecretSecretRef = NormalizeOrDefault(request.SpClientSecretSecretRef, $"customers/{customerId}/sp-client-secret"),
            SpTenantIdSecretRef = NormalizeOrDefault(request.SpTenantIdSecretRef, $"customers/{customerId}/sp-tenant-id"),
            SpSubscriptionIdSecretRef = NormalizeOrDefault(request.SpSubscriptionIdSecretRef, $"customers/{customerId}/sp-subscription-id"),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        var user = new UserEntity
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            Username = normalizedUsername,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(request.Password),
            Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now
        };

        dbContext.Customers.Add(customer);
        dbContext.Users.Add(user);
        await dbContext.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(
            nameof(OnboardCustomer),
            new { customerId = customer.Id },
            new OnboardCustomerResponse
            {
                CustomerId = customer.Id,
                UserId = user.Id,
                Username = user.Username,
                Role = AppRoles.Customer,
                CreatedAtUtc = customer.CreatedAt,
                SpClientSecretSecretRefMasked = MaskReference(customer.SpClientSecretSecretRef) ?? "********"
            });
    }

    private static string NormalizeOrDefault(string? value, string fallback)
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string? MaskReference(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length <= 8)
        {
            return "********";
        }

        return $"{trimmed[..4]}****{trimmed[^4..]}";
    }
}