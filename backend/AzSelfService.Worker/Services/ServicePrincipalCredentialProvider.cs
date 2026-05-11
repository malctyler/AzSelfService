using Azure;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.Worker.Data.Entities;

namespace AzSelfService.Worker.Services;

public sealed class ServicePrincipalCredentialProvider(SecretClient secretClient)
{
    public async Task<ServicePrincipalCredentialResolution> ResolveAsync(
        CustomerEntity customer,
        int expiryWarningDays,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
        {
            throw new InvalidOperationException("Customer client secret reference is not configured.");
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            throw new InvalidOperationException("Customer tenant_id and subscription_id must be configured in metadata.");
        }

        var warningThreshold = DateTimeOffset.UtcNow.AddDays(expiryWarningDays);

        var clientSecretSecret = await GetRequiredSecretAsync(
            ResolveSecretReference(customer.SpClientSecretSecretRef, $"customers/{customer.Id}/sp-client-secret"),
            cancellationToken);

        var clientId = ResolveAppIdFromSecretMetadata(clientSecretSecret);
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("Could not resolve appid from Key Vault secret metadata. Set content type or tag to 'appid=<value>'.");
        }

        if (!Guid.TryParse(clientId, out _))
        {
            throw new InvalidOperationException("appid resolved from secret metadata is not a valid GUID.");
        }

        var expiry = clientSecretSecret.Properties.ExpiresOn;
        var isExpired = expiry.HasValue && expiry.Value <= DateTimeOffset.UtcNow;
        var isNearExpiry = expiry.HasValue && expiry.Value > DateTimeOffset.UtcNow && expiry.Value <= warningThreshold;

        return new ServicePrincipalCredentialResolution
        {
            Credentials = new ServicePrincipalCredentials(
                clientId,
                clientSecretSecret.Value,
                customer.TenantId,
                customer.SubscriptionId),
            SecretMetadata = new ServicePrincipalSecretMetadata
            {
                ClientIdRef = "secret-metadata:appid",
                ClientSecretRef = clientSecretSecret.Name,
                TenantIdRef = "customer.tenant_id",
                SubscriptionIdRef = "customer.subscription_id",
                ClientSecretExpiresOn = expiry,
                ClientSecretExpired = isExpired,
                ClientSecretNearExpiry = isNearExpiry
            }
        };
    }

    private async Task<KeyVaultSecret> GetRequiredSecretAsync(string secretName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Value.Value))
            {
                throw new InvalidOperationException($"Key Vault secret '{secretName}' is empty.");
            }

            return response.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            throw new InvalidOperationException($"Required Key Vault secret '{secretName}' does not exist.", ex);
        }
        catch (RequestFailedException ex) when (ex.Status == 401 || ex.Status == 403)
        {
            throw new InvalidOperationException("Worker identity does not have access to required Key Vault secrets.", ex);
        }
    }

    private static string ResolveSecretReference(string? reference, string fallbackName)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return fallbackName;
        }

        if (Uri.TryCreate(reference, UriKind.Absolute, out var uri))
        {
            // Accept full secret URI format: https://<vault>/secrets/<name>/<version?>
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length >= 2 && string.Equals(segments[0], "secrets", StringComparison.OrdinalIgnoreCase))
            {
                return segments[1];
            }

            throw new InvalidOperationException($"Invalid Key Vault secret URI reference '{reference}'.");
        }

        return reference.Trim();
    }

    private static string? ResolveAppIdFromSecretMetadata(KeyVaultSecret secret)
    {
        if (secret.Properties.Tags is not null
            && secret.Properties.Tags.TryGetValue("appid", out var appIdTag)
            && !string.IsNullOrWhiteSpace(appIdTag))
        {
            return appIdTag.Trim();
        }

        var contentType = secret.Properties.ContentType;
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        const string prefix = "appid=";
        var index = contentType.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        var value = contentType[(index + prefix.Length)..].Trim();
        if (value.Contains(';'))
        {
            value = value.Split(';', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }

        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}

public sealed record ServicePrincipalCredentials(
    string ClientId,
    string ClientSecret,
    string TenantId,
    string SubscriptionId);

public sealed class ServicePrincipalCredentialResolution
{
    public required ServicePrincipalCredentials Credentials { get; init; }
    public required ServicePrincipalSecretMetadata SecretMetadata { get; init; }
}

public sealed class ServicePrincipalSecretMetadata
{
    public string ClientIdRef { get; set; } = string.Empty;
    public string ClientSecretRef { get; set; } = string.Empty;
    public string TenantIdRef { get; set; } = string.Empty;
    public string SubscriptionIdRef { get; set; } = string.Empty;
    public DateTimeOffset? ClientSecretExpiresOn { get; set; }
    public bool ClientSecretExpired { get; set; }
    public bool ClientSecretNearExpiry { get; set; }
}