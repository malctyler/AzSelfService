using System.Text.Json;
using YamlDotNet.Serialization;

namespace AzSelfService.API.Services;

public sealed class ModuleManifestLoader(IHostEnvironment hostEnvironment)
{
    private readonly string _contentRootPath = hostEnvironment.ContentRootPath;

    public async Task<LoadedModuleManifest> LoadAsync(string modulePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            throw new InvalidOperationException("modulePath is required.");
        }

        var normalizedInput = modulePath.Replace('\\', '/').Trim();
        var moduleDirectory = normalizedInput.EndsWith("/module.yaml", StringComparison.OrdinalIgnoreCase)
            ? normalizedInput[..^"/module.yaml".Length]
            : normalizedInput;

        var manifestPath = Path.Combine(_contentRootPath, moduleDirectory, "module.yaml");
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"Module manifest not found at '{manifestPath}'.", manifestPath);
        }

        var yaml = await File.ReadAllTextAsync(manifestPath, cancellationToken);

        var deserializer = new DeserializerBuilder().Build();
        var root = deserializer.Deserialize<Dictionary<object, object?>>(yaml)
            ?? throw new InvalidOperationException("module.yaml is empty.");

        var name = GetRequiredString(root, "name");
        var version = GetRequiredString(root, "version");
        var description = GetOptionalString(root, "description");
        var terraformPath = GetOptionalString(root, "terraform_path") ?? moduleDirectory;

        var schemaElement = BuildSchema(root);
        var uiSchemaElement = TryGetElement(root, "ui_schema");

        return new LoadedModuleManifest
        {
            Name = name,
            Version = version,
            Description = description,
            TerraformPath = terraformPath.Replace('\\', '/'),
            SchemaJson = JsonSerializer.Serialize(schemaElement),
            UiSchemaJson = uiSchemaElement is null ? null : JsonSerializer.Serialize(uiSchemaElement)
        };
    }

    private static Dictionary<string, object?> BuildSchema(IDictionary<object, object?> root)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        if (root.TryGetValue("variables", out var variablesRaw) && variablesRaw is IEnumerable<object> variables)
        {
            foreach (var variableRaw in variables)
            {
                if (variableRaw is not IDictionary<object, object?> variable)
                {
                    continue;
                }

                var name = GetOptionalString(variable, "name");
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                var property = new Dictionary<string, object?>
                {
                    ["type"] = GetOptionalString(variable, "type") ?? "string"
                };

                var description = GetOptionalString(variable, "description");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    property["description"] = description;
                }

                if (variable.TryGetValue("enum", out var enumRaw) && enumRaw is IEnumerable<object> enumValues)
                {
                    property["enum"] = enumValues.Select(ToPlainObject).ToList();
                }

                if (variable.TryGetValue("default", out var defaultRaw))
                {
                    property["default"] = ToPlainObject(defaultRaw);
                }

                if (variable.TryGetValue("sensitive", out var sensitiveRaw) && IsTruthy(sensitiveRaw))
                {
                    property["sensitive"] = true;
                }

                if (variable.TryGetValue("validation", out var validationRaw)
                    && validationRaw is IDictionary<object, object?> validation
                    && validation.TryGetValue("pattern", out var patternRaw)
                    && patternRaw is not null)
                {
                    property["pattern"] = patternRaw.ToString();

                    if (validation.TryGetValue("message", out var messageRaw)
                        && messageRaw is not null)
                    {
                        property["validationMessage"] = messageRaw.ToString();
                    }
                }

                properties[name] = property;

                if (variable.TryGetValue("required", out var requiredRaw)
                    && IsTruthy(requiredRaw))
                {
                    required.Add(name);
                }
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties
        };

        if (required.Count > 0)
        {
            schema["required"] = required;
        }

        return schema;
    }

    private static object? TryGetElement(IDictionary<object, object?> source, string key)
    {
        return source.TryGetValue(key, out var value) ? ToPlainObject(value) : null;
    }

    private static string GetRequiredString(IDictionary<object, object?> source, string key)
    {
        var value = GetOptionalString(source, key);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is required in module.yaml.");
        }

        return value;
    }

    private static string? GetOptionalString(IDictionary<object, object?> source, string key)
    {
        if (!source.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        return value.ToString()?.Trim();
    }

    private static object? ToPlainObject(object? value)
    {
        return value switch
        {
            null => null,
            IDictionary<object, object?> dictionary => dictionary
                .ToDictionary(x => x.Key.ToString() ?? string.Empty, x => ToPlainObject(x.Value), StringComparer.Ordinal),
            IEnumerable<object> list => list.Select(ToPlainObject).ToList(),
            _ => value
        };
    }

    private static bool IsTruthy(object? value)
    {
        return value switch
        {
            bool boolean => boolean,
            string text when bool.TryParse(text, out var parsed) => parsed,
            int number => number != 0,
            long number => number != 0,
            _ => false
        };
    }
}

public sealed class LoadedModuleManifest
{
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string TerraformPath { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string SchemaJson { get; init; } = "{}";
    public string? UiSchemaJson { get; init; }
}