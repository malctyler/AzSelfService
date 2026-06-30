using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Services;

public sealed class ModuleCatalogBootstrapper(
    AzSelfServiceDbContext dbContext,
    ModuleManifestLoader manifestLoader,
    AllowedRegionCatalogService allowedRegionCatalogService,
    IHostEnvironment hostEnvironment,
    ILogger<ModuleCatalogBootstrapper> logger)
{
    public async Task EnsureModulesRegisteredAsync(CancellationToken cancellationToken)
    {
        var modulesRoot = Path.Combine(hostEnvironment.ContentRootPath, "terraform-modules");
        if (!Directory.Exists(modulesRoot))
        {
            logger.LogWarning("Module root '{ModulesRoot}' does not exist.", modulesRoot);
            return;
        }

        var manifestPaths = Directory.GetFiles(modulesRoot, "module.yaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.GetDirectories(modulesRoot).Select(path => Path.Combine(path, "module.yaml")))
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var allowedRegionCodes = await allowedRegionCatalogService.GetAllowedRegionCodesAsync(cancellationToken);
        var now = DateTime.UtcNow;

        foreach (var manifestPath in manifestPaths)
        {
            var relativeModulePath = Path.GetRelativePath(hostEnvironment.ContentRootPath, Path.GetDirectoryName(manifestPath)!)
                .Replace('\\', '/');

            var manifest = await manifestLoader.LoadAsync(relativeModulePath, cancellationToken);
            var module = await dbContext.Modules.SingleOrDefaultAsync(
                x => x.Name == manifest.Name && x.Version == manifest.Version,
                cancellationToken);

            if (module is null)
            {
                module = new ModuleEntity
                {
                    Id = Guid.NewGuid(),
                    Name = manifest.Name,
                    Version = manifest.Version,
                    CreatedAt = now
                };
                dbContext.Modules.Add(module);
            }

            module.TerraformPath = manifest.TerraformPath;
            module.Schema = allowedRegionCatalogService.ApplyAllowedRegionsToSchemaJson(manifest.SchemaJson, allowedRegionCodes) ?? manifest.SchemaJson;
            module.UiSchema = manifest.UiSchemaJson;
            module.Description = manifest.Description;
            module.IsPublished = manifest.IsPublished;
            module.IsDeprecated = manifest.IsDeprecated;
            module.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}