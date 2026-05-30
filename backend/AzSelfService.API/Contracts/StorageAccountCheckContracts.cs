namespace AzSelfService.API.Contracts;

public sealed class CheckStorageAccountNameRequest
{
    public required string Name { get; set; }
}

public sealed class StorageNameAvailabilityCheckResponse
{
    public required string NameChecked { get; set; }
    public required bool IsAvailable { get; set; }
    public string? Message { get; set; }
}
