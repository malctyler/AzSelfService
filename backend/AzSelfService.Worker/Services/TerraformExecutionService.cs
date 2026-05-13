using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AzSelfService.Worker.Data.Entities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AzSelfService.Worker.Services;

public sealed class TerraformExecutionService(
    IOptions<WorkerOptions> options,
    ILogger<TerraformExecutionService> logger)
{
    private readonly WorkerOptions _options = options.Value;

    public const string OperationCreate = "create";
    public const string OperationDestroy = "destroy";

    public async Task<string> ExecuteAsync(
        DeploymentEntity deployment,
        string operation,
        ServicePrincipalCredentials credentials,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        var modulePath = ResolveModulePath(deployment);
        var executionRoot = Path.Combine(_options.TerraformWorkingDirectory, deployment.Id.ToString("N"));
        var executionModulePath = Path.Combine(executionRoot, "module");

        Directory.CreateDirectory(executionRoot);
        if (Directory.Exists(executionModulePath))
        {
            Directory.Delete(executionModulePath, recursive: true);
        }

        CopyDirectory(modulePath, executionModulePath);

        var inputsPath = Path.Combine(executionModulePath, "inputs.auto.tfvars.json");
        var normalizedInputs = NormalizeInputsJson(deployment.Input?.Inputs, operation);
        await File.WriteAllTextAsync(inputsPath, normalizedInputs, cancellationToken);

        var stateFilePath = ResolveStateFilePath(deployment);
        if (string.Equals(operation, OperationDestroy, StringComparison.OrdinalIgnoreCase))
        {
            EnsureDestroyStateExists(stateFilePath, deployment.TerraformStatePath);
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(stateFilePath)!);
        }

        await writeLogAsync("INFO", "terraform init", new { modulePath = deployment.Module?.TerraformPath }, cancellationToken);
        await RunTerraformCommandAsync(
            "init -input=false -no-color",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken);

        if (string.Equals(operation, OperationDestroy, StringComparison.OrdinalIgnoreCase))
        {
            await writeLogAsync("INFO", "terraform destroy -auto-approve", null, cancellationToken);
            await RunTerraformCommandAsync(
                $"destroy -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json -state=\"{stateFilePath}\"",
                executionModulePath,
                credentials,
                writeLogAsync,
                cancellationToken);

            logger.LogInformation("Terraform destroy completed for deployment {DeploymentId}.", deployment.Id);
            return JsonSerializer.Serialize(new
            {
                operation = OperationDestroy,
                statePath = deployment.TerraformStatePath,
                destroyed = true
            });
        }

        await writeLogAsync("INFO", "terraform apply -auto-approve", null, cancellationToken);
        await RunTerraformCommandAsync(
            $"apply -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json -state=\"{stateFilePath}\"",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken);

        var output = await RunTerraformCommandAsync(
            $"output -json -state=\"{stateFilePath}\"",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken,
            logOutput: false);

        logger.LogInformation("Terraform execution completed for deployment {DeploymentId}.", deployment.Id);
        return output.Trim();
    }

    private string ResolveModulePath(DeploymentEntity deployment)
    {
        var relativePath = deployment.Module?.TerraformPath;
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new InvalidOperationException("Module terraform path is not configured.");
        }

        var normalizedPath = relativePath.Replace('\\', Path.DirectorySeparatorChar).Trim();
        var modulePath = Path.IsPathRooted(normalizedPath)
            ? normalizedPath
            : Path.Combine(_options.RepositoryRootPath, normalizedPath);

        if (!Directory.Exists(modulePath))
        {
            throw new DirectoryNotFoundException($"Terraform module path does not exist: {modulePath}");
        }

        return modulePath;
    }

    private string ResolveStateFilePath(DeploymentEntity deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment.TerraformStatePath))
        {
            throw new InvalidOperationException("Deployment terraform state path is not configured.");
        }

        var relative = deployment.TerraformStatePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return Path.Combine(_options.TerraformWorkingDirectory, "state", relative);
    }

    private static string NormalizeInputsJson(string? inputJson, string operation)
    {
        if (string.IsNullOrWhiteSpace(inputJson))
        {
            return "{}";
        }

        using var document = JsonDocument.Parse(inputJson);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            return "{}";
        }

        var filtered = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.StartsWith("__", StringComparison.Ordinal))
            {
                continue;
            }

            filtered[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(filtered);
    }

    private static void EnsureDestroyStateExists(string stateFilePath, string? logicalStatePath)
    {
        if (!File.Exists(stateFilePath))
        {
            throw new InvalidOperationException(
                $"Terraform destroy aborted: state file was not found. logicalStatePath='{logicalStatePath}', resolvedPath='{stateFilePath}'.");
        }

        var stateFileInfo = new FileInfo(stateFilePath);
        if (stateFileInfo.Length == 0)
        {
            throw new InvalidOperationException(
                $"Terraform destroy aborted: state file is empty. logicalStatePath='{logicalStatePath}', resolvedPath='{stateFilePath}'.");
        }
    }

    private async Task<string> RunTerraformCommandAsync(
        string arguments,
        string workingDirectory,
        ServicePrincipalCredentials credentials,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken,
        bool logOutput = true)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.TerraformBinaryPath,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.StartInfo.Environment["ARM_CLIENT_ID"] = credentials.ClientId;
        process.StartInfo.Environment["ARM_CLIENT_SECRET"] = credentials.ClientSecret;
        process.StartInfo.Environment["ARM_TENANT_ID"] = credentials.TenantId;
        process.StartInfo.Environment["ARM_SUBSCRIPTION_ID"] = credentials.SubscriptionId;
        process.StartInfo.Environment["ARM_USE_AZUREAD"] = "true";
        process.StartInfo.Environment["TF_IN_AUTOMATION"] = "1";

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stdout.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start terraform process.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        await process.WaitForExitAsync(cancellationToken);

        var warningLines = SplitLines(stderr.ToString());
        foreach (var line in warningLines)
        {
            await writeLogAsync("WARN", line, null, cancellationToken);
        }

        if (logOutput)
        {
            var outputLines = SplitLines(stdout.ToString());
            foreach (var line in outputLines)
            {
                await writeLogAsync("INFO", line, null, cancellationToken);
            }
        }

        if (process.ExitCode != 0)
        {
            var message = $"terraform {arguments} failed with exit code {process.ExitCode}. {stderr}";
            throw new InvalidOperationException(message);
        }

        return stdout.ToString();
    }

    private static void CopyDirectory(string sourcePath, string destinationPath)
    {
        var sourceDirectory = new DirectoryInfo(sourcePath);
        Directory.CreateDirectory(destinationPath);

        foreach (var file in sourceDirectory.GetFiles())
        {
            file.CopyTo(Path.Combine(destinationPath, file.Name), overwrite: true);
        }

        foreach (var directory in sourceDirectory.GetDirectories())
        {
            CopyDirectory(directory.FullName, Path.Combine(destinationPath, directory.Name));
        }
    }

    private static IReadOnlyList<string> SplitLines(string content)
    {
        return content
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();
    }
}