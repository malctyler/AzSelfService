using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/admin/customers")]
[Authorize(Policy = "AdminOnly")]
public sealed class AdminCustomersController(
    AzSelfServiceDbContext dbContext,
    ICustomerCredentialProvisioningService credentialProvisioningService) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<AdminCustomerSummaryResponse>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<AdminCustomerSummaryResponse>>> GetCustomers(CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var customers = await dbContext.Customers
            .OrderBy(x => x.Name)
            .Select(x => new AdminCustomerSummaryResponse
            {
                CustomerId = x.Id,
                CustomerName = x.Name,
                SubscriptionId = x.SubscriptionId,
                TenantId = x.TenantId,
                IsActive = x.IsActive,
                Username = x.Users.OrderBy(u => u.CreatedAt).Select(u => u.Username).FirstOrDefault(),
                Email = x.Users.OrderBy(u => u.CreatedAt).Select(u => u.Email).FirstOrDefault(),
                SpClientIdSecretRef = x.SpClientIdSecretRef,
                SpClientSecretSecretRefMasked = MaskReference(x.SpClientSecretSecretRef),
                SpTenantIdSecretRef = x.SpTenantIdSecretRef,
                SpSubscriptionIdSecretRef = x.SpSubscriptionIdSecretRef,
                UpdatedAtUtc = x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        return Ok(customers);
    }

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
            || string.IsNullOrWhiteSpace(request.SpClientId)
            || string.IsNullOrWhiteSpace(request.SpClientSecret)
            || string.IsNullOrWhiteSpace(request.Username)
            || string.IsNullOrWhiteSpace(request.Password))
        {
            return BadRequest(new { message = "customerName, subscriptionId, tenantId, spClientId, spClientSecret, username, and password are required." });
        }

        if (request.Password.Length < 8)
        {
            return BadRequest(new { message = "Password must be at least 8 characters." });
        }

        var normalizedSubscription = request.SubscriptionId.Trim();
        var normalizedTenant = request.TenantId.Trim();
        var normalizedSpClientId = request.SpClientId.Trim();
        var normalizedSpClientSecret = request.SpClientSecret.Trim();
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

        var provisioning = await credentialProvisioningService.ProvisionAsync(
            customerId,
            normalizedSpClientId,
            normalizedSpClientSecret,
            normalizedTenant,
            normalizedSubscription,
            request.SpClientIdSecretRef,
            request.SpClientSecretSecretRef,
            request.SpTenantIdSecretRef,
            request.SpSubscriptionIdSecretRef,
            cancellationToken);

        if (!provisioning.IsSuccess)
        {
            return BadRequest(new { message = provisioning.ErrorMessage ?? "Failed to provision customer secrets in Key Vault." });
        }

        var customer = new CustomerEntity
        {
            Id = customerId,
            Name = request.CustomerName.Trim(),
            SubscriptionId = normalizedSubscription,
            TenantId = normalizedTenant,
            SpClientIdSecretRef = provisioning.SpClientIdSecretRef,
            SpClientSecretSecretRef = provisioning.SpClientSecretSecretRef,
            SpTenantIdSecretRef = provisioning.SpTenantIdSecretRef,
            SpSubscriptionIdSecretRef = provisioning.SpSubscriptionIdSecretRef,
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

    [HttpPut("{customerId:guid}")]
    [ProducesResponseType(typeof(AdminCustomerSummaryResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<AdminCustomerSummaryResponse>> UpdateCustomer(
        Guid customerId,
        [FromBody] UpdateCustomerRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.CustomerName)
            || string.IsNullOrWhiteSpace(request.SubscriptionId)
            || string.IsNullOrWhiteSpace(request.TenantId))
        {
            return BadRequest(new { message = "customerName, subscriptionId, and tenantId are required." });
        }

        var customer = await dbContext.Customers
            .Include(x => x.Users)
            .SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken);

        if (customer is null)
        {
            return NotFound(new { message = "Customer not found." });
        }

        var normalizedSubscription = request.SubscriptionId.Trim();
        var normalizedTenant = request.TenantId.Trim();

        var duplicateSubscription = await dbContext.Customers
            .AnyAsync(x => x.Id != customerId && x.SubscriptionId == normalizedSubscription, cancellationToken);

        if (duplicateSubscription)
        {
            return BadRequest(new { message = "A customer for this subscription already exists." });
        }

        var shouldReprovisionSecrets =
            !string.IsNullOrWhiteSpace(request.SpClientId)
            || !string.IsNullOrWhiteSpace(request.SpClientSecret);

        if (shouldReprovisionSecrets)
        {
            if (string.IsNullOrWhiteSpace(request.SpClientId) || string.IsNullOrWhiteSpace(request.SpClientSecret))
            {
                return BadRequest(new { message = "Both spClientId and spClientSecret are required when rotating customer credentials." });
            }

            var provisioning = await credentialProvisioningService.ProvisionAsync(
                customer.Id,
                request.SpClientId.Trim(),
                request.SpClientSecret.Trim(),
                normalizedTenant,
                normalizedSubscription,
                request.SpClientIdSecretRef ?? customer.SpClientIdSecretRef,
                request.SpClientSecretSecretRef ?? customer.SpClientSecretSecretRef,
                request.SpTenantIdSecretRef ?? customer.SpTenantIdSecretRef,
                request.SpSubscriptionIdSecretRef ?? customer.SpSubscriptionIdSecretRef,
                cancellationToken);

            if (!provisioning.IsSuccess)
            {
                return BadRequest(new { message = provisioning.ErrorMessage ?? "Failed to provision customer secrets in Key Vault." });
            }

            customer.SpClientIdSecretRef = provisioning.SpClientIdSecretRef;
            customer.SpClientSecretSecretRef = provisioning.SpClientSecretSecretRef;
            customer.SpTenantIdSecretRef = provisioning.SpTenantIdSecretRef;
            customer.SpSubscriptionIdSecretRef = provisioning.SpSubscriptionIdSecretRef;
        }
        else
        {
            customer.SpClientIdSecretRef = string.IsNullOrWhiteSpace(request.SpClientIdSecretRef) ? customer.SpClientIdSecretRef : request.SpClientIdSecretRef.Trim();
            customer.SpClientSecretSecretRef = string.IsNullOrWhiteSpace(request.SpClientSecretSecretRef) ? customer.SpClientSecretSecretRef : request.SpClientSecretSecretRef.Trim();
            customer.SpTenantIdSecretRef = string.IsNullOrWhiteSpace(request.SpTenantIdSecretRef) ? customer.SpTenantIdSecretRef : request.SpTenantIdSecretRef.Trim();
            customer.SpSubscriptionIdSecretRef = string.IsNullOrWhiteSpace(request.SpSubscriptionIdSecretRef) ? customer.SpSubscriptionIdSecretRef : request.SpSubscriptionIdSecretRef.Trim();
        }

        customer.Name = request.CustomerName.Trim();
        customer.SubscriptionId = normalizedSubscription;
        customer.TenantId = normalizedTenant;
        customer.IsActive = request.IsActive;
        customer.UpdatedAt = DateTime.UtcNow;

        var firstUser = customer.Users.OrderBy(u => u.CreatedAt).FirstOrDefault();
        if (firstUser is not null)
        {
            firstUser.Email = string.IsNullOrWhiteSpace(request.Email) ? null : request.Email.Trim();
            firstUser.IsActive = request.IsActive;
            firstUser.UpdatedAt = DateTime.UtcNow;
        }

        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new AdminCustomerSummaryResponse
        {
            CustomerId = customer.Id,
            CustomerName = customer.Name,
            SubscriptionId = customer.SubscriptionId,
            TenantId = customer.TenantId,
            IsActive = customer.IsActive,
            Username = firstUser?.Username,
            Email = firstUser?.Email,
            SpClientIdSecretRef = customer.SpClientIdSecretRef,
            SpClientSecretSecretRefMasked = MaskReference(customer.SpClientSecretSecretRef),
            SpTenantIdSecretRef = customer.SpTenantIdSecretRef,
            SpSubscriptionIdSecretRef = customer.SpSubscriptionIdSecretRef,
            UpdatedAtUtc = customer.UpdatedAt
        });
    }

    [HttpDelete("{customerId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteCustomer(Guid customerId, CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        var customer = await dbContext.Customers
            .SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken);

        if (customer is null)
        {
            return NotFound(new { message = "Customer not found." });
        }

        dbContext.Customers.Remove(customer);
        await dbContext.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

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