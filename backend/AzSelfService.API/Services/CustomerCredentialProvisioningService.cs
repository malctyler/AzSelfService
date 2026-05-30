using Azure;
using Azure.Security.KeyVault.Secrets;
using System.Text.RegularExpressions;

namespace AzSelfService.API.Services;

public interface ICustomerCredentialProvisioningService
{
    Task<CustomerCredentialProvisioningResult> ProvisionAsync(
        Guid customerId,
        string spClientId,
        string spClientSecret,
        string tenantId,
        string subscriptionId,
        string? spClientIdSecretRef,
        string? spClientSecretSecretRef,
        string? spTenantIdSecretRef,
        string? spSubscriptionIdSecretRef,
        CancellationToken cancellationToken);
}

public sealed class CustomerCredentialProvisioningService(
    IConfiguration configuration,
    SecretClient? secretClient,
    ILogger<CustomerCredentialProvisioningService> logger) : ICustomerCredentialProvisioningService
{
    public async Task<CustomerCredentialProvisioningResult> ProvisionAsync(
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
        if (secretClient is null)
        {
            return CustomerCredentialProvisioningResult.Failed(
                "Key Vault client is not configured. Set Azure:KeyVault:Url on the API service.");
        }

        try
        {
            var generatedPrefix = $"customer-{customerId:N}";

            var clientIdRef = NormalizeOrDefaultRef(spClientIdSecretRef, $"{generatedPrefix}-sp-client-id");
            var clientSecretRef = NormalizeOrDefaultRef(spClientSecretSecretRef, $"{generatedPrefix}-sp-client-secret");
            var tenantRef = NormalizeOrDefaultRef(spTenantIdSecretRef, $"{generatedPrefix}-sp-tenant-id");
            var subscriptionRef = NormalizeOrDefaultRef(spSubscriptionIdSecretRef, $"{generatedPrefix}-sp-subscription-id");

            var clientIdSecretName = ResolveSecretName(clientIdRef);
            var clientSecretName = ResolveSecretName(clientSecretRef);
            var tenantSecretName = ResolveSecretName(tenantRef);
            var subscriptionSecretName = ResolveSecretName(subscriptionRef);

            await secretClient.SetSecretAsync(new KeyVaultSecret(clientIdSecretName, spClientId), cancellationToken);

            var clientSecret = new KeyVaultSecret(clientSecretName, spClientSecret)
            {
                Properties =
                {
                    ContentType = $"appid={spClientId}"
                }
            };
            await secretClient.SetSecretAsync(clientSecret, cancellationToken);

            await secretClient.SetSecretAsync(new KeyVaultSecret(tenantSecretName, tenantId), cancellationToken);
            await secretClient.SetSecretAsync(new KeyVaultSecret(subscriptionSecretName, subscriptionId), cancellationToken);

            logger.LogInformation(
                "Provisioned customer credential secrets in Key Vault for customer {CustomerId}.",
                customerId);

            return CustomerCredentialProvisioningResult.Success(
                clientIdRef,
                clientSecretRef,
                tenantRef,
                subscriptionRef);
        }
        catch (RequestFailedException ex)
        {
            logger.LogWarning(ex, "Key Vault secret provisioning failed for customer {CustomerId}.", customerId);
            return CustomerCredentialProvisioningResult.Failed($"Failed to store customer credentials in Key Vault: {ex.Message}");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unexpected secret provisioning failure for customer {CustomerId}.", customerId);
            return CustomerCredentialProvisioningResult.Failed($"Failed to store customer credentials in Key Vault: {ex.Message}");
        }
    }

    private static string NormalizeOrDefaultRef(string? value, string fallback)
    {
        var reference = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
            {
                return reference;
            }

            throw new InvalidOperationException($"Invalid Key Vault secret URI reference '{reference}'.");
        }

        return SanitizeSecretName(reference);
    }

    private static string ResolveSecretName(string reference)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
            {
                return segments[1];
            }

            throw new InvalidOperationException($"Invalid Key Vault secret URI reference '{reference}'.");
        }

        return SanitizeSecretName(reference);
    }

    private static string SanitizeSecretName(string reference)
    {
        var sanitized = reference.Trim().Replace('/', '-').Replace('\\', '-');
        sanitized = Regex.Replace(sanitized, "[^A-Za-z0-9-]", "-");
        sanitized = Regex.Replace(sanitized, "-{2,}", "-").Trim('-');

        if (string.IsNullOrWhiteSpace(sanitized))
        {
            throw new InvalidOperationException(
                $"Invalid secret reference '{reference}'. Use a Key Vault secret URI or a plain secret name (letters, numbers, and hyphens).");
        }

        return sanitized;
    }
}

public sealed class CustomerCredentialProvisioningResult
{
    public bool IsSuccess { get; private init; }
    public string? ErrorMessage { get; private init; }
    public string SpClientIdSecretRef { get; private init; } = string.Empty;
    public string SpClientSecretSecretRef { get; private init; } = string.Empty;
    public string SpTenantIdSecretRef { get; private init; } = string.Empty;
    public string SpSubscriptionIdSecretRef { get; private init; } = string.Empty;

    public static CustomerCredentialProvisioningResult Success(
        string spClientIdSecretRef,
        string spClientSecretSecretRef,
        string spTenantIdSecretRef,
        string spSubscriptionIdSecretRef)
        => new()
        {
            IsSuccess = true,
            SpClientIdSecretRef = spClientIdSecretRef,
            SpClientSecretSecretRef = spClientSecretSecretRef,
            SpTenantIdSecretRef = spTenantIdSecretRef,
            SpSubscriptionIdSecretRef = spSubscriptionIdSecretRef
        };

    public static CustomerCredentialProvisioningResult Failed(string errorMessage)
        => new()
        {
            IsSuccess = false,
            ErrorMessage = errorMessage
        };
}
