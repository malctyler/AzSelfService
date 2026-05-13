using Azure;
using Azure.Security.KeyVault.Secrets;

namespace AzSelfService.API.Services;

public sealed class KeyVaultReadinessService(
    IConfiguration configuration,
    IServiceProvider serviceProvider,
    ILogger<KeyVaultReadinessService> logger)
{
    public async Task<ReadinessResult> CheckAsync(CancellationToken cancellationToken)
    {
        var checks = new List<ReadinessCheckResult>();
        var keyVaultUrl = configuration["Azure:KeyVault:Url"];

        if (string.IsNullOrWhiteSpace(keyVaultUrl))
        {
            checks.Add(new ReadinessCheckResult(
                "keyvault-config",
                false,
                "Azure:KeyVault:Url is not configured."));

            return new ReadinessResult(false, checks);
        }

        checks.Add(new ReadinessCheckResult("keyvault-config", true, $"Configured ({keyVaultUrl})."));

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
        {
            checks.Add(new ReadinessCheckResult(
                "keyvault-client",
                false,
                "SecretClient is not registered. Ensure Key Vault URL is configured at startup."));

            return new ReadinessResult(false, checks);
        }

        try
        {
            // Probe Key Vault with a minimal list call (first page only) to validate DNS, auth, and RBAC.
            await foreach (var _ in secretClient.GetPropertiesOfSecretsAsync(cancellationToken: cancellationToken).AsPages(pageSizeHint: 1))
            {
                break;
            }

            checks.Add(new ReadinessCheckResult("keyvault-access", true, "Key Vault connectivity and authentication are healthy."));
            return new ReadinessResult(true, checks);
        }
        catch (RequestFailedException ex)
        {
            checks.Add(new ReadinessCheckResult("keyvault-access", false, $"Request failed ({ex.Status}): {ex.Message}"));
            logger.LogWarning(ex, "Key Vault readiness probe failed.");
            return new ReadinessResult(false, checks);
        }
        catch (Exception ex)
        {
            checks.Add(new ReadinessCheckResult("keyvault-access", false, ex.Message));
            logger.LogWarning(ex, "Key Vault readiness probe failed with unexpected error.");
            return new ReadinessResult(false, checks);
        }
    }
}

public sealed record ReadinessResult(bool IsReady, IReadOnlyList<ReadinessCheckResult> Checks);

public sealed record ReadinessCheckResult(string Name, bool Healthy, string Message);
