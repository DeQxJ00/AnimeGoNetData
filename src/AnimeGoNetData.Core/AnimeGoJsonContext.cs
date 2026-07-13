using System.Text.Json.Serialization;

namespace AnimeGoNetData.Core;

[JsonSerializable(typeof(SubjectRecord))]
[JsonSerializable(typeof(EpisodeRecord))]
[JsonSerializable(typeof(DatasetManifest))]
[JsonSerializable(typeof(GitHubReleaseResponse))]
[JsonSerializable(typeof(List<GitHubAssetResponse>))]
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
internal sealed partial class AnimeGoJsonContext : JsonSerializerContext;
