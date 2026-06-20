using AzSelfService.API.Data;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Services;

public sealed class DatabaseBootstrapper(AzSelfServiceDbContext dbContext)
{
    private static readonly string[] DefaultRegionCodes =
    [
        "eastus",
        "westus",
        "eastus2",
        "westeurope",
        "southeastasia",
        "northeurope"
    ];

    public async Task EnsureInfrastructureAsync(CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlRawAsync(
            """
            CREATE TABLE IF NOT EXISTS allowed_regions (
                code VARCHAR(64) PRIMARY KEY,
                sort_order INTEGER NOT NULL DEFAULT 0,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );
            CREATE INDEX IF NOT EXISTS idx_allowed_regions_sort_order ON allowed_regions(sort_order);
            """,
            cancellationToken);

        for (var index = 0; index < DefaultRegionCodes.Length; index++)
        {
            var code = DefaultRegionCodes[index];
            await dbContext.Database.ExecuteSqlRawAsync(
                $"INSERT INTO allowed_regions (code, sort_order) VALUES ('{code}', {index}) ON CONFLICT (code) DO NOTHING;",
                cancellationToken);
        }
    }
}