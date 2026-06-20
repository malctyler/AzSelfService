using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AzSelfService.API.Contracts;
using AzSelfService.API.Data;
using AzSelfService.API.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace AzSelfService.API.Services;

public sealed class AllowedRegionCatalogService(AzSelfServiceDbContext dbContext)
{
    private static readonly Regex RegionCodePattern = new("^[a-z0-9]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public async Task<IReadOnlyList<AllowedRegionResponse>> GetAllowedRegionsAsync(CancellationToken cancellationToken)
    {
        return await dbContext.AllowedRegions
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .Select(x => new AllowedRegionResponse
            {
                Code = x.Code,
                SortOrder = x.SortOrder
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetAllowedRegionCodesAsync(CancellationToken cancellationToken)
    {
        return await dbContext.AllowedRegions
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Code)
            .Select(x => x.Code)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AllowedRegionResponse>> ReplaceAllowedRegionsAsync(
        IEnumerable<string> rawCodes,
        CancellationToken cancellationToken)
    {
        var normalizedCodes = NormalizeCodes(rawCodes);
        if (normalizedCodes.Count == 0)
        {
            throw new InvalidOperationException("At least one allowed region is required.");
        }

        var existing = await dbContext.AllowedRegions.ToListAsync(cancellationToken);
        var existingByCode = existing.ToDictionary(x => x.Code, StringComparer.Ordinal);
        var now = DateTime.UtcNow;

        foreach (var obsolete in existing.Where(x => !normalizedCodes.Contains(x.Code, StringComparer.Ordinal)).ToList())
        {
            dbContext.AllowedRegions.Remove(obsolete);
        }

        for (var index = 0; index < normalizedCodes.Count; index++)
        {
            var code = normalizedCodes[index];
            if (!existingByCode.TryGetValue(code, out var region))
            {
                region = new AllowedRegionEntity
                {
                    Code = code,
                    CreatedAt = now
                };
                dbContext.AllowedRegions.Add(region);
            }

            region.SortOrder = index;
            region.UpdatedAt = now;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return await GetAllowedRegionsAsync(cancellationToken);
    }

    public string? ApplyAllowedRegionsToSchemaJson(string? schemaJson, IReadOnlyList<string> allowedRegionCodes)
    {
        if (string.IsNullOrWhiteSpace(schemaJson) || allowedRegionCodes.Count == 0)
        {
            return schemaJson;
        }

        JsonNode? rootNode;
        try
        {
            rootNode = JsonNode.Parse(schemaJson);
        }
        catch (JsonException)
        {
            return schemaJson;
        }

        if (rootNode is not JsonObject rootObject
            || rootObject["properties"] is not JsonObject properties
            || properties["location"] is not JsonObject locationProperty)
        {
            return schemaJson;
        }

        locationProperty["enum"] = new JsonArray(allowedRegionCodes.Select(code => (JsonNode)code).ToArray());
        return rootObject.ToJsonString();
    }

    public JsonElement ApplyAllowedRegionsToSchema(JsonElement schema, IReadOnlyList<string> allowedRegionCodes)
    {
        var schemaJson = ApplyAllowedRegionsToSchemaJson(schema.GetRawText(), allowedRegionCodes) ?? schema.GetRawText();
        using var document = JsonDocument.Parse(schemaJson);
        return document.RootElement.Clone();
    }

    private static List<string> NormalizeCodes(IEnumerable<string> rawCodes)
    {
        var results = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var rawCode in rawCodes)
        {
            var normalized = rawCode?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            if (!RegionCodePattern.IsMatch(normalized))
            {
                throw new InvalidOperationException($"Invalid region code '{rawCode}'. Use Azure region codes such as eastus or westeurope.");
            }

            if (seen.Add(normalized))
            {
                results.Add(normalized);
            }
        }

        return results;
    }
}