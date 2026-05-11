using System.Text.Json;

namespace AzSelfService.API.Contracts;

public sealed class ModuleSummaryResponse
{
    public Guid Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string TerraformPath { get; init; } = string.Empty;
    public string? Description { get; init; }
    public JsonElement Schema { get; init; }
    public JsonElement? UiSchema { get; init; }
}