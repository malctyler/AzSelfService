using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;

namespace AzSelfService.API.Services;

public interface ISoftwarePackageBlobStorageService
{
    Task UploadAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        Stream content,
        CancellationToken cancellationToken);

    Task DeleteIfExistsAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        CancellationToken cancellationToken);

    Task<Uri> CreateReadUriAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken);
}

public sealed class SoftwarePackageBlobStorageService : ISoftwarePackageBlobStorageService
{
    public async Task UploadAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        Stream content,
        CancellationToken cancellationToken)
    {
        var container = CreateContainerClient(storageAccountName, containerName);
        var blob = container.GetBlobClient(blobPath);

        content.Position = 0;
        await blob.UploadAsync(content, overwrite: true, cancellationToken);
    }

    public async Task DeleteIfExistsAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        CancellationToken cancellationToken)
    {
        var container = CreateContainerClient(storageAccountName, containerName);
        var blob = container.GetBlobClient(blobPath);
        await blob.DeleteIfExistsAsync(cancellationToken: cancellationToken);
    }

    public async Task<Uri> CreateReadUriAsync(
        string storageAccountName,
        string containerName,
        string blobPath,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var serviceClient = new BlobServiceClient(new Uri($"https://{storageAccountName}.blob.core.windows.net"), new DefaultAzureCredential());
        var startsOn = DateTimeOffset.UtcNow.AddMinutes(-5);
        var expiresOn = DateTimeOffset.UtcNow.Add(lifetime);
        var userDelegationKey = await serviceClient.GetUserDelegationKeyAsync(startsOn, expiresOn, cancellationToken);

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerName,
            BlobName = blobPath,
            Resource = "b",
            StartsOn = startsOn,
            ExpiresOn = expiresOn
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var sas = sasBuilder.ToSasQueryParameters(userDelegationKey.Value, storageAccountName).ToString();
        return new Uri($"https://{storageAccountName}.blob.core.windows.net/{containerName}/{blobPath}?{sas}");
    }

    private static BlobContainerClient CreateContainerClient(string storageAccountName, string containerName)
    {
        var uri = new Uri($"https://{storageAccountName}.blob.core.windows.net/{containerName}");
        return new BlobContainerClient(uri, new DefaultAzureCredential());
    }
}
