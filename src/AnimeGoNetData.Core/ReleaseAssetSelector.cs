using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnimeGoNetData.Core;

public static class ReleaseAssetSelector
{
    public static ArchiveAsset SelectLatestZip(string releaseJson)
    {
        GitHubReleaseResponse? release = JsonSerializer.Deserialize(
            releaseJson,
            AnimeGoJsonContext.Default.GitHubReleaseResponse);

        if (release?.Assets is null || release.Assets.Count == 0)
        {
            throw new InvalidOperationException("Release API response does not contain assets.");
        }

        GitHubAssetResponse? selected = release.Assets
            .Where(asset => asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(asset => asset.UpdatedAt)
            .ThenBy(asset => asset.Name, StringComparer.Ordinal)
            .FirstOrDefault();

        if (selected is null)
        {
            throw new InvalidOperationException("Release API response does not contain a .zip asset.");
        }

        return new ArchiveAsset(selected.Name, selected.BrowserDownloadUrl, selected.UpdatedAt, selected.Size, release.TagName);
    }
}

internal sealed record GitHubReleaseResponse(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("assets")] List<GitHubAssetResponse> Assets);

internal sealed record GitHubAssetResponse(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt,
    [property: JsonPropertyName("size")] long Size);
