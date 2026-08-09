using System.Text.Json.Serialization;

namespace AnimeGoNetData.Core;

public sealed record ArchiveAsset(
    string Name,
    string DownloadUrl,
    DateTimeOffset UpdatedAt,
    long Size,
    string Release);

public sealed record SubjectRecord(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("name_cn")] string? NameCn,
    [property: JsonPropertyName("air_date")] string? AirDate,
    [property: JsonPropertyName("episode_count")] int EpisodeCount);

public sealed record EpisodeRecord(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("subject_id")] int SubjectId,
    [property: JsonPropertyName("sort")] int Sort,
    [property: JsonPropertyName("episode")] string Episode,
    [property: JsonPropertyName("air_date")] string? AirDate);

public sealed record RelationRecord(
    [property: JsonPropertyName("subject_id")] int SubjectId,
    [property: JsonPropertyName("related_subject_id")] int RelatedSubjectId,
    [property: JsonPropertyName("relation_type")] int RelationType,
    [property: JsonPropertyName("order")] int Order);

public sealed record DataManifest(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("data_version")] string DataVersion,
    [property: JsonPropertyName("generated_at_utc")] string GeneratedAtUtc,
    [property: JsonPropertyName("minimum_client_version")] string MinimumClientVersion,
    [property: JsonPropertyName("upstream")] DataManifestUpstream Upstream,
    [property: JsonPropertyName("assets")] IReadOnlyList<DataManifestAsset> Assets,
    [property: JsonPropertyName("totals")] DataManifestTotals Totals);

public sealed record DataManifestUpstream(
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("release")] string Release,
    [property: JsonPropertyName("asset")] string Asset,
    [property: JsonPropertyName("sha256")] string Sha256);

public sealed record DataManifestAsset(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("file_name")] string FileName,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("record_count")] long RecordCount,
    [property: JsonPropertyName("subject_id_min")] int SubjectIdMin,
    [property: JsonPropertyName("subject_id_max")] int SubjectIdMax);

public sealed record DataManifestTotals(
    [property: JsonPropertyName("subjects")] long Subjects,
    [property: JsonPropertyName("episodes")] long Episodes,
    [property: JsonPropertyName("relations")] long Relations);

public sealed record GenerationOptions(
    string OutputDirectory,
    string ZipPath,
    ArchiveAsset UpstreamAsset,
    string UpstreamSha256,
    string DataVersion,
    Uri AssetBaseUrl,
    string MinimumClientVersion,
    int SubjectsPerShard,
    int MinimumSubjects,
    int MinimumEpisodes,
    DateTimeOffset GeneratedAtUtc,
    int MinimumRelations = 1);

public sealed record GenerationResult(
    DataManifest Manifest,
    string ManifestSha256,
    string OfflinePackagePath,
    string ChecksumsPath);
