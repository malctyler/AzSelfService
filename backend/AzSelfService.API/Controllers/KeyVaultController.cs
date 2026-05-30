using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public sealed class KeyVaultController(
    AzSelfServiceDbContext dbContext,
    KeyVaultNameAvailabilityService keyVaultNameAvailabilityService) : ControllerBase
{

        [HttpPost("validate")]
        public async Task<ActionResult<KeyVaultValidationResponse>> Validate([FromBody] KeyVaultCreateRequest request, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Ok(new KeyVaultValidationResponse { IsValid = false, ErrorMessage = "Key Vault name is required." });
            }

            // Key Vault naming constraints: 3-24 chars, lowercase letters/numbers/hyphens, start with letter, end with letter/number.
            var keyVaultName = request.Name.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(keyVaultName, "^[a-z][a-z0-9-]{1,22}[a-z0-9]$"))
            {
                return Ok(new KeyVaultValidationResponse
                {
                    IsValid = false,
                    ErrorMessage = "Key Vault name must be 3-24 chars, lowercase letters/numbers/hyphens, start with a letter, and end with a letter or number."
                });
            }

            if (keyVaultName.Contains("--", StringComparison.Ordinal))
            {
                return Ok(new KeyVaultValidationResponse
                {
                    IsValid = false,
                    ErrorMessage = "Key Vault name cannot contain consecutive hyphens."
                });
            }

            var customerId = User.GetRequiredCustomerId();
            var customer = await dbContext.Customers
                .SingleOrDefaultAsync(x => x.Id == customerId && x.IsActive, cancellationToken);

            if (customer is null)
            {
                return Ok(new KeyVaultValidationResponse { IsValid = false, ErrorMessage = "Customer is not active or does not exist." });
            }

            var availability = await keyVaultNameAvailabilityService.CheckAsync(customer, keyVaultName, cancellationToken);
            if (!availability.IsAvailable)
            {
                return Ok(new KeyVaultValidationResponse
                {
                    IsValid = false,
                    ErrorMessage = availability.Message ?? "Key Vault name is not available globally."
                });
            }

            return Ok(new KeyVaultValidationResponse { IsValid = true });
        }

        [HttpPost("deploy")]
        public ActionResult<KeyVaultDeployResponse> Deploy([FromBody] KeyVaultDeployRequest request)
        {
            // Basic deployment stub
            if (string.IsNullOrWhiteSpace(request.Name))
            {
                return Ok(new KeyVaultDeployResponse { Success = false, ErrorMessage = "Key Vault name is required." });
            }
            // TODO: Integrate with Terraform/ARM for actual deployment
            return Ok(new KeyVaultDeployResponse { Success = true });
        }
}
