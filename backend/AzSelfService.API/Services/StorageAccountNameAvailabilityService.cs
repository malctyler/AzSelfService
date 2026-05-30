using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;

namespace AzSelfService.API.Services;

public sealed class StorageAccountNameAvailabilityService(
    IServiceProvider serviceProvider,
    IHttpClientFactory httpClientFactory,
    ILogger<StorageAccountNameAvailabilityService> logger)
{
    private const string CheckNameAvailabilityApiVersion = "2023-01-01";

    public async Task<StorageAccountNameAvailabilityResult> CheckAsync(
        CustomerEntity customer,
        string accountName,
        CancellationToken cancellationToken)
    {
        var totalStart = DateTime.UtcNow;
        var stepStart = totalStart;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            return StorageAccountNameAvailabilityResult.Failed("Storage account name is required.");
        }

        if (string.IsNullOrWhiteSpace(customer.TenantId) || string.IsNullOrWhiteSpace(customer.SubscriptionId))
        {
            return StorageAccountNameAvailabilityResult.Failed("Customer tenant_id and subscription_id must be configured.");
        }

        if (string.IsNullOrWhiteSpace(customer.SpClientSecretSecretRef))
        {
            return StorageAccountNameAvailabilityResult.Failed("Customer service principal client secret reference is not configured.");
        }

        var secretClient = serviceProvider.GetService<SecretClient>();
        if (secretClient is null)
        {
            return StorageAccountNameAvailabilityResult.Failed("Key Vault client is not configured on the API service.");
        }

        try
        {
            var secretName = ResolveSecretReference(customer.SpClientSecretSecretRef);
            var secretResponse = await secretClient.GetSecretAsync(secretName, cancellationToken: cancellationToken);
            var secret = secretResponse.Value;
            logger.LogInformation("[TIMING] Key Vault secret fetch for '{StorageAccountName}': {ElapsedMs}ms", accountName, (DateTime.UtcNow - stepStart).TotalMilliseconds);
            stepStart = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(secret.Value))
            {
                return StorageAccountNameAvailabilityResult.Failed("Customer service principal secret is empty.");
            }

            var appId = ResolveAppIdFromSecretMetadata(secret);
            if (string.IsNullOrWhiteSpace(appId))
            {
                return StorageAccountNameAvailabilityResult.Failed("Could not resolve appid from Key Vault secret metadata.");
            }

            var credential = new ClientSecretCredential(customer.TenantId, appId, secret.Value);
            var token = await credential.GetTokenAsync(
                new TokenRequestContext(new[] { "https://management.azure.com/.default" }),
                cancellationToken);
            logger.LogInformation("[TIMING] Azure token acquisition for '{StorageAccountName}': {ElapsedMs}ms", accountName, (DateTime.UtcNow - stepStart).TotalMilliseconds);
            stepStart = DateTime.UtcNow;

            var client = httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"https://management.azure.com/subscriptions/{customer.SubscriptionId}/providers/Microsoft.Storage/checkNameAvailability?api-version={CheckNameAvailabilityApiVersion}");
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token.Token);

            var payload = JsonSerializer.Serialize(new
            {
                name = accountName,
                type = "Microsoft.Storage/storageAccounts"
            });

            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await client.SendAsync(request, cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);
            logger.LogInformation("[TIMING] Azure checkNameAvailability API call for '{StorageAccountName}' ({StatusCode}): {ElapsedMs}ms", accountName, (int)response.StatusCode, (DateTime.UtcNow - stepStart).TotalMilliseconds);
            stepStart = DateTime.UtcNow;

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "Storage name availability precheck request failed. Status={StatusCode}, Body={Body}",
                    (int)response.StatusCode,
                    content);
                return StorageAccountNameAvailabilityResult.Failed("Could not validate storage account name availability at this time.");
            }

            using var document = JsonDocument.Parse(content);
            var root = document.RootElement;

            var isAvailable = root.TryGetProperty("nameAvailable", out var nameAvailable)
                              && nameAvailable.ValueKind == JsonValueKind.True;
            logger.LogInformation("[TIMING] Total storage account name availability check for '{StorageAccountName}': {TotalMs}ms (Available={IsAvailable})", accountName, (DateTime.UtcNow - totalStart).TotalMilliseconds, isAvailable);

            if (isAvailable)
            {
                return StorageAccountNameAvailabilityResult.Available();
            }

            var message = root.TryGetProperty("message", out var messageElement)
                ? messageElement.GetString()
                : null;

            return StorageAccountNameAvailabilityResult.NotAvailable(
                string.IsNullOrWhiteSpace(message)
                    ? $"Storage account name '{accountName}' is not available globally."
                    : message);
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return StorageAccountNameAvailabilityResult.Failed("Customer service principal secret was not found in Key Vault.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Storage account name availability precheck failed.");
            return StorageAccountNameAvailabilityResult.Failed("Could not validate storage account name availability at this time.");
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

public sealed class StorageAccountNameAvailabilityResult
{
    public bool IsAvailable { get; private init; }
    public bool CheckFailed { get; private init; }
    public string? Message { get; private init; }

    public static StorageAccountNameAvailabilityResult Available()
        => new() { IsAvailable = true };

    public static StorageAccountNameAvailabilityResult NotAvailable(string? message)
        => new() { IsAvailable = false, CheckFailed = false, Message = message };

    public static StorageAccountNameAvailabilityResult Failed(string message)
        => new() { IsAvailable = false, CheckFailed = true, Message = message };
}