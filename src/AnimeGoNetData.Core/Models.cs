using System.Text.Json.Serialization;

namespace AnimeGoNetData.Core;

public sealed record ArchiveAsset(
    string Name,
    string DownloadUrl,
    DateTimeOffset UpdatedAt,
    long Size);

public sealed record SubjectRecord(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name_cn")] string NameCn,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("eps")] int Eps,
    [property: JsonPropertyName("airdate")] string AirDate,
    [property: JsonPropertyName("type")] int Type);

public sealed record EpisodeRecord(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("subject_id")] int SubjectId,
    [property: JsonPropertyName("sort")] double Sort,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("name_cn")] string NameCn,
    [property: JsonPropertyName("type")] int Type,
    [property: JsonPropertyName("airdate")] string AirDate);

public sealed record UpstreamManifest(
    [property: JsonPropertyName("release_api")] string? ReleaseApi,
    [property: JsonPropertyName("asset_name")] string AssetName,
    [property: JsonPropertyName("asset_url")] string AssetUrl,
    [property: JsonPropertyName("asset_updated_at")] DateTimeOffset AssetUpdatedAt,
    [property: JsonPropertyName("asset_size")] long AssetSize);

public sealed record ManifestFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("records")] int Records,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record DatasetManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("dataset_version")] string DatasetVersion,
    [property: JsonPropertyName("generated_at")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("upstream")] UpstreamManifest Upstream,
    [property: JsonPropertyName("record_counts")] Dictionary<string, int> RecordCounts,
    [property: JsonPropertyName("files")] IReadOnlyList<ManifestFile> Files);

public sealed record GenerationOptions(
    string OutputDirectory,
    string ZipSource,
    ArchiveAsset UpstreamAsset,
    string? ReleaseApi,
    int ChunkSize,
    int MinimumSubjects,
    int MinimumEpisodes,
    DateTimeOffset GeneratedAt);

public sealed record GenerationResult(
    DatasetManifest Manifest,
    int SkippedBadSubjectLines,
    int SkippedBadEpisodeLines,
    int DuplicateSubjects,
    int DuplicateEpisodes);
