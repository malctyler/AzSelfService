namespace AzSelfService.API.Data.Entities;

public sealed class AllowedRegionEntity
{
    public string Code { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}