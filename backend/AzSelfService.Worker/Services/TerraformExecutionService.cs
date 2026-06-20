using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    public const string OperationImport = "import";

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

        var stateKey = ResolveStateKey(deployment);
        var initArgs = BuildInitArgs(stateKey);

        await writeLogAsync("INFO", "terraform init", new { modulePath = deployment.Module?.TerraformPath, stateKey }, cancellationToken);
        await RunTerraformCommandAsync(
            initArgs,
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken);

        if (string.Equals(operation, OperationDestroy, StringComparison.OrdinalIgnoreCase))
        {
            await EnsureRemoteStateHasResourcesAsync(executionModulePath, credentials, writeLogAsync, cancellationToken);

            // Before destroy, remove VM extension resources from Terraform state so Terraform
            // does not call the Azure API to delete them individually. Azure extensions are
            // automatically removed when their parent VM is deleted. This avoids the
            // "Cannot modify extensions in the VM when the VM is not running" 409 error that
            // occurs when the VM is deallocated prior to destroy.
            await RemoveVmExtensionsFromStateAsync(executionModulePath, credentials, writeLogAsync, cancellationToken);

            await writeLogAsync("INFO", "terraform destroy -auto-approve", null, cancellationToken);
            try
            {
                await RunTerraformCommandAsync(
                    "destroy -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json",
                    executionModulePath,
                    credentials,
                    writeLogAsync,
                    cancellationToken);
            }
            catch (InvalidOperationException ex) when (IsStateLockError(ex.Message))
            {
                var lockId = ParseStateLockId(ex.Message);
                if (!string.IsNullOrWhiteSpace(lockId))
                {
                    await writeLogAsync("WARN", $"Terraform state lock detected (ID: {lockId}). Forcing unlock and retrying destroy.", new { lockId }, cancellationToken);
                    await RunTerraformCommandAsync(
                        $"force-unlock -force {lockId}",
                        executionModulePath,
                        credentials,
                        writeLogAsync,
                        cancellationToken);
                    await writeLogAsync("INFO", "terraform destroy -auto-approve (retry after force-unlock)", null, cancellationToken);
                    await RunTerraformCommandAsync(
                        "destroy -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json",
                        executionModulePath,
                        credentials,
                        writeLogAsync,
                        cancellationToken);
                }
                else
                {
                    throw;
                }
            }

            logger.LogInformation("Terraform destroy completed for deployment {DeploymentId}.", deployment.Id);
            return JsonSerializer.Serialize(new
            {
                operation = OperationDestroy,
                stateKey,
                destroyed = true
            });
        }

        if (string.Equals(operation, OperationImport, StringComparison.OrdinalIgnoreCase))
        {
            var importBlocks = ExtractImportBlocks(deployment.Input?.Inputs);

            if (importBlocks.Count > 0)
            {
                // Multi-resource import (e.g. virtual-network with subnets)
                // Check what's already in state so retries don't re-import resources
                // that were successfully imported in a previous (failed) attempt.
                var stateListOutput = await RunTerraformCommandAsync(
                    "state list -no-color",
                    executionModulePath,
                    credentials,
                    writeLogAsync,
                    cancellationToken,
                    logOutput: false);
                var alreadyInState = new HashSet<string>(
                    SplitLines(stateListOutput).Select(l => l.Trim()).Where(l => l.Length > 0),
                    StringComparer.Ordinal);

                foreach (var (address, resourceId) in importBlocks)
                {
                    if (alreadyInState.Contains(address))
                    {
                        await writeLogAsync("INFO", $"Skipping import of {address}: already in Terraform state.", new { address }, cancellationToken);
                        continue;
                    }

                    await writeLogAsync("INFO", $"terraform import {address}", new { address, resourceId }, cancellationToken);
                    await RunTerraformImportAsync(address, resourceId, executionModulePath, credentials, writeLogAsync, cancellationToken);
                }
            }
            else
            {
                // Single-resource import (legacy path)
                var resourceId = ExtractResourceId(deployment.Input?.Inputs)
                    ?? throw new InvalidOperationException("__resource_id is required for import operations.");
                var importAddress = ResolveImportAddress(deployment);
                await writeLogAsync("INFO", $"terraform import {importAddress}", new { importAddress, resourceId }, cancellationToken);
                await RunTerraformImportAsync(importAddress, resourceId, executionModulePath, credentials, writeLogAsync, cancellationToken);
            }

            var importOutput = await RunTerraformCommandAsync(
                "output -json",
                executionModulePath,
                credentials,
                writeLogAsync,
                cancellationToken,
                logOutput: false);

            logger.LogInformation("Terraform import completed for deployment {DeploymentId}.", deployment.Id);
            return importOutput.Trim();
        }

        await writeLogAsync("INFO", "terraform apply -auto-approve", null, cancellationToken);
        var replaceResources = ExtractReplaceResources(deployment.Input?.Inputs);
        var applyOutput = await ApplyWithConflictRecoveryAsync(
            executionModulePath,
            credentials,
            replaceResources,
            writeLogAsync,
            cancellationToken);

        var output = await RunTerraformCommandAsync(
            "output -json",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken,
            logOutput: false);

        logger.LogInformation("Terraform execution completed for deployment {DeploymentId}.", deployment.Id);
        return output.Trim();
    }

    /// <summary>
    /// Runs terraform apply. If the apply fails because resources already exist in Azure but are
    /// absent from Terraform state (a common scenario when a previous apply partially succeeded
    /// and the worker retried with a fresh state), the conflicting resources are automatically
    /// <summary>
    /// Runs terraform apply, looping through import + retry passes until there are no more
    /// "already exists" conflicts or the pass limit is reached.
    /// Terraform resolves resources in dependency order, so a single apply only surfaces the
    /// first unresolvable layer. Multiple passes are required for a full VM stack
    /// (PIP → NIC → VM → extension) when a previous deployment left resources in Azure
    /// without a matching state file (e.g. after a state path migration).
    /// </summary>
    private async Task<string> ApplyWithConflictRecoveryAsync(
        string workingDirectory,
        ServicePrincipalCredentials credentials,
        IReadOnlyList<string> replaceResources,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        // Build -replace=<address> flags for resources that cannot be updated in-place
        // (e.g. VM extensions). This forces Terraform to destroy and recreate them.
        var replaceFlags = replaceResources.Count > 0
            ? string.Join(" ", replaceResources.Select(r => $"-replace=\"{r}\""))
            : string.Empty;

        var applyArgs = string.IsNullOrEmpty(replaceFlags)
            ? "apply -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json"
            : $"apply -auto-approve -input=false -no-color -var-file=inputs.auto.tfvars.json {replaceFlags}";

        if (replaceResources.Count > 0)
        {
            await writeLogAsync("INFO",
                $"Forcing replace of {replaceResources.Count} resource(s) that cannot be updated in-place.",
                new { replaceResources },
                cancellationToken);
        }

        const int maxConflictPasses = 10;
        for (var pass = 1; pass <= maxConflictPasses; pass++)
        {
            try
            {
                return await RunTerraformCommandAsync(applyArgs, workingDirectory, credentials, writeLogAsync, cancellationToken);
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase))
            {
                var conflicts = ParseAlreadyExistsConflicts(ex.Message);
                if (conflicts.Count == 0)
                {
                    throw;
                }

                await writeLogAsync("WARN",
                    $"Pass {pass}: detected {conflicts.Count} resource(s) present in Azure but missing from " +
                    "Terraform state. Importing before retrying apply.",
                    new { pass, count = conflicts.Count },
                    cancellationToken);

                foreach (var (address, resourceId) in conflicts)
                {
                    await writeLogAsync("INFO", $"terraform import {address}", new { address, resourceId }, cancellationToken);
                    await RunTerraformImportAsync(address, resourceId, workingDirectory, credentials, writeLogAsync, cancellationToken);
                }

                await writeLogAsync("INFO", $"terraform apply -auto-approve (pass {pass} retry after import)", null, cancellationToken);
            }
        }

        throw new InvalidOperationException($"Terraform apply still has unresolved resource conflicts after {maxConflictPasses} import passes.");
    }

    /// <summary>
    /// Parses Terraform stderr for "A resource with the ID ... already exists" error blocks and
    /// returns a list of (Terraform address, Azure resource ID) pairs to import.
    /// </summary>
    private static IReadOnlyList<(string Address, string ResourceId)> ParseAlreadyExistsConflicts(string errorText)
    {
        // Error block format (stderr, may span multiple lines):
        //   Error: A resource with the ID "<azure-id>" already exists - to be managed via Terraform ...
        //   with <tf-address>,
        var matches = Regex.Matches(
            errorText,
            @"resource with the ID ""([^""]+)"" already exists[\s\S]*?with ([\w.\[\]""]+),",
            RegexOptions.IgnoreCase);

        var results = new List<(string, string)>();
        foreach (Match match in matches)
        {
            var resourceId = match.Groups[1].Value.Trim();
            var address    = match.Groups[2].Value.Trim();
            if (!string.IsNullOrWhiteSpace(resourceId) && !string.IsNullOrWhiteSpace(address))
            {
                results.Add((address, resourceId));
            }
        }

        return results;
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

    private static string ResolveStateKey(DeploymentEntity deployment)
    {
        if (string.IsNullOrWhiteSpace(deployment.TerraformStatePath))
        {
            throw new InvalidOperationException("Deployment terraform state path is not configured.");
        }

        return deployment.TerraformStatePath.Replace('\\', '/');
    }

    private string BuildInitArgs(string stateKey)
    {
        var accountName = _options.AzureStorageAccountName;
        var containerName = _options.AzureStorageContainerName;

        if (string.IsNullOrWhiteSpace(accountName))
        {
            throw new InvalidOperationException("AzureStorageAccountName must be configured for remote Terraform state.");
        }

        var initArgs = $"init -input=false -no-color -reconfigure" +
                       $" -backend-config=\"storage_account_name={accountName}\"" +
                       $" -backend-config=\"container_name={containerName}\"" +
                       $" -backend-config=\"key={stateKey}\"";

        // Add platform backend authentication (use access key or SAS token, not customer credentials)
        if (!string.IsNullOrWhiteSpace(_options.AzureStorageBackendAccessKey))
        {
            initArgs += $" -backend-config=\"access_key={_options.AzureStorageBackendAccessKey}\"";
        }
        else if (!string.IsNullOrWhiteSpace(_options.AzureStorageBackendSasToken))
        {
            initArgs += $" -backend-config=\"sas_token={_options.AzureStorageBackendSasToken}\"";
        }

        return initArgs;
    }

    private async Task EnsureRemoteStateHasResourcesAsync(
        string executionModulePath,
        ServicePrincipalCredentials credentials,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        var stateList = await RunTerraformCommandAsync(
            "state list -no-color",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken,
            logOutput: false);

        if (string.IsNullOrWhiteSpace(stateList))
        {
            throw new InvalidOperationException(
                "Terraform destroy aborted: no resources found in remote state. The infrastructure may already have been destroyed.");
        }
    }

    /// <summary>
    /// Removes VM extension resources from Terraform state before a destroy operation so that
    /// Terraform does not call the Azure API to delete them individually. Azure automatically
    /// removes extensions when their parent VM is deleted, so this is safe. Without this step,
    /// a destroy against a deallocated VM fails with:
    ///   "Cannot modify extensions in the VM when the VM is not running."
    /// </summary>
    private async Task RemoveVmExtensionsFromStateAsync(
        string executionModulePath,
        ServicePrincipalCredentials credentials,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        var stateList = await RunTerraformCommandAsync(
            "state list -no-color",
            executionModulePath,
            credentials,
            writeLogAsync,
            cancellationToken,
            logOutput: false);

        var extensionAddresses = SplitLines(stateList)
            .Select(l => l.Trim())
            .Where(l => l.Contains("virtual_machine_extension", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (extensionAddresses.Count == 0)
        {
            return;
        }

        await writeLogAsync("INFO",
            $"Removing {extensionAddresses.Count} VM extension(s) from Terraform state before destroy to avoid " +
            "'VM is not running' errors on deallocated VMs. Azure removes extensions automatically when the VM is deleted.",
            new { extensionAddresses },
            cancellationToken);

        foreach (var address in extensionAddresses)
        {
            await RunTerraformCommandAsync(
                $"state rm -no-color \"{address}\"",
                executionModulePath,
                credentials,
                writeLogAsync,
                cancellationToken);
        }
    }

    private static bool IsStateLockError(string errorMessage)
        => errorMessage.Contains("state blob is already locked", StringComparison.OrdinalIgnoreCase)
           || errorMessage.Contains("Error acquiring the state lock", StringComparison.OrdinalIgnoreCase);

    private static string? ParseStateLockId(string errorMessage)
    {
        var match = Regex.Match(errorMessage, @"ID:\s+([0-9a-f\-]{36})", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static IReadOnlyList<string> ExtractReplaceResources(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
            return Array.Empty<string>();

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("__replace_resources", out var el)
                || el.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<string>();
            }

            return el.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
        }
        catch (JsonException)
        {
            return Array.Empty<string>();
        }
    }

    private static IReadOnlyList<(string Address, string ResourceId)> ExtractImportBlocks(string? inputsJson)    {
        if (string.IsNullOrWhiteSpace(inputsJson))
            return Array.Empty<(string, string)>();

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("__import_blocks", out var blocksEl)
                || blocksEl.ValueKind != JsonValueKind.Array)
            {
                return Array.Empty<(string, string)>();
            }

            var result = new List<(string, string)>();
            foreach (var block in blocksEl.EnumerateArray())
            {
                if (block.ValueKind != JsonValueKind.Object) continue;
                if (!block.TryGetProperty("address", out var addrEl) || addrEl.ValueKind != JsonValueKind.String) continue;
                if (!block.TryGetProperty("resourceId", out var ridEl) || ridEl.ValueKind != JsonValueKind.String) continue;
                var addr = addrEl.GetString();
                var rid = ridEl.GetString();
                if (!string.IsNullOrWhiteSpace(addr) && !string.IsNullOrWhiteSpace(rid))
                    result.Add((addr!, rid!));
            }
            return result;
        }
        catch (JsonException)
        {
            return Array.Empty<(string, string)>();
        }
    }

    private async Task RunTerraformImportAsync(
        string address,
        string resourceId,
        string workingDirectory,
        ServicePrincipalCredentials credentials,
        Func<string, string, object?, CancellationToken, Task> writeLogAsync,
        CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _options.TerraformBinaryPath,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // Use ArgumentList to avoid shell-quoting issues with resource addresses like azurerm_subnet.this["0"]
        process.StartInfo.ArgumentList.Add("import");
        process.StartInfo.ArgumentList.Add("-no-color");
        process.StartInfo.ArgumentList.Add("-input=false");
        process.StartInfo.ArgumentList.Add("-var-file=inputs.auto.tfvars.json");
        process.StartInfo.ArgumentList.Add(address);
        process.StartInfo.ArgumentList.Add(resourceId);

        process.StartInfo.Environment["ARM_CLIENT_ID"] = credentials.ClientId;
        process.StartInfo.Environment["ARM_CLIENT_SECRET"] = credentials.ClientSecret;
        process.StartInfo.Environment["ARM_TENANT_ID"] = credentials.TenantId;
        process.StartInfo.Environment["ARM_SUBSCRIPTION_ID"] = credentials.SubscriptionId;
        process.StartInfo.Environment["ARM_USE_AZUREAD"] = "true";
        process.StartInfo.Environment["TF_IN_AUTOMATION"] = "1";

        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start terraform import process for '{address}'.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        foreach (var line in SplitLines(stderr.ToString()))
            await writeLogAsync("WARN", line, null, cancellationToken);
        foreach (var line in SplitLines(stdout.ToString()))
            await writeLogAsync("INFO", line, null, cancellationToken);

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"terraform import '{address}' failed with exit code {process.ExitCode}. {stderr}");
    }

    private static string? ExtractResourceId(string? inputsJson)
    {
        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(inputsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("__resource_id", out var el)
                && el.ValueKind == JsonValueKind.String)
            {
                return el.GetString();
            }
        }
        catch (JsonException)
        {
            // ignore malformed input
        }

        return null;
    }

    private static string ResolveImportAddress(DeploymentEntity deployment)
    {
        var moduleName = (deployment.Module?.Name ?? string.Empty).Trim().ToLowerInvariant();
        return moduleName switch
        {
            "resource-group" => "azurerm_resource_group.rg",
            "storage-account" => "azurerm_storage_account.this",
            "keyvault" => "azurerm_key_vault.this",
            "network-security-group" => "azurerm_network_security_group.this",
            "network-security-rule" => "azurerm_network_security_rule.this",
            "public-ip" => "azurerm_public_ip.this",
            "local-network-gateway" => "azurerm_local_network_gateway.this",
            "virtual-network-gateway" => "azurerm_virtual_network_gateway.this",
            "virtual-network-peering" => "azurerm_virtual_network_peering.this",
            "bastion-host" => "azurerm_bastion_host.this",
            "subnet" => "azurerm_subnet.this",
            _ => throw new InvalidOperationException($"Import is not supported for module '{moduleName}'.")
        };
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

            if (ShouldTreatAsObject(property.Name))
            {
                filtered[property.Name] = NormalizeObjectInput(property.Name, property.Value);
                continue;
            }

            filtered[property.Name] = property.Value.Clone();
        }

        return JsonSerializer.Serialize(filtered);
    }

    private static bool ShouldTreatAsObject(string propertyName)
    {
        return propertyName.Equals("tags", StringComparison.OrdinalIgnoreCase)
               || propertyName.EndsWith("_tags", StringComparison.OrdinalIgnoreCase);
    }

    private static JsonElement NormalizeObjectInput(string propertyName, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            return value.Clone();
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var rawText = value.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return JsonSerializer.SerializeToElement(new Dictionary<string, string>());
            }

            try
            {
                using var parsedDocument = JsonDocument.Parse(rawText);
                if (parsedDocument.RootElement.ValueKind == JsonValueKind.Object)
                {
                    return parsedDocument.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Fall through to the empty object fallback below.
            }

            return JsonSerializer.SerializeToElement(new Dictionary<string, string>());
        }

        return value.Clone();
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

        // Provider credentials: customer SP for resource provisioning in customer subscription
        process.StartInfo.Environment["ARM_CLIENT_ID"] = credentials.ClientId;
        process.StartInfo.Environment["ARM_CLIENT_SECRET"] = credentials.ClientSecret;
        process.StartInfo.Environment["ARM_TENANT_ID"] = credentials.TenantId;
        process.StartInfo.Environment["ARM_SUBSCRIPTION_ID"] = credentials.SubscriptionId;
        process.StartInfo.Environment["ARM_USE_AZUREAD"] = "true";
        
        // Terraform automation mode
        process.StartInfo.Environment["TF_IN_AUTOMATION"] = "1";
        
        // Note: Backend authentication (access_key or sas_token) is passed via init backend-config args.
        // This ensures backend state access uses platform credentials, not customer SP credentials.

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