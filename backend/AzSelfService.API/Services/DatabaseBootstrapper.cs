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

            CREATE TABLE IF NOT EXISTS software_packages (
                id UUID PRIMARY KEY,
                scope VARCHAR(32) NOT NULL,
                customer_id UUID NULL REFERENCES customers(id) ON DELETE SET NULL,
                package_id VARCHAR(255) NOT NULL,
                version VARCHAR(50) NOT NULL,
                display_name VARCHAR(255) NOT NULL,
                publisher VARCHAR(255) NOT NULL,
                os VARCHAR(64) NOT NULL,
                architecture VARCHAR(64) NOT NULL,
                installer_type VARCHAR(64) NOT NULL,
                blob_path VARCHAR(1024) NOT NULL,
                zip_sha256 VARCHAR(64) NOT NULL,
                manifest_json JSONB NULL,
                is_published BOOLEAN NOT NULL DEFAULT TRUE,
                created_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP
            );

            CREATE UNIQUE INDEX IF NOT EXISTS idx_software_packages_scope_customer_package_version
                ON software_packages(scope, customer_id, package_id, version);
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