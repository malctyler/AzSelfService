using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public sealed class CustomerCredentialsController(
    AzSelfServiceDbContext dbContext,
    CustomerCredentialPreflightService preflightService) : ControllerBase
{
    [HttpPut("admin/customers/{customerId:guid}/credential-references")]
    [ProducesResponseType(typeof(CustomerCredentialReferencesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerCredentialReferencesResponse>> UpsertCredentialReferences(
        Guid customerId,
        [FromBody] UpsertCustomerCredentialReferencesRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser())
        {
            return Forbid();
        }

        if (string.IsNullOrWhiteSpace(request.SpClientSecretSecretRef))
        {
            return BadRequest(new { message = "spClientSecretSecretRef is required." });
        }

        var customer = await dbContext.Customers.SingleOrDefaultAsync(x => x.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return NotFound(new { message = "Customer not found." });
        }

        customer.SpClientSecretSecretRef = request.SpClientSecretSecretRef.Trim();

        if (!string.IsNullOrWhiteSpace(request.SpClientIdSecretRef))
        {
            customer.SpClientIdSecretRef = request.SpClientIdSecretRef.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.SpTenantIdSecretRef))
        {
            customer.SpTenantIdSecretRef = request.SpTenantIdSecretRef.Trim();
        }

        if (!string.IsNullOrWhiteSpace(request.SpSubscriptionIdSecretRef))
        {
            customer.SpSubscriptionIdSecretRef = request.SpSubscriptionIdSecretRef.Trim();
        }
        customer.UpdatedAt = DateTime.UtcNow;

        await dbContext.SaveChangesAsync(cancellationToken);

        return Ok(new CustomerCredentialReferencesResponse
        {
            CustomerId = customer.Id,
            SpClientIdSecretRefMasked = MaskReference(customer.SpClientIdSecretRef),
            SpClientSecretSecretRefMasked = MaskReference(customer.SpClientSecretSecretRef),
            SpTenantIdSecretRefMasked = MaskReference(customer.SpTenantIdSecretRef),
            SpSubscriptionIdSecretRefMasked = MaskReference(customer.SpSubscriptionIdSecretRef),
            UpdatedAtUtc = customer.UpdatedAt
        });
    }

    [HttpGet("customers/{customerId:guid}/credential-preflight")]
    [ProducesResponseType(typeof(CustomerCredentialPreflightResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<ActionResult<CustomerCredentialPreflightResponse>> GetCredentialPreflight(
        Guid customerId,
        CancellationToken cancellationToken)
    {
        if (!User.IsAdminUser() && User.GetRequiredCustomerId() != customerId)
        {
            return Forbid();
        }

        var customer = await dbContext.Customers.SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);
        if (customer is null)
        {
            return NotFound(new { message = "Customer not found." });
        }

        var result = await preflightService.CheckAsync(customer, cancellationToken);

        var status = result.CanProceed
            ? (result.Warnings.Count > 0 ? "WARN" : "PASS")
            : "FAIL";

        return Ok(new CustomerCredentialPreflightResponse
        {
            CustomerId = customer.Id,
            CanProceed = result.CanProceed,
            Status = status,
            Issues = result.Issues,
            Warnings = result.Warnings,
            SecretExpiry = result.SecretExpiry is null
                ? null
                : new SecretExpirySummaryResponse
                {
                    ClientSecretExpiresOn = result.SecretExpiry.ClientSecretExpiresOn,
                    ClientSecretExpired = result.SecretExpiry.ClientSecretExpired,
                    ClientSecretNearExpiry = result.SecretExpiry.ClientSecretNearExpiry,
                    WarningThresholdDays = result.SecretExpiry.WarningThresholdDays
                },
            CheckedAtUtc = DateTime.UtcNow
        });
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