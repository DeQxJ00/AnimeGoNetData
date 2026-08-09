using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnimeGoNetData.Core;

public sealed partial class BangumiArchiveGenerator
{
    public const int SchemaVersion = 1;
    public const string UpstreamRepository = "https://github.com/bangumi/Archive";
    private const string SubjectEntryName = "subject.jsonlines";
    private const string EpisodeEntryName = "episode.jsonlines";
    private const int MaximumLineBytes = 8 * 1024 * 1024;

    public async Task<GenerationResult> GenerateAsync(
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        ValidateOptions(options);
        string inputPath = Path.GetFullPath(options.ZipPath);
        if (!File.Exists(inputPath))
        {
            throw new FileNotFoundException("The Bangumi Archive ZIP does not exist.", inputPath);
        }

        string actualSha256 = await ComputeSha256Async(inputPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, options.UpstreamSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The Bangumi Archive ZIP SHA-256 does not match the declared upstream hash.");
        }

        string outputPath = Path.GetFullPath(options.OutputDirectory);
        if (Directory.Exists(outputPath) || File.Exists(outputPath))
        {
            throw new IOException("The output path must not already exist.");
        }

        string parent = Path.GetDirectoryName(outputPath)
            ?? throw new ArgumentException("The output directory has no parent.", nameof(options));
        Directory.CreateDirectory(parent);
        string staging = Path.Combine(parent, $".{Path.GetFileName(outputPath)}.partial-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            ArchiveData data = await ReadArchiveAsync(inputPath, cancellationToken).ConfigureAwait(false);
            if (data.Subjects.Count < options.MinimumSubjects)
            {
                throw new InvalidDataException($"Subject count {data.Subjects.Count.ToString(CultureInfo.InvariantCulture)} is below minimum {options.MinimumSubjects.ToString(CultureInfo.InvariantCulture)}.");
            }
            if (data.Episodes.Count < options.MinimumEpisodes)
            {
                throw new InvalidDataException($"Episode count {data.Episodes.Count.ToString(CultureInfo.InvariantCulture)} is below minimum {options.MinimumEpisodes.ToString(CultureInfo.InvariantCulture)}.");
            }

            IReadOnlyList<DataManifestAsset> assets = await WriteAssetsAsync(staging, options.AssetBaseUrl, options.SubjectsPerShard, data, cancellationToken).ConfigureAwait(false);
            var manifest = new DataManifest(
                SchemaVersion,
                options.DataVersion,
                options.GeneratedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                options.MinimumClientVersion,
                new DataManifestUpstream(
                    UpstreamRepository,
                    options.UpstreamAsset.Release,
                    options.UpstreamAsset.Name,
                    options.UpstreamSha256),
                assets,
                new DataManifestTotals(data.Subjects.Count, data.Episodes.Count));

            byte[] manifestBytes = RenderManifest(manifest);
            ValidateManifestShape(manifest);
            string manifestPath = Path.Combine(staging, "manifest.json");
            await File.WriteAllBytesAsync(manifestPath, manifestBytes, cancellationToken).ConfigureAwait(false);
            string manifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifestBytes));

            string offlinePackageName = $"animegonetdata-{options.DataVersion}-offline.zip";
            string offlinePackagePath = Path.Combine(staging, offlinePackageName);
            await WriteOfflinePackageAsync(
                offlinePackagePath,
                manifestPath,
                assets.Select(asset => Path.Combine(staging, asset.FileName)),
                options.GeneratedAtUtc,
                cancellationToken).ConfigureAwait(false);

            string checksumsPath = await WriteChecksumsAsync(staging, cancellationToken).ConfigureAwait(false);
            Directory.Move(staging, outputPath);

            return new GenerationResult(
                manifest,
                manifestSha256,
                Path.Combine(outputPath, offlinePackageName),
                Path.Combine(outputPath, Path.GetFileName(checksumsPath)));
        }
        catch
        {
            TryDeleteDirectory(staging);
            throw;
        }
    }

    private static async Task<ArchiveData> ReadArchiveAsync(string inputPath, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(inputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: false);
        ZipArchiveEntry subjectEntry = RequireSingleRootEntry(archive, SubjectEntryName);
        ZipArchiveEntry episodeEntry = RequireSingleRootEntry(archive, EpisodeEntryName);

        var subjects = new SortedDictionary<int, NormalizedSubject>();
        await ReadJsonLinesAsync(
            subjectEntry,
            line =>
            {
                using JsonDocument document = ParseLine(line, "subject");
                JsonElement root = document.RootElement;
                if (OptionalInt(root, "type") != 2)
                {
                    return;
                }

                int id = RequiredPositiveInt(root, "id", "subject");
                if (!subjects.TryAdd(
                    id,
                    new NormalizedSubject(
                        id,
                        NormalizeRequiredText(root, "name", "subject"),
                        NormalizeOptionalText(root, "name_cn"),
                        NormalizeDate(root, "date"))))
                {
                    throw new InvalidDataException("The Bangumi Archive contains a duplicate anime Subject ID.");
                }
            },
            cancellationToken).ConfigureAwait(false);

        if (subjects.Count == 0)
        {
            throw new InvalidDataException("The Bangumi Archive contains no anime Subjects.");
        }

        var episodes = new List<NormalizedEpisode>();
        var episodeIds = new HashSet<int>();
        await ReadJsonLinesAsync(
            episodeEntry,
            line =>
            {
                using JsonDocument document = ParseLine(line, "episode");
                JsonElement root = document.RootElement;
                if (OptionalInt(root, "type") != 0)
                {
                    return;
                }

                int subjectId = RequiredPositiveInt(root, "subject_id", "episode");
                if (!subjects.ContainsKey(subjectId))
                {
                    return;
                }

                int id = RequiredPositiveInt(root, "id", "episode");
                if (!episodeIds.Add(id))
                {
                    throw new InvalidDataException("The Bangumi Archive contains a duplicate normal Episode ID.");
                }

                episodes.Add(new NormalizedEpisode(
                    id,
                    subjectId,
                    RequiredPositiveDecimal(root, "sort"),
                    0,
                    NormalizeDate(root, "airdate")));
            },
            cancellationToken).ConfigureAwait(false);

        if (episodes.Count == 0)
        {
            throw new InvalidDataException("The Bangumi Archive contains no normal anime Episodes.");
        }

        episodes.Sort(static (left, right) =>
        {
            int subject = left.SubjectId.CompareTo(right.SubjectId);
            if (subject != 0)
            {
                return subject;
            }

            int episode = left.EpisodeNumber.CompareTo(right.EpisodeNumber);
            return episode != 0 ? episode : left.Id.CompareTo(right.Id);
        });

        int currentSubjectId = 0;
        int subjectSort = 0;
        for (int index = 0; index < episodes.Count; index++)
        {
            if (episodes[index].SubjectId != currentSubjectId)
            {
                currentSubjectId = episodes[index].SubjectId;
                subjectSort = 0;
            }

            episodes[index] = episodes[index] with { Sort = ++subjectSort };
        }

        Dictionary<int, int> episodeCounts = episodes
            .GroupBy(static episode => episode.SubjectId)
            .ToDictionary(static group => group.Key, static group => group.Count());

        return new ArchiveData(subjects.Values.ToArray(), episodes, episodeCounts);
    }

    private static async Task<IReadOnlyList<DataManifestAsset>> WriteAssetsAsync(
        string staging,
        Uri assetBaseUrl,
        int subjectsPerShard,
        ArchiveData data,
        CancellationToken cancellationToken)
    {
        var assets = new List<DataManifestAsset>();
        int episodeIndex = 0;
        for (int start = 0; start < data.Subjects.Count; start += subjectsPerShard)
        {
            NormalizedSubject[] subjectSlice = data.Subjects.Skip(start).Take(Math.Min(subjectsPerShard, data.Subjects.Count - start)).ToArray();
            int minimumId = subjectSlice[0].Id;
            int maximumId = subjectSlice[^1].Id;
            string fileRange = $"{minimumId:D6}-{maximumId:D6}";

            SubjectRecord[] subjectRecords = subjectSlice
                .Select(subject =>
                {
                    data.EpisodeCounts.TryGetValue(subject.Id, out int episodeCount);
                    return new SubjectRecord(
                        subject.Id,
                        subject.Name,
                        subject.ChineseName,
                        FormatDate(subject.AirDate),
                        episodeCount);
                })
                .ToArray();

            assets.Add(await WriteAssetAsync(
                staging,
                assetBaseUrl,
                "subjects",
                $"bangumi-subjects-v1-{fileRange}.jsonl.gz",
                subjectRecords,
                minimumId,
                maximumId,
                cancellationToken).ConfigureAwait(false));

            int episodeStart = episodeIndex;
            while (episodeIndex < data.Episodes.Count && data.Episodes[episodeIndex].SubjectId <= maximumId)
            {
                episodeIndex++;
            }

            int episodeCount = episodeIndex - episodeStart;
            if (episodeCount == 0)
            {
                continue;
            }

            NormalizedEpisode[] episodeSlice = data.Episodes.Skip(episodeStart).Take(episodeCount).ToArray();
            EpisodeRecord[] episodeRecords = episodeSlice
                .OrderBy(static episode => episode.Id)
                .Select(static episode => new EpisodeRecord(
                    episode.Id,
                    episode.SubjectId,
                    episode.Sort,
                    episode.EpisodeNumber.ToString("0.############################", CultureInfo.InvariantCulture),
                    FormatDate(episode.AirDate)))
                .ToArray();

            assets.Add(await WriteAssetAsync(
                staging,
                assetBaseUrl,
                "episodes",
                $"bangumi-episodes-v1-{fileRange}.jsonl.gz",
                episodeRecords,
                episodeSlice.Min(static episode => episode.SubjectId),
                episodeSlice.Max(static episode => episode.SubjectId),
                cancellationToken).ConfigureAwait(false));
        }

        return assets;
    }

    private static async Task<DataManifestAsset> WriteAssetAsync<T>(
        string staging,
        Uri assetBaseUrl,
        string kind,
        string fileName,
        IReadOnlyList<T> records,
        int subjectIdMin,
        int subjectIdMax,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(staging, fileName);
        await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
        await using (var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false))
        {
            foreach (T record in records)
            {
                if (record is SubjectRecord subject)
                {
                    await JsonSerializer.SerializeAsync(gzip, subject, AnimeGoJsonContext.Default.SubjectRecord, cancellationToken).ConfigureAwait(false);
                }
                else if (record is EpisodeRecord episode)
                {
                    await JsonSerializer.SerializeAsync(gzip, episode, AnimeGoJsonContext.Default.EpisodeRecord, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported record type {typeof(T).FullName}.");
                }

                await gzip.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }

        var info = new FileInfo(path);
        return new DataManifestAsset(
            kind,
            fileName,
            new Uri(assetBaseUrl, fileName).AbsoluteUri,
            info.Length,
            await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false),
            records.Count,
            subjectIdMin,
            subjectIdMax);
    }

    private static byte[] RenderManifest(DataManifest manifest)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", manifest.SchemaVersion);
            writer.WriteString("data_version", manifest.DataVersion);
            writer.WriteString("generated_at_utc", manifest.GeneratedAtUtc);
            writer.WriteString("minimum_client_version", manifest.MinimumClientVersion);
            writer.WriteStartObject("upstream");
            writer.WriteString("repository", manifest.Upstream.Repository);
            writer.WriteString("release", manifest.Upstream.Release);
            writer.WriteString("asset", manifest.Upstream.Asset);
            writer.WriteString("sha256", manifest.Upstream.Sha256);
            writer.WriteEndObject();
            writer.WriteStartArray("assets");
            foreach (DataManifestAsset asset in manifest.Assets)
            {
                writer.WriteStartObject();
                writer.WriteString("kind", asset.Kind);
                writer.WriteString("file_name", asset.FileName);
                writer.WriteString("url", asset.Url);
                writer.WriteNumber("size_bytes", asset.SizeBytes);
                writer.WriteString("sha256", asset.Sha256);
                writer.WriteNumber("record_count", asset.RecordCount);
                writer.WriteNumber("subject_id_min", asset.SubjectIdMin);
                writer.WriteNumber("subject_id_max", asset.SubjectIdMax);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartObject("totals");
            writer.WriteNumber("subjects", manifest.Totals.Subjects);
            writer.WriteNumber("episodes", manifest.Totals.Episodes);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        byte[] bytes = new byte[buffer.WrittenCount + 1];
        buffer.WrittenSpan.CopyTo(bytes);
        bytes[^1] = (byte)'\n';
        return bytes;
    }

    private static async Task WriteOfflinePackageAsync(
        string packagePath,
        string manifestPath,
        IEnumerable<string> assetPaths,
        DateTimeOffset generatedAtUtc,
        CancellationToken cancellationToken)
    {
        await using var output = new FileStream(packagePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
        foreach (string path in new[] { manifestPath }.Concat(assetPaths.OrderBy(Path.GetFileName, StringComparer.Ordinal)))
        {
            var entry = archive.CreateEntry(Path.GetFileName(path), CompressionLevel.NoCompression);
            entry.LastWriteTime = generatedAtUtc;
            await using Stream target = entry.Open();
            await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<string> WriteChecksumsAsync(string staging, CancellationToken cancellationToken)
    {
        const string checksumName = "SHA256SUMS";
        string checksumPath = Path.Combine(staging, checksumName);
        string[] files = Directory.EnumerateFiles(staging, "*", SearchOption.TopDirectoryOnly)
            .Where(path => !string.Equals(Path.GetFileName(path), checksumName, StringComparison.Ordinal))
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();

        var builder = new StringBuilder(files.Length * 96);
        foreach (string file in files)
        {
            builder
                .Append(await ComputeSha256Async(file, cancellationToken).ConfigureAwait(false))
                .Append("  ")
                .Append(Path.GetFileName(file))
                .Append('\n');
        }

        await File.WriteAllTextAsync(checksumPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken).ConfigureAwait(false);
        return checksumPath;
    }

    private static async Task ReadJsonLinesAsync(
        ZipArchiveEntry entry,
        Action<ReadOnlyMemory<byte>> consume,
        CancellationToken cancellationToken)
    {
        await using Stream stream = entry.Open();
        byte[] readBuffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        var line = new ArrayBufferWriter<byte>();
        try
        {
            while (true)
            {
                int read = await stream.ReadAsync(readBuffer.AsMemory(0, readBuffer.Length), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                for (int index = 0; index < read; index++)
                {
                    byte value = readBuffer[index];
                    if (value == (byte)'\n')
                    {
                        ConsumeLine(line, consume);
                        continue;
                    }

                    if (line.WrittenCount >= MaximumLineBytes)
                    {
                        throw new InvalidDataException("A Bangumi Archive JSONL record is too large.");
                    }

                    line.GetSpan(1)[0] = value;
                    line.Advance(1);
                }
            }

            if (line.WrittenCount > 0)
            {
                ConsumeLine(line, consume);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(readBuffer);
        }
    }

    private static void ConsumeLine(ArrayBufferWriter<byte> line, Action<ReadOnlyMemory<byte>> consume)
    {
        int length = line.WrittenCount;
        if (length > 0 && line.WrittenSpan[length - 1] == (byte)'\r')
        {
            length--;
        }

        if (length > 0)
        {
            consume(line.WrittenMemory[..length]);
        }

        line.Clear();
    }

    private static ZipArchiveEntry RequireSingleRootEntry(ZipArchive archive, string name)
    {
        ZipArchiveEntry[] matches = archive.Entries
            .Where(entry => string.Equals(entry.FullName, name, StringComparison.Ordinal))
            .ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new InvalidDataException($"The Bangumi Archive ZIP must contain exactly one root {name} entry.");
    }

    private static JsonDocument ParseLine(ReadOnlyMemory<byte> line, string kind)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(line, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"A Bangumi Archive {kind} record is invalid JSON.", exception);
        }

        throw new InvalidDataException($"A Bangumi Archive {kind} record must be an object.");
    }

    private static int OptionalInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : -1;

    private static int RequiredPositiveInt(JsonElement root, string name, string kind)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) && result > 0
            ? result
            : throw new InvalidDataException($"A Bangumi Archive {kind} ID is invalid.");

    private static decimal RequiredPositiveDecimal(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetDecimal(out decimal result) && result > 0
            ? result
            : throw new InvalidDataException("A Bangumi Archive normal Episode number is invalid.");

    private static string NormalizeRequiredText(JsonElement root, string name, string kind)
        => NormalizeOptionalText(root, name) ?? throw new InvalidDataException($"A Bangumi Archive {kind} name is empty.");

    private static string? NormalizeOptionalText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("A Bangumi Archive title has an invalid type.");
        }

        string text = value.GetString()!;
        string normalized = new(text.Select(static character => char.IsControl(character) ? ' ' : character).ToArray());
        normalized = normalized.Trim();
        if (normalized.Length == 0)
        {
            return null;
        }

        return normalized.Length <= 1024 ? normalized : normalized[..1024].TrimEnd();
    }

    private static DateOnly? NormalizeDate(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date)
            ? date
            : null;
    }

    private static string? FormatDate(DateOnly? value)
        => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void ValidateOptions(GenerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!StableVersion().IsMatch(options.DataVersion))
        {
            throw new ArgumentException("The data version is invalid.", nameof(options));
        }

        if (!LowerSha256().IsMatch(options.UpstreamSha256))
        {
            throw new ArgumentException("The upstream SHA-256 is invalid.", nameof(options));
        }

        if (!Version.TryParse(options.MinimumClientVersion, out _))
        {
            throw new ArgumentException("The minimum client version is invalid.", nameof(options));
        }

        if (options.GeneratedAtUtc.Offset != TimeSpan.Zero || options.GeneratedAtUtc.Year is < 1980 or > 2107)
        {
            throw new ArgumentException("The generated timestamp must be UTC and ZIP-compatible.", nameof(options));
        }

        if (options.SubjectsPerShard is < 1 or > 1_000_000)
        {
            throw new ArgumentException("Subjects per shard must be between 1 and 1000000.", nameof(options));
        }

        if (options.MinimumSubjects is < 1 or > 10_000_000)
        {
            throw new ArgumentException("Minimum Subject count must be between 1 and 10000000.", nameof(options));
        }

        if (options.MinimumEpisodes is < 1 or > 100_000_000)
        {
            throw new ArgumentException("Minimum Episode count must be between 1 and 100000000.", nameof(options));
        }

        if (!IsSafeHttpBaseUrl(options.AssetBaseUrl))
        {
            throw new ArgumentException("The asset base URL is invalid.", nameof(options));
        }
    }

    private static void ValidateManifestShape(DataManifest manifest)
    {
        long subjects = manifest.Assets.Where(static asset => asset.Kind == "subjects").Sum(static asset => asset.RecordCount);
        long episodes = manifest.Assets.Where(static asset => asset.Kind == "episodes").Sum(static asset => asset.RecordCount);
        if (subjects != manifest.Totals.Subjects || episodes != manifest.Totals.Episodes || subjects <= 0 || episodes <= 0)
        {
            throw new InvalidDataException("The generated manifest totals do not match asset counts.");
        }

        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (DataManifestAsset asset in manifest.Assets)
        {
            if (!names.Add(asset.FileName)
                || asset.FileName != Path.GetFileName(asset.FileName)
                || !asset.FileName.EndsWith(".jsonl.gz", StringComparison.Ordinal)
                || !LowerSha256().IsMatch(asset.Sha256)
                || asset.SizeBytes <= 0
                || asset.RecordCount <= 0
                || asset.SubjectIdMin <= 0
                || asset.SubjectIdMax < asset.SubjectIdMin)
            {
                throw new InvalidDataException("The generated manifest asset set is invalid.");
            }
        }
    }

    private static bool IsSafeHttpBaseUrl(Uri value)
        => value.IsAbsoluteUri
            && value.Scheme is "http" or "https"
            && string.IsNullOrEmpty(value.UserInfo)
            && string.IsNullOrEmpty(value.Query)
            && string.IsNullOrEmpty(value.Fragment)
            && value.AbsolutePath.EndsWith('/');

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex StableVersion();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerSha256();

    private sealed record NormalizedSubject(int Id, string Name, string? ChineseName, DateOnly? AirDate);

    private sealed record NormalizedEpisode(int Id, int SubjectId, decimal EpisodeNumber, int Sort, DateOnly? AirDate);

    private sealed record ArchiveData(
        IReadOnlyList<NormalizedSubject> Subjects,
        IReadOnlyList<NormalizedEpisode> Episodes,
        IReadOnlyDictionary<int, int> EpisodeCounts);
}
