using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AzSelfService.API.Tests;

public sealed class CustomerCredentialPreflightServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsFail_WhenSecretClientNotConfigured()
    {
        var service = CreateService(serviceProvider: new ServiceCollection().BuildServiceProvider());
        var customer = CreateCustomer();

        var result = await service.CheckAsync(customer, CancellationToken.None);

        Assert.False(result.CanProceed);
        Assert.Contains(result.Issues, issue => issue.Contains("Key Vault client is not configured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckAsync_ReturnsPass_WhenSecretMetadataAndExpiryAreValid()
    {
        var secret = new KeyVaultSecret("customers-test-sp-client-secret", "secret-value");
        secret.Properties.ContentType = "appid=11111111-1111-1111-1111-111111111111";
        secret.Properties.ExpiresOn = DateTimeOffset.UtcNow.AddDays(90);

        var service = CreateService(secret);
        var customer = CreateCustomer();

        var result = await service.CheckAsync(customer, CancellationToken.None);

        Assert.True(result.CanProceed);
        Assert.Empty(result.Issues);
        Assert.Empty(result.Warnings);
        Assert.NotNull(result.SecretExpiry);
        Assert.False(result.SecretExpiry!.ClientSecretExpired);
        Assert.False(result.SecretExpiry.ClientSecretNearExpiry);
    }

    [Fact]
    public async Task CheckAsync_ReturnsWarn_WhenSecretNearExpiry()
    {
        var secret = new KeyVaultSecret("customers-test-sp-client-secret", "secret-value");
        secret.Properties.ContentType = "appid=22222222-2222-2222-2222-222222222222";
        secret.Properties.ExpiresOn = DateTimeOffset.UtcNow.AddDays(3);

        var service = CreateService(secret, warningThresholdDays: 30);
        var customer = CreateCustomer();

        var result = await service.CheckAsync(customer, CancellationToken.None);

        Assert.True(result.CanProceed);
        Assert.Empty(result.Issues);
        Assert.NotEmpty(result.Warnings);
        Assert.Contains(result.Warnings, warning => warning.Contains("expires soon", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(result.SecretExpiry);
        Assert.True(result.SecretExpiry!.ClientSecretNearExpiry);
    }

    private static CustomerCredentialPreflightService CreateService(
        KeyVaultSecret? secret = null,
        int warningThresholdDays = 30,
        IServiceProvider? serviceProvider = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PRECHECK_SECRET_EXPIRY_WARNING_DAYS"] = warningThresholdDays.ToString()
            })
            .Build();

        if (serviceProvider is null)
        {
            var services = new ServiceCollection();

            if (secret is not null)
            {
                var secretClient = new Mock<SecretClient>();
                secretClient
                    .Setup(x => x.GetSecretAsync(
                        It.IsAny<string>(),
                        It.IsAny<string?>(),
                        It.IsAny<CancellationToken>()))
                    .ReturnsAsync(Response.FromValue(secret, Mock.Of<Response>()));

                services.AddSingleton(secretClient.Object);
            }

            serviceProvider = services.BuildServiceProvider();
        }

        return new CustomerCredentialPreflightService(
            config,
            serviceProvider,
            NullLogger<CustomerCredentialPreflightService>.Instance);
    }

    private static CustomerEntity CreateCustomer()
        => new()
        {
            Id = Guid.NewGuid(),
            Name = "Test Customer",
            IsActive = true,
            TenantId = "bf0465f4-f8c0-4ff4-978d-af5315afa795",
            SubscriptionId = "5b337264-50ba-4056-bc9f-1a926a433c18",
            SpClientSecretSecretRef = "customers-test-sp-client-secret"
        };
}
