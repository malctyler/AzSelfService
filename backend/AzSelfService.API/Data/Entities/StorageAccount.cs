namespace AzSelfService.API.Data.Entities;

public sealed class StorageAccount
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Region { get; set; } = string.Empty;
    public string ResourceGroup { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}