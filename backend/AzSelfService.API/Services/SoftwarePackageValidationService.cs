using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AzSelfService.API.Services;

public sealed class SoftwarePackageValidationService
{
    private static readonly Regex FileNamePattern = new(
        "^(?<vendor>[a-z0-9]+(?:-[a-z0-9]+)*)-(?<product>[a-z0-9]+(?:-[a-z0-9]+)*)-(?<version>\\d+\\.\\d+\\.\\d+)-(?<os>[a-z0-9]+)-(?<arch>[a-z0-9]+)-(?<installer>[a-z0-9]+)\\.zip$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] RequiredFiles =
    [
        "manifest.json",
        "checksums.sha256",
        "scripts/install.ps1",
        "scripts/detect.ps1"
    ];

    private static readonly string[] RequiredManifestProperties =
    [
        "packageId",
        "displayName",
        "version",
        "publisher",
        "os",
        "architecture",
        "installerType",
        "entrypoint",
        "installCommand",
        "silentArgs",
        "silentInstallArgsTested",
        "rebootSuppressionArgsTested",
        "expectedExitCodes",
        "rebootBehavior",
        "detectionRules",
        "artifacts"
    ];

    public async Task<SoftwarePackageValidationResult> ValidateAsync(
        Stream zipStream,
        string zipFileName,
        CancellationToken cancellationToken)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(zipFileName))
        {
            errors.Add("Package filename is required.");
            return new SoftwarePackageValidationResult(false, null, null, null, null, null, null, null, null, errors);
        }

        var fileNameMatch = FileNamePattern.Match(zipFileName);
        if (!fileNameMatch.Success)
        {
            errors.Add("Package filename must follow vendor-product-version-os-arch-installer.zip using lowercase letters, numbers, and hyphens.");
        }

        await using var memory = new MemoryStream();
        await zipStream.CopyToAsync(memory, cancellationToken);
        memory.Position = 0;

        ZipArchive archive;
        try
        {
            archive = new ZipArchive(memory, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            errors.Add("Uploaded file is not a valid zip archive.");
            return new SoftwarePackageValidationResult(false, null, null, null, null, null, null, null, null, errors);
        }

        using (archive)
        {
            var entries = archive.Entries
                .ToDictionary(
                    x => NormalizePath(x.FullName),
                    x => x,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var required in RequiredFiles)
            {
                if (!entries.ContainsKey(required))
                {
                    errors.Add($"Missing required file: {required}");
                }
            }

            if (!entries.TryGetValue("manifest.json", out var manifestEntry))
            {
                return new SoftwarePackageValidationResult(false, null, null, null, null, null, null, null, null, errors);
            }

            JsonDocument? manifest;
            try
            {
                await using var manifestStream = manifestEntry.Open();
                manifest = await JsonDocument.ParseAsync(manifestStream, cancellationToken: cancellationToken);
            }
            catch (Exception)
            {
                errors.Add("manifest.json must be valid JSON.");
                return new SoftwarePackageValidationResult(false, null, null, null, null, null, null, null, null, errors);
            }

            using (manifest)
            {
                foreach (var requiredProperty in RequiredManifestProperties)
                {
                    if (!manifest.RootElement.TryGetProperty(requiredProperty, out _))
                    {
                        errors.Add($"manifest.json is missing required property: {requiredProperty}");
                    }
                }

                var packageId = TryGetString(manifest.RootElement, "packageId");
                var version = TryGetString(manifest.RootElement, "version");
                var displayName = TryGetString(manifest.RootElement, "displayName");
                var publisher = TryGetString(manifest.RootElement, "publisher");
                var os = TryGetString(manifest.RootElement, "os");
                var architecture = TryGetString(manifest.RootElement, "architecture");
                var installerType = TryGetString(manifest.RootElement, "installerType");
                var manifestJson = manifest.RootElement.GetRawText();

                ValidateInstallerMetadata(manifest.RootElement, installerType, errors);

                if (!string.IsNullOrWhiteSpace(version) && fileNameMatch.Success)
                {
                    var versionFromFileName = fileNameMatch.Groups["version"].Value;
                    if (!string.Equals(versionFromFileName, version, StringComparison.Ordinal))
                    {
                        errors.Add($"manifest version '{version}' does not match filename version '{versionFromFileName}'.");
                    }
                }

                if (manifest.RootElement.TryGetProperty("artifacts", out var artifactsElement) && artifactsElement.ValueKind == JsonValueKind.Array)
                {
                    var checksums = ParseChecksums(entries, errors);

                    foreach (var artifact in artifactsElement.EnumerateArray())
                    {
                        var artifactPath = TryGetString(artifact, "path");
                        var artifactSha = TryGetString(artifact, "sha256");

                        if (string.IsNullOrWhiteSpace(artifactPath) || string.IsNullOrWhiteSpace(artifactSha))
                        {
                            errors.Add("Each artifact in manifest.json must include path and sha256.");
                            continue;
                        }

                        var normalizedArtifactPath = NormalizePath(artifactPath);
                        if (!entries.TryGetValue(normalizedArtifactPath, out var artifactEntry))
                        {
                            errors.Add($"Artifact listed in manifest.json not found in zip: {artifactPath}");
                            continue;
                        }

                        var computedSha = ComputeSha256(artifactEntry);
                        if (!string.Equals(computedSha, artifactSha, StringComparison.OrdinalIgnoreCase))
                        {
                            errors.Add($"SHA256 mismatch for artifact '{artifactPath}'.");
                        }

                        // Guard against HTML/error pages accidentally packaged as installers.
                        ValidateInstallerArtifact(
                            installerType,
                            normalizedArtifactPath,
                            artifactEntry,
                            errors);

                        if (checksums.Count > 0)
                        {
                            if (!checksums.TryGetValue(normalizedArtifactPath, out var checksumSha))
                            {
                                errors.Add($"checksums.sha256 missing entry for artifact '{artifactPath}'.");
                            }
                            else if (!string.Equals(checksumSha, artifactSha, StringComparison.OrdinalIgnoreCase))
                            {
                                errors.Add($"checksums.sha256 value mismatch for artifact '{artifactPath}'.");
                            }
                        }
                    }
                }
                else
                {
                    errors.Add("manifest.json property 'artifacts' must be an array.");
                }

                return new SoftwarePackageValidationResult(
                    errors.Count == 0,
                    packageId,
                    version,
                    displayName,
                    publisher,
                    os,
                    architecture,
                    installerType,
                    manifestJson,
                    errors);
            }
        }
    }

