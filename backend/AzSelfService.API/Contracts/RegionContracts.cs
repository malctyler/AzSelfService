namespace AzSelfService.API.Contracts;

public sealed class AllowedRegionResponse
{
    public string Code { get; init; } = string.Empty;
    public int SortOrder { get; init; }
}

public sealed class UpdateAllowedRegionsRequest
{
    public IReadOnlyList<string> Codes { get; init; } = Array.Empty<string>();
}