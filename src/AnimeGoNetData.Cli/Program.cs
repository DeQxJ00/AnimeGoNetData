using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AnimeGoNetData.Core;

namespace AnimeGoNetData.Cli;

internal static class Program
{
    private const string DefaultReleaseApi = "https://api.github.com/repos/bangumi/Archive/releases/latest";

    public static Task<int> Main(string[] args) => MainAsync(args);

    public static async Task<int> MainAsync(string[] args)
    {
        string? temporaryZip = null;
        try
        {
            CliOptions options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                Console.WriteLine(CliOptions.HelpText);
                return 0;
            }

            ArchiveAsset asset;
            string zipPath;
            string upstreamSha256;
            DateTimeOffset generatedAtUtc;
            string dataVersion;

            if (options.ZipSource is { Length: > 0 } zip)
            {
                zipPath = Path.GetFullPath(zip);
                if (!File.Exists(zipPath))
                {
                    throw new FileNotFoundException("The ZIP file does not exist.", zipPath);
                }

                upstreamSha256 = options.UpstreamSha256 ?? await Sha256FileAsync(zipPath).ConfigureAwait(false);
                generatedAtUtc = NormalizeUtc(options.GeneratedAtUtc ?? DateTimeOffset.UtcNow);
                dataVersion = options.DataVersion ?? ToDataVersion(generatedAtUtc);
                asset = new ArchiveAsset(Path.GetFileName(zipPath), zipPath, generatedAtUtc, new FileInfo(zipPath).Length, options.UpstreamRelease ?? "local");
            }
            else
            {
                string releaseApi = options.ReleaseApi ?? DefaultReleaseApi;
                asset = await FetchLatestAssetAsync(releaseApi).ConfigureAwait(false);
                Console.WriteLine($"Selected asset: {asset.Name} ({asset.UpdatedAt:O}, {asset.Size.ToString(CultureInfo.InvariantCulture)} bytes)");

                if (options.SelectAssetOnly)
                {
                    Console.WriteLine(asset.DownloadUrl);
                    return 0;
                }

                temporaryZip = Path.Combine(Path.GetTempPath(), $"animegonetdata-{Guid.NewGuid():N}-{asset.Name}");
                await DownloadFileAsync(asset.DownloadUrl, temporaryZip).ConfigureAwait(false);
                zipPath = temporaryZip;
                upstreamSha256 = await Sha256FileAsync(zipPath).ConfigureAwait(false);
                generatedAtUtc = NormalizeUtc(options.GeneratedAtUtc ?? asset.UpdatedAt);
                dataVersion = options.DataVersion ?? ToDataVersion(asset.UpdatedAt);
            }

            if (options.SelectAssetOnly)
            {
                Console.WriteLine(zipPath);
                return 0;
            }

            if (options.OutputDirectory is null)
            {
                throw new ArgumentException("Missing required --output option.");
            }
            if (options.AssetBaseUrl is null)
            {
                throw new ArgumentException("Missing required --asset-base-url option.");
            }

            var generationOptions = new GenerationOptions(
                options.OutputDirectory,
                zipPath,
                asset,
                upstreamSha256,
                dataVersion,
                new Uri(options.AssetBaseUrl, UriKind.Absolute),
                options.MinimumClientVersion,
                options.SubjectsPerShard,
                options.MinimumSubjects,
                options.MinimumEpisodes,
                generatedAtUtc);

            var generator = new BangumiArchiveGenerator();
            GenerationResult result = await generator.GenerateAsync(generationOptions).ConfigureAwait(false);

            Console.WriteLine($"Built {result.Manifest.DataVersion}: {result.Manifest.Totals.Subjects.ToString(CultureInfo.InvariantCulture)} subjects, {result.Manifest.Totals.Episodes.ToString(CultureInfo.InvariantCulture)} episodes, {result.Manifest.Assets.Count.ToString(CultureInfo.InvariantCulture)} assets.");
            Console.WriteLine($"Manifest SHA-256: {result.ManifestSha256}");
            Console.WriteLine(Path.Combine(options.OutputDirectory, "manifest.json"));
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException or HttpRequestException)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        finally
        {
            if (temporaryZip is not null)
            {
                TryDeleteFile(temporaryZip);
            }
        }
    }

    private static async Task<ArchiveAsset> FetchLatestAssetAsync(string releaseApi)
    {
        using var httpClient = CreateHttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, releaseApi);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        return ReleaseAssetSelector.SelectLatestZip(json);
    }

    private static async Task DownloadFileAsync(string url, string path)
    {
        using var httpClient = CreateHttpClient();
        using HttpResponseMessage response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await input.CopyToAsync(output).ConfigureAwait(false);
    }

    private static HttpClient CreateHttpClient()
    {
        var httpClient = new HttpClient();
        httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AnimeGoNetData", "1.0"));
        string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");
        token ??= Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return httpClient;
    }

    private static async Task<string> Sha256FileAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static string ToDataVersion(DateTimeOffset value)
        => NormalizeUtc(value).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture) + ".1";

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.ToUniversalTime();

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}

internal sealed record CliOptions(
    string? OutputDirectory,
    string? ZipSource,
    string? ReleaseApi,
    string? AssetBaseUrl,
    string? DataVersion,
    string? UpstreamRelease,
    string? UpstreamSha256,
    string MinimumClientVersion,
    int SubjectsPerShard,
    int MinimumSubjects,
    int MinimumEpisodes,
    DateTimeOffset? GeneratedAtUtc,
    bool SelectAssetOnly,
    bool ShowHelp)
{
    public const string HelpText = """
AnimeGoNetData

Usage:
  AnimeGoNetData --output <dir> --asset-base-url <url> [--zip <path>]
  AnimeGoNetData --output <dir> --asset-base-url <url> [--release-api <url>]
  AnimeGoNetData --release-api <url> --select-asset-only

Options:
  -o, --output <dir>             New output directory. It must not already exist.
  --asset-base-url <url>         Immutable Release asset base URL ending with '/'.
  --zip <path>                   Use a local Bangumi Archive ZIP.
  --release-api <url>            GitHub Releases API endpoint. Defaults to bangumi/Archive latest.
  --data-version <version>       Stable DATA_MANIFEST_V1 data_version. Defaults to yyyy.MM.dd.1.
  --upstream-release <name>      Upstream release name for local ZIP mode. Default: local.
  --upstream-sha256 <sha256>     Expected ZIP SHA-256. If omitted, local ZIP hash is computed.
  --minimum-client-version <v>   DATA_MANIFEST_V1 minimum_client_version. Default: 0.1.0.
  --subjects-per-shard <n>       Anime subjects per shard. Default: 25000.
  --chunk-size <n>               Alias for --subjects-per-shard.
  --min-subjects <n>             Fail if fewer subjects are generated. Default: 1.
  --min-episodes <n>             Fail if fewer episodes are generated. Default: 1.
  --generated-at-utc <iso>       Override generated_at_utc; must be UTC round-trip O format.
  --generated-at <iso>           Alias for --generated-at-utc.
  --select-asset-only            Fetch Release API, select newest .zip by updated_at, print URL, then exit.
  -h, --help                     Show this help.

Environment:
  GITHUB_TOKEN or GH_TOKEN is used as a Bearer token for Release API and asset downloads.
""";

    public static CliOptions Parse(string[] args)
    {
        string? output = null;
        string? zip = null;
        string? releaseApi = null;
        string? assetBaseUrl = null;
        string? dataVersion = null;
        string? upstreamRelease = null;
        string? upstreamSha256 = null;
        string minimumClientVersion = "0.1.0";
        int subjectsPerShard = 25_000;
        int minimumSubjects = 1;
        int minimumEpisodes = 1;
        DateTimeOffset? generatedAtUtc = null;
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
                case "--asset-base-url":
                    assetBaseUrl = RequireValue(args, ref i, arg);
                    break;
                case "--data-version":
                    dataVersion = RequireValue(args, ref i, arg);
                    break;
                case "--upstream-release":
                    upstreamRelease = RequireValue(args, ref i, arg);
                    break;
                case "--upstream-sha256":
                    upstreamSha256 = RequireValue(args, ref i, arg);
                    break;
                case "--minimum-client-version":
                    minimumClientVersion = RequireValue(args, ref i, arg);
                    break;
                case "--subjects-per-shard":
                case "--chunk-size":
                    subjectsPerShard = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--min-subjects":
                    minimumSubjects = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--min-episodes":
                    minimumEpisodes = ParsePositiveInt(RequireValue(args, ref i, arg), arg);
                    break;
                case "--generated-at-utc":
                case "--generated-at":
                    generatedAtUtc = DateTimeOffset.ParseExact(RequireValue(args, ref i, arg), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
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

        return new CliOptions(output, zip, releaseApi, assetBaseUrl, dataVersion, upstreamRelease, upstreamSha256, minimumClientVersion, subjectsPerShard, minimumSubjects, minimumEpisodes, generatedAtUtc, selectAssetOnly, showHelp);
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
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int result) && result > 0
            ? result
            : throw new ArgumentException($"{option} must be a positive integer.");
}
