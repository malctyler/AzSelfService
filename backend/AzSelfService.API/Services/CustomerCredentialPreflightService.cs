using Azure;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;

namespace AzSelfService.API.Services;

public sealed class CustomerCredentialPreflightService(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ILogger<CustomerCredentialPreflightService> logger)
{
    public async Task<CustomerCredentialPreflightResult> CheckAsync(CustomerEntity customer, CancellationToken cancellationToken)
    {
        var issues = new List<string>();
        var warnings = new List<string>();
        var warningThresholdDays = configuration.GetValue<int?>("PRECHECK_SECRET_EXPIRY_WARNING_DAYS") ?? 30;

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
        {
            issues.Add("Customer service principal client secret reference is not configured.");
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            issues.Add("Customer tenant_id and subscription_id must be present in customer metadata.");
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
        {
            issues.Add("Key Vault client is not configured. Set Azure:KeyVault:Url on the API service.");
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }

        try
        {
            var clientSecretSecret = await GetSecretAsync(secretClient, ResolveSecretReference(customer.SpClientSecretSecretRef), "client secret", issues, cancellationToken);

            if (issues.Count > 0)
            {
                return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
            }

            var appId = ResolveAppIdFromSecretMetadata(clientSecretSecret!);
            if (string.IsNullOrWhiteSpace(appId))
            {
                issues.Add("Could not resolve appid from Key Vault secret metadata. Set content type or tag to 'appid=<value>'.");
                return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
            }

            if (!Guid.TryParse(appId, out _))
            {
                issues.Add("appid resolved from secret metadata is not a valid GUID.");
                return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
            }

            var expiry = clientSecretSecret!.Properties.ExpiresOn;
            var expired = expiry.HasValue && expiry.Value <= DateTimeOffset.UtcNow;
            var nearExpiry = expiry.HasValue
                && expiry.Value > DateTimeOffset.UtcNow
                && expiry.Value <= DateTimeOffset.UtcNow.AddDays(warningThresholdDays);

            if (expired)
            {
                issues.Add("Service principal client secret has expired.");
            }
            else if (nearExpiry)
            {
                warnings.Add($"Service principal client secret expires soon on {expiry:O}.");
                logger.LogWarning(
                    "Service principal client secret near expiry for customer {CustomerId}. ExpiresOn={ExpiresOn}",
                    customer.Id,
                    expiry);
            }

            return new CustomerCredentialPreflightResult
            {
                CanProceed = issues.Count == 0,
                Issues = issues,
                Warnings = warnings,
                SecretExpiry = new SecretExpirySummary
                {
                    ClientSecretExpiresOn = expiry,
                    ClientSecretExpired = expired,
                    ClientSecretNearExpiry = nearExpiry,
                    WarningThresholdDays = warningThresholdDays
                }
            };
        }
        catch (RequestFailedException ex) when (ex.Status == 401 || ex.Status == 403)
        {
            issues.Add("API identity is not authorized to read Key Vault secrets.");
            logger.LogWarning(ex, "Key Vault authorization failed during preflight for customer {CustomerId}", customer.Id);
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }
        catch (RequestFailedException ex)
        {
            issues.Add($"Key Vault check failed: {ex.Message}");
            logger.LogWarning(ex, "Key Vault preflight failed for customer {CustomerId}", customer.Id);
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }
        catch (Exception ex)
        {
            issues.Add($"Key Vault check failed: {ex.Message}");
            logger.LogWarning(ex, "Unexpected Key Vault preflight failure for customer {CustomerId}", customer.Id);
            return CustomerCredentialPreflightResult.Fail(issues, warnings, warningThresholdDays);
        }
    }

    private static async Task<KeyVaultSecret?> GetSecretAsync(
        SecretClient client,
        string secretName,
        string label,
        List<string> issues,
        CancellationToken cancellationToken)
    {
        try
        {
            var secret = await client.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(secret.Value.Value))
            {
                issues.Add($"Key Vault {label} secret '{secretName}' is empty.");
            }

            return secret.Value;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            issues.Add($"Key Vault {label} secret '{secretName}' was not found.");
            return null;
        }
    }

    private static string ResolveSecretReference(string reference)
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

public sealed class CustomerCredentialPreflightResult
{
    public bool CanProceed { get; init; }
    public IReadOnlyList<string> Issues { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public SecretExpirySummary? SecretExpiry { get; init; }

    public static CustomerCredentialPreflightResult Fail(IReadOnlyList<string> issues, IReadOnlyList<string> warnings, int warningThresholdDays)
        => new()
        {
            CanProceed = false,
            Issues = issues,
            Warnings = warnings,
            SecretExpiry = new SecretExpirySummary
            {
                WarningThresholdDays = warningThresholdDays
            }
        };
}

public sealed class SecretExpirySummary
{
    public DateTimeOffset? ClientSecretExpiresOn { get; init; }
    public bool ClientSecretExpired { get; init; }
    public bool ClientSecretNearExpiry { get; init; }
    public int WarningThresholdDays { get; init; }
}