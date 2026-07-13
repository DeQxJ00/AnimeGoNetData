using System.Globalization;
using System.Net.Http.Headers;
using AnimeGoNetData.Core;

namespace AnimeGoNetData.Cli;

internal static class Program
{
    private const string DefaultReleaseApi = "https://api.github.com/repos/bangumi/Archive/releases/latest";

    public static Task<int> Main(string[] args) => MainAsync(args);

    public static async Task<int> MainAsync(string[] args)
    {
        try
        {
            CliOptions options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            ArchiveAsset asset;
            string zipSource;

            if (options.ZipSource is { Length: > 0 } zip)
            {
                zipSource = zip;
                asset = new ArchiveAsset(
                    Path.GetFileName(zip),
                    Uri.TryCreate(zip, UriKind.Absolute, out Uri? uri) ? uri.ToString() : Path.GetFullPath(zip),
                    File.Exists(zip) ? File.GetLastWriteTimeUtc(zip) : DateTimeOffset.UtcNow,
                    File.Exists(zip) ? new FileInfo(zip).Length : 0);
            }
            else
            {
                string releaseApi = options.ReleaseApi ?? DefaultReleaseApi;
                asset = await FetchLatestAssetAsync(releaseApi).ConfigureAwait(false);
                zipSource = asset.DownloadUrl;
                Console.WriteLine($"Selected asset: {asset.Name} ({asset.UpdatedAt:O}, {asset.Size.ToString(CultureInfo.InvariantCulture)} bytes)");
            }

            if (options.SelectAssetOnly)
            {
                Console.WriteLine(asset.DownloadUrl);
                return 0;
            }

            if (options.OutputDirectory is null)
            {
                throw new ArgumentException("Missing required --output option.");
            }

            var generationOptions = new GenerationOptions(
                options.OutputDirectory,
                zipSource,
                asset,
                options.ZipSource is null ? options.ReleaseApi ?? DefaultReleaseApi : null,
                options.ChunkSize,
                options.MinimumSubjects,
                options.MinimumEpisodes,
                options.GeneratedAt ?? DateTimeOffset.UtcNow);

            var generator = new BangumiArchiveGenerator();
            GenerationResult result = await generator.GenerateAsync(generationOptions).ConfigureAwait(false);

            Console.WriteLine($"Wrote {result.Manifest.RecordCounts["subjects"].ToString(CultureInfo.InvariantCulture)} subjects and {result.Manifest.RecordCounts["episodes"].ToString(CultureInfo.InvariantCulture)} episodes.");
            Console.WriteLine($"Skipped bad lines: subjects={result.SkippedBadSubjectLines.ToString(CultureInfo.InvariantCulture)}, episodes={result.SkippedBadEpisodeLines.ToString(CultureInfo.InvariantCulture)}.");
            Console.WriteLine($"Duplicates: subjects={result.DuplicateSubjects.ToString(CultureInfo.InvariantCulture)}, episodes={result.DuplicateEpisodes.ToString(CultureInfo.InvariantCulture)}.");
            Console.WriteLine(Path.Combine(options.OutputDirectory, "manifest.json"));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or HttpRequestException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
    }

    private static async Task<ArchiveAsset> FetchLatestAssetAsync(string releaseApi)
    {
        using var httpClient = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AnimeGoNetData", "1.0"));

        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        token ??= Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ReleaseAssetSelector.SelectLatestZip(json);
    }
}

internal sealed record CliOptions(
    string? OutputDirectory,
    string? ZipSource,
    string? ReleaseApi,
    int ChunkSize,
    int MinimumSubjects,
    int MinimumEpisodes,
    DateTimeOffset? GeneratedAt,
    bool SelectAssetOnly,
    bool ShowHelp)
{
    public const string HelpText = """
AnimeGoNetData

Usage:
  AnimeGoNetData --output <dir> [--zip <path-or-url>]
  AnimeGoNetData --output <dir> [--release-api <url>]
  AnimeGoNetData --release-api <url> --select-asset-only

Options:
  -o, --output <dir>          Output directory for manifest.json and JSONL.gz chunks.
  --zip <path-or-url>         Use a local or remote Bangumi Archive ZIP directly.
  --release-api <url>         GitHub Releases API endpoint. Defaults to bangumi/Archive latest.
  --chunk-size <n>            Records per JSONL.gz chunk. Default: 100000.
  --min-subjects <n>          Fail if fewer subjects are generated. Default: 1.
  --min-episodes <n>          Fail if fewer episodes are generated. Default: 1.
  --generated-at <iso>        Override manifest generated_at for reproducible test runs.
  --select-asset-only         Fetch Release API, select newest .zip by updated_at, print URL, then exit.
  -h, --help                  Show this help.

Environment:
  GITHUB_TOKEN or GH_TOKEN is used as a Bearer token for Release API requests.
""";

    public static CliOptions Parse(string[] args)
    {
        string? output = null;
        string? zip = null;
        string? releaseApi = null;
        int chunkSize = 100_000;
        int minimumSubjects = 1;
        int minimumEpisodes = 1;
        DateTimeOffset? generatedAt = null;
        bool selectAssetOnly = false;
        bool showHelp = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    showHelp = true;
                    break;
                case "-o":
                case "--output":
                    output = RequireValue(args, ref i, arg);
                    break;
                case "--zip":
                    zip = RequireValue(args, ref i, arg);
                    break;
                case "--release-api":
                    releaseApi = RequireValue(args, ref i, arg);
                    break;
                case "--chunk-size":
                    chunkSize = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--min-subjects":
                    minimumSubjects = ParseNonNegativeInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--min-episodes":
                    minimumEpisodes = ParseNonNegativeInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--generated-at":
                    generatedAt = DateTimeOffset.Parse(RequireValue(args, ref i, arg), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
                    break;
                case "--select-asset-only":
                    selectAssetOnly = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'. Use --help.");
            }
        }

        if (zip is not null && releaseApi is not null)
        {
            throw new ArgumentException("--zip and --release-api cannot be used together.");
        }

        return new CliOptions(output, zip, releaseApi, chunkSize, minimumSubjects, minimumEpisodes, generatedAt, selectAssetOnly, showHelp);
    }

    private static string RequireValue(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException($"Missing value for {option}.");
        }

        index++;
        return args[index];
    }

    private static int ParsePositiveInt(string value, string option)
    {
        int result = ParseNonNegativeInt(value, option);
        if (result <= 0)
        {
            throw new ArgumentException($"{option} must be greater than zero.");
        }

        return result;
    }

    private static int ParseNonNegativeInt(string value, string option)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result >= 0
            ? result
            : throw new ArgumentException($"{option} must be a non-negative integer.");
}
