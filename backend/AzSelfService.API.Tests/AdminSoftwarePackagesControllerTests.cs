using System.Security.Claims;
using AzSelfService.API.Controllers;
using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using AzSelfService.API.Security;
using AzSelfService.API.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.IO.Compression;
using System.Text;

namespace AzSelfService.API.Tests;

public sealed class AdminSoftwarePackagesControllerTests
{
    [Fact]
    public async Task Publish_ReturnsForbid_WhenCallerIsNotAdmin()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, new FakeSoftwarePackageBlobStorageService(), username: "standard-user", role: AppRoles.Customer);

        var result = await controller.Publish(new PublishSoftwarePackageRequest
        {
            Scope = "platform",
            PackageId = "igorpavlov.7zip",
            Version = "24.09.0",
            DisplayName = "7-Zip",
            Publisher = "Igor Pavlov",
            Os = "windows",
            Architecture = "x64",
            InstallerType = "msi",
            BlobPath = "catalog/platform/igorpavlov.7zip/24.09.0/igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            ZipSha256 = new string('a', 64)
        }, CancellationToken.None);

        Assert.IsType<ForbidResult>(result.Result);
    }

    [Fact]
    public async Task Publish_ReturnsBadRequest_WhenBlobPathPrefixIsInvalid()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, new FakeSoftwarePackageBlobStorageService(), username: "admin", role: AppRoles.Admin);

        var result = await controller.Publish(new PublishSoftwarePackageRequest
        {
            Scope = "platform",
            PackageId = "igorpavlov.7zip",
            Version = "24.09.0",
            DisplayName = "7-Zip",
            Publisher = "Igor Pavlov",
            Os = "windows",
            Architecture = "x64",
            InstallerType = "msi",
            BlobPath = "catalog/platform/wrong.package/24.09.0/igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            ZipSha256 = new string('a', 64)
        }, CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(result.Result);
    }

    [Fact]
    public async Task Publish_Upserts_And_GetCatalog_ReturnsRecords()
    {
        await using var db = CreateDbContext();
        var controller = CreateController(db, new FakeSoftwarePackageBlobStorageService(), username: "admin", role: AppRoles.Admin);

        var publishResult = await controller.Publish(new PublishSoftwarePackageRequest
        {
            Scope = "platform",
            PackageId = "igorpavlov.7zip",
            Version = "24.09.0",
            DisplayName = "7-Zip",
            Publisher = "Igor Pavlov",
            Os = "windows",
            Architecture = "x64",
            InstallerType = "msi",
            BlobPath = "catalog/platform/igorpavlov.7zip/24.09.0/igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            ZipSha256 = new string('a', 64),
            IsPublished = true
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(publishResult.Result);
        var payload = Assert.IsType<SoftwarePackageCatalogItemResponse>(ok.Value);
        Assert.Equal("igorpavlov.7zip", payload.PackageId);
        Assert.Equal("24.09.0", payload.Version);

        var upsertResult = await controller.Publish(new PublishSoftwarePackageRequest
        {
            Scope = "platform",
            PackageId = "igorpavlov.7zip",
            Version = "24.09.0",
            DisplayName = "7-Zip Updated",
            Publisher = "Igor Pavlov",
            Os = "windows",
            Architecture = "x64",
            InstallerType = "msi",
            BlobPath = "catalog/platform/igorpavlov.7zip/24.09.0/igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            ZipSha256 = new string('b', 64),
            IsPublished = false
        }, CancellationToken.None);

        var upsertOk = Assert.IsType<OkObjectResult>(upsertResult.Result);
        var upsertPayload = Assert.IsType<SoftwarePackageCatalogItemResponse>(upsertOk.Value);
        Assert.Equal(payload.Id, upsertPayload.Id);
        Assert.Equal("7-Zip Updated", upsertPayload.DisplayName);
        Assert.False(upsertPayload.IsPublished);

        var listResult = await controller.GetCatalog(scope: "platform", customerId: null, CancellationToken.None);
        var listOk = Assert.IsType<OkObjectResult>(listResult.Result);
        var list = Assert.IsAssignableFrom<IReadOnlyList<SoftwarePackageCatalogItemResponse>>(listOk.Value);

        Assert.Single(list);
        Assert.Equal("igorpavlov.7zip", list[0].PackageId);
        Assert.Equal("24.09.0", list[0].Version);
    }

    [Fact]
    public async Task Upload_ValidPackage_UploadsAndUpsertsCatalog()
    {
        await using var db = CreateDbContext();
        var blobStore = new FakeSoftwarePackageBlobStorageService();
        var controller = CreateController(db, blobStore, username: "admin", role: AppRoles.Admin);

        var packageBytes = BuildValidPackageZipBytes();
        var formFile = new FormFile(new MemoryStream(packageBytes), 0, packageBytes.Length, "PackageFile", "igorpavlov-7zip-24.09.0-windows-x64-msi.zip");

        var result = await controller.Upload(new UploadSoftwarePackageRequest
        {
            Scope = "platform",
            StorageAccountName = "azselfservicesoftware01",
            ContainerName = "packages",
            IsPublished = true,
            PackageFile = formFile
        }, CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        var payload = Assert.IsType<SoftwarePackageCatalogItemResponse>(ok.Value);

        Assert.Equal("igorpavlov.7zip", payload.PackageId);
        Assert.Equal("24.09.0", payload.Version);
        Assert.StartsWith("catalog/platform/igorpavlov.7zip/24.09.0/", payload.BlobPath);

        Assert.Single(blobStore.Uploads);
        Assert.Equal("azselfservicesoftware01", blobStore.Uploads[0].StorageAccountName);
        Assert.Equal("packages", blobStore.Uploads[0].ContainerName);
    }

    private static AdminSoftwarePackagesController CreateController(AzSelfServiceDbContext db, FakeSoftwarePackageBlobStorageService blobStore, string username, string role)
    {
        return new AdminSoftwarePackagesController(db, new SoftwarePackageValidationService(), blobStore)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = BuildPrincipal(username, role)
                }
            }
        };
    }

    private static ClaimsPrincipal BuildPrincipal(string username, string role)
    {
        var userId = Guid.NewGuid();
        return new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("username", username),
                    new Claim("customer_id", Guid.NewGuid().ToString()),
                    new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                    new Claim("sub", userId.ToString()),
                    new Claim(ClaimTypes.Role, role),
                    new Claim("role", role)
                },
                authenticationType: "Test"));
    }

    private static AzSelfServiceDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<AzSelfServiceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new AzSelfServiceDbContext(options);
    }

    private static byte[] BuildValidPackageZipBytes()
    {
        var artifactPath = "payload/7z2409-x64.msi";
        var artifactBytes = new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00 };
        var artifactSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artifactBytes)).ToLowerInvariant();

        var manifest = $$"""
                {
                    "packageId": "igorpavlov.7zip",
                    "displayName": "7-Zip",
                    "version": "24.09.0",
                    "publisher": "Igor Pavlov",
                    "os": "windows",
                    "architecture": "x64",
                    "installerType": "msi",
                    "entrypoint": "scripts/install.ps1",
                    "installCommand": "msiexec.exe",
                    "silentArgs": "/i payload\\7z2409-x64.msi /qn /norestart",
                    "silentInstallArgsTested": true,
                    "rebootSuppressionArgsTested": true,
                    "expectedExitCodes": [0, 3010],
                    "rebootBehavior": "possible",
                    "detectionRules": [
                        {
                            "type": "fileExists",
                            "path": "C:\\Program Files\\7-Zip\\7z.exe"
                        }
                    ],
                    "artifacts": [
                        {
                            "path": "{{artifactPath}}",
                            "sha256": "{{artifactSha}}"
                        }
                    ]
                }
                """;

        var checksums = $"{artifactSha}  {artifactPath}";

        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteEntry(archive, "manifest.json", manifest);
            WriteEntry(archive, "checksums.sha256", checksums);
            WriteEntry(archive, "scripts/install.ps1", "Write-Host 'install'");
            WriteEntry(archive, "scripts/detect.ps1", "exit 0");

            var payloadEntry = archive.CreateEntry(artifactPath);
            using var payloadStream = payloadEntry.Open();
            payloadStream.Write(artifactBytes, 0, artifactBytes.Length);
        }

        return memory.ToArray();
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }

    private sealed class FakeSoftwarePackageBlobStorageService : ISoftwarePackageBlobStorageService
    {
        public List<(string StorageAccountName, string ContainerName, string BlobPath, byte[] Content)> Uploads { get; } = [];

        public Task UploadAsync(string storageAccountName, string containerName, string blobPath, Stream content, CancellationToken cancellationToken)
        {
            using var memory = new MemoryStream();
            content.Position = 0;
            content.CopyTo(memory);
            Uploads.Add((storageAccountName, containerName, blobPath, memory.ToArray()));
            return Task.CompletedTask;
        }

        public Task DeleteIfExistsAsync(string storageAccountName, string containerName, string blobPath, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<Uri> CreateReadUriAsync(string storageAccountName, string containerName, string blobPath, TimeSpan lifetime, CancellationToken cancellationToken)
        {
            return Task.FromResult(new Uri($"https://example.invalid/{containerName}/{blobPath}?sig=test"));
        }
    }
}
