using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;

namespace AzSelfService.API.Services;

public sealed class KeyVaultNameAvailabilityService(
    IServiceProvider serviceProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<KeyVaultNameAvailabilityService> logger)
{
    private const string CheckNameAvailabilityApiVersion = "2023-07-01";

    public async Task<KeyVaultNameAvailabilityResult> CheckAsync(
        CustomerEntity customer,
        string vaultName,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(vaultName))
        {
            return KeyVaultNameAvailabilityResult.Failed("Key Vault name is required.");
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return KeyVaultNameAvailabilityResult.Failed("Customer tenant_id and subscription_id must be configured.");
        }

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
        {
            return KeyVaultNameAvailabilityResult.Failed("Customer service principal client secret reference is not configured.");
        }

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
        {
            return KeyVaultNameAvailabilityResult.Failed("Key Vault client is not configured on the API service.");
        }

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;

            if (string.IsNullOrWhiteSpace(secret.Value))
            {
                return KeyVaultNameAvailabilityResult.Failed("Customer service principal secret is empty.");
            }

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
            {
                return KeyVaultNameAvailabilityResult.Failed("Could not resolve appid from Key Vault secret metadata.");
            }

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);

            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://management.azure.com/subscriptions/{customer.SubscriptionId}/providers/Microsoft.KeyVault/checkNameAvailability?api-version={CheckNameAvailabilityApiVersion}");

            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            var payload = JsonSerializer.Serialize(new
            {
                name = vaultName,
                type = "Microsoft.KeyVault/vaults"
            });

            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Key Vault name availability precheck request failed. Status={StatusCode}, Body={Body}",
                    (int)response.StatusCode,
                    content);
                return KeyVaultNameAvailabilityResult.Failed("Could not validate Key Vault name availability at this time.");
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var isAvailable = root.TryGetProperty("nameAvailable", out var nameAvailable)
                              && nameAvailable.ValueKind == JsonValueKind.True;

            if (isAvailable)
            {
                return KeyVaultNameAvailabilityResult.Available();
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            return KeyVaultNameAvailabilityResult.NotAvailable(
                string.IsNullOrWhiteSpace(message)
                    ? $"Key Vault name '{vaultName}' is not available globally."
                    : message);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return KeyVaultNameAvailabilityResult.Failed("Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Key Vault name availability precheck failed.");
            return KeyVaultNameAvailabilityResult.Failed("Could not validate Key Vault name availability at this time.");
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

public sealed class KeyVaultNameAvailabilityResult
{
    public bool IsAvailable { get; private init; }
    public bool CheckFailed { get; private init; }
    public string? Message { get; private init; }

    public static KeyVaultNameAvailabilityResult Available()
        => new() { IsAvailable = true };

    public static KeyVaultNameAvailabilityResult NotAvailable(string? message)
        => new() { IsAvailable = false, CheckFailed = false, Message = message };

    public static KeyVaultNameAvailabilityResult Failed(string message)
        => new() { IsAvailable = false, CheckFailed = true, Message = message };
}