    private static Dictionary<string, string> ParseChecksums(
        IReadOnlyDictionary<string, ZipArchiveEntry> entries,
        List<string> errors)
    {
        if (!entries.TryGetValue("checksums.sha256", out var checksumEntry))
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var checksums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = new StreamReader(checksumEntry.Open());

        while (!reader.EndOfStream)
        {
            var line = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var parts = line.Split("  ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2)
            {
                errors.Add($"Invalid checksums.sha256 line: '{line}'");
                continue;
            }

            checksums[NormalizePath(parts[1])] = parts[0];
        }

        return checksums;
    }

    private static string ComputeSha256(ZipArchiveEntry entry)
    {
        using var sha = SHA256.Create();
        using var stream = entry.Open();
        var hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ValidateInstallerArtifact(
        string? installerType,
        string normalizedArtifactPath,
        ZipArchiveEntry artifactEntry,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(installerType))
        {
            return;
        }

        var extension = Path.GetExtension(normalizedArtifactPath);
        if (string.IsNullOrWhiteSpace(extension))
        {
            return;
        }

        try
        {
            using var stream = artifactEntry.Open();
            var header = new byte[16];
            var bytesRead = stream.Read(header, 0, header.Length);
            if (bytesRead == 0)
            {
                errors.Add($"Artifact '{normalizedArtifactPath}' is empty.");
                return;
            }

            if (LooksLikeHtml(header, bytesRead))
            {
                errors.Add($"Artifact '{normalizedArtifactPath}' appears to be HTML, not a binary installer.");
                return;
            }

            if (string.Equals(installerType, "msi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".msi", StringComparison.OrdinalIgnoreCase))
            {
                var isMsi = bytesRead >= 8
                    && header[0] == 0xD0
                    && header[1] == 0xCF
                    && header[2] == 0x11
                    && header[3] == 0xE0
                    && header[4] == 0xA1
                    && header[5] == 0xB1
                    && header[6] == 0x1A
                    && header[7] == 0xE1;

                if (!isMsi)
                {
                    errors.Add($"Artifact '{normalizedArtifactPath}' does not have a valid MSI header.");
                }

                return;
            }

            if (string.Equals(installerType, "exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
            {
                var isExe = bytesRead >= 2
                    && header[0] == 0x4D
                    && header[1] == 0x5A;

                if (!isExe)
                {
                    errors.Add($"Artifact '{normalizedArtifactPath}' does not have a valid EXE header.");
                }
            }
        }
        catch (Exception ex)
        {
            errors.Add($"Failed to inspect artifact '{normalizedArtifactPath}': {ex.Message}");
        }
    }

    private static void ValidateInstallerMetadata(
        JsonElement manifest,
        string? installerType,
        List<string> errors)
    {
        var installCommand = TryGetString(manifest, "installCommand");
        var silentArgs = TryGetString(manifest, "silentArgs");
        var silentInstallArgsTested = TryGetBoolean(manifest, "silentInstallArgsTested");
        var rebootSuppressionArgsTested = TryGetBoolean(manifest, "rebootSuppressionArgsTested");

        if (string.IsNullOrWhiteSpace(installCommand))
        {
            errors.Add("manifest.json property 'installCommand' must be a non-empty string.");
        }

        if (string.IsNullOrWhiteSpace(silentArgs))
        {
            errors.Add("manifest.json property 'silentArgs' must be a non-empty string.");
        }

        if (silentInstallArgsTested != true)
        {
            errors.Add("manifest.json property 'silentInstallArgsTested' must be true.");
        }

        if (rebootSuppressionArgsTested != true)
        {
            errors.Add("manifest.json property 'rebootSuppressionArgsTested' must be true.");
        }

        if (string.Equals(installerType, "msi", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.Equals(installCommand, "msiexec.exe", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("MSI packages must set installCommand to 'msiexec.exe'.");
            }

            if (!string.IsNullOrWhiteSpace(silentArgs)
                && !silentArgs.Contains("/qn", StringComparison.OrdinalIgnoreCase)
                && !silentArgs.Contains("/quiet", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add("MSI packages should use silentArgs containing '/qn' or '/quiet'.");
            }
        }
    }

    private static bool LooksLikeHtml(byte[] header, int bytesRead)
    {
        if (bytesRead <= 0)
        {
            return false;
        }

        var text = System.Text.Encoding.ASCII.GetString(header, 0, bytesRead).TrimStart();
        return text.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<!html", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<html", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string value)
        => value.Replace('\\', '/').TrimStart('/');

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? TryGetBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.True
            ? true
            : value.ValueKind == JsonValueKind.False
                ? false
                : null;
    }
}

public sealed record SoftwarePackageValidationResult(
    bool IsValid,
    string? PackageId,
    string? Version,
    string? DisplayName,
    string? Publisher,
    string? Os,
    string? Architecture,
    string? InstallerType,
    string? ManifestJson,
    IReadOnlyList<string> Errors);
