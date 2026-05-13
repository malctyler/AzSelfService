using Azure;
using Azure.Core;
using Azure.Security.KeyVault.Secrets;
using AzSelfService.API.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace AzSelfService.API.Tests;

public sealed class KeyVaultReadinessServiceTests
{
    [Fact]
    public async Task CheckAsync_ReturnsNotReady_WhenKeyVaultUrlMissing()
    {
        var config = new ConfigurationBuilder().Build();
        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = new KeyVaultReadinessService(config, serviceProvider, NullLogger<KeyVaultReadinessService>.Instance);

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains(result.Checks, x => x.Name == "keyvault-config" && !x.Healthy);
    }

    [Fact]
    public async Task CheckAsync_ReturnsNotReady_WhenSecretClientMissing()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:KeyVault:Url"] = "https://azselfservice-1338-kv.vault.azure.net/"
            })
            .Build();

        var serviceProvider = new ServiceCollection().BuildServiceProvider();
        var service = new KeyVaultReadinessService(config, serviceProvider, NullLogger<KeyVaultReadinessService>.Instance);

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.False(result.IsReady);
        Assert.Contains(result.Checks, x => x.Name == "keyvault-client" && !x.Healthy);
    }

    [Fact]
    public async Task CheckAsync_ReturnsReady_WhenSecretClientProbeSucceeds()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Azure:KeyVault:Url"] = "https://azselfservice-1338-kv.vault.azure.net/"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<SecretClient>(new ReadySecretClient());
        var serviceProvider = services.BuildServiceProvider();

        var service = new KeyVaultReadinessService(config, serviceProvider, NullLogger<KeyVaultReadinessService>.Instance);

        var result = await service.CheckAsync(CancellationToken.None);

        Assert.True(result.IsReady);
        Assert.Contains(result.Checks, x => x.Name == "keyvault-access" && x.Healthy);
    }

    private sealed class ReadySecretClient : SecretClient
    {
        public override AsyncPageable<SecretProperties> GetPropertiesOfSecretsAsync(CancellationToken cancellationToken = default)
        {
            var page = Page<SecretProperties>.FromValues([], null, Mock.Of<Response>());
            return AsyncPageable<SecretProperties>.FromPages([page]);
        }
    }
}
