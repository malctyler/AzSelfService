using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AzSelfService.API.Services;

namespace AzSelfService.API.Tests;

public sealed class SoftwarePackageValidationServiceTests
{
    [Fact]
    public async Task ValidateAsync_ReturnsValid_ForWellFormedPackage()
    {
        var service = new SoftwarePackageValidationService();
        await using var zip = BuildPackageZip(
            zipFileName: "igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            packageId: "igorpavlov.7zip",
            version: "24.09.0",
            artifactContent: "test-installer-content",
            tamperArtifactHash: false);

        var result = await service.ValidateAsync(zip, "igorpavlov-7zip-24.09.0-windows-x64-msi.zip", CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal("igorpavlov.7zip", result.PackageId);
        Assert.Equal("24.09.0", result.Version);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenFilenameIsNotConventionCompliant()
    {
        var service = new SoftwarePackageValidationService();
        await using var zip = BuildPackageZip(
            zipFileName: "7zip.zip",
            packageId: "igorpavlov.7zip",
            version: "24.09.0",
            artifactContent: "test-installer-content",
            tamperArtifactHash: false);

        var result = await service.ValidateAsync(zip, "7zip.zip", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("filename must follow", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenArtifactChecksumDoesNotMatch()
    {
        var service = new SoftwarePackageValidationService();
        await using var zip = BuildPackageZip(
            zipFileName: "igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            packageId: "igorpavlov.7zip",
            version: "24.09.0",
            artifactContent: "test-installer-content",
            tamperArtifactHash: true);

        var result = await service.ValidateAsync(zip, "igorpavlov-7zip-24.09.0-windows-x64-msi.zip", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("SHA256 mismatch", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenSilentArgsAreBlank()
    {
        var service = new SoftwarePackageValidationService();
        await using var zip = BuildPackageZip(
            zipFileName: "igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            packageId: "igorpavlov.7zip",
            version: "24.09.0",
            artifactContent: "test-installer-content",
            tamperArtifactHash: false,
            silentArgs: "   ");

        var result = await service.ValidateAsync(zip, "igorpavlov-7zip-24.09.0-windows-x64-msi.zip", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("silentArgs", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ValidateAsync_ReturnsInvalid_WhenSilentInstallDeclarationsAreMissing()
    {
        var service = new SoftwarePackageValidationService();
        await using var zip = BuildPackageZip(
            zipFileName: "igorpavlov-7zip-24.09.0-windows-x64-msi.zip",
            packageId: "igorpavlov.7zip",
            version: "24.09.0",
            artifactContent: "test-installer-content",
            tamperArtifactHash: false,
            silentInstallArgsTested: false,
            rebootSuppressionArgsTested: false);

        var result = await service.ValidateAsync(zip, "igorpavlov-7zip-24.09.0-windows-x64-msi.zip", CancellationToken.None);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, x => x.Contains("silentInstallArgsTested", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(result.Errors, x => x.Contains("rebootSuppressionArgsTested", StringComparison.OrdinalIgnoreCase));
    }

    private static MemoryStream BuildPackageZip(
        string zipFileName,
        string packageId,
        string version,
        string artifactContent,
        bool tamperArtifactHash,
        string? silentArgs = "/i payload\\7z2409-x64.msi /qn /norestart",
        bool silentInstallArgsTested = true,
        bool rebootSuppressionArgsTested = true)
    {
        var artifactPath = "payload/7z2409-x64.msi";
        var artifactBody = Encoding.UTF8.GetBytes(artifactContent);
        var artifactBytes = new byte[8 + artifactBody.Length];
        artifactBytes[0] = 0xD0;
        artifactBytes[1] = 0xCF;
        artifactBytes[2] = 0x11;
        artifactBytes[3] = 0xE0;
        artifactBytes[4] = 0xA1;
        artifactBytes[5] = 0xB1;
        artifactBytes[6] = 0x1A;
        artifactBytes[7] = 0xE1;
        Buffer.BlockCopy(artifactBody, 0, artifactBytes, 8, artifactBody.Length);
        var artifactSha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(artifactBytes)).ToLowerInvariant();
        var manifestSha = tamperArtifactHash
            ? new string('0', 64)
            : artifactSha;

        var manifest = JsonSerializer.Serialize(new
        {
            packageId,
            displayName = "7-Zip",
            version,
            publisher = "Igor Pavlov",
            os = "windows",
            architecture = "x64",
            installerType = "msi",
            entrypoint = "scripts/install.ps1",
            installCommand = "msiexec.exe",
            silentArgs,
            silentInstallArgsTested,
            rebootSuppressionArgsTested,
            expectedExitCodes = new[] { 0, 3010 },
            rebootBehavior = "possible",
            detectionRules = new[]
                {
                                new
                                {
                                        type = "fileExists",
                                        path = "C:\\Program Files\\7-Zip\\7z.exe"
                                }
                        },
            artifacts = new[]
                {
                                new
                                {
                                        path = artifactPath,
                                        sha256 = manifestSha
                                }
                        }
        }, new JsonSerializerOptions { WriteIndented = true });

        var checksums = $"{artifactSha}  {artifactPath}";

        var memory = new MemoryStream();
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

        memory.Position = 0;
        return memory;
    }

    private static void WriteEntry(ZipArchive archive, string path, string content)
    {
        var entry = archive.CreateEntry(path);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(content);
    }
}
