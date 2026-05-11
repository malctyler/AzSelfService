namespace AzSelfService.API.Data.Entities;

public sealed class UserEntity
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? Email { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public bool IsActive { get; set; }

    public CustomerEntity? Customer { get; set; }
    public ICollection<DeploymentEntity> RequestedDeployments { get; set; } = new List<DeploymentEntity>();
}