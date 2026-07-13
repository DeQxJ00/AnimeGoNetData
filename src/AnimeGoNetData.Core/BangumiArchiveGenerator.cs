using System.Buffers;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AnimeGoNetData.Core;

public sealed class BangumiArchiveGenerator
{
    public const int SchemaVersion = 1;
    private const string SubjectEntryName = "subject.jsonlines";
    private const string EpisodeEntryName = "episode.jsonlines";

    public async Task<GenerationResult> GenerateAsync(
        GenerationOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ChunkSize, 1);

        await using Stream zipStream = await OpenZipSourceAsync(options.ZipSource, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: false);

        ZipArchiveEntry subjectEntry = archive.GetEntry(SubjectEntryName)
            ?? throw new FileNotFoundException($"ZIP entry '{SubjectEntryName}' was not found.");
        ZipArchiveEntry episodeEntry = archive.GetEntry(EpisodeEntryName)
            ?? throw new FileNotFoundException($"ZIP entry '{EpisodeEntryName}' was not found.");

        Dictionary<int, ParsedEpisode> episodes = new();
        int skippedBadEpisodeLines = await ReadEpisodesAsync(episodeEntry, episodes, cancellationToken).ConfigureAwait(false);

        Dictionary<int, ParsedSubject> subjects = new();
        int skippedBadSubjectLines = await ReadSubjectsAsync(subjectEntry, subjects, cancellationToken).ConfigureAwait(false);

        int duplicateSubjects = subjects.Values.Sum(static item => item.Duplicates);
        int duplicateEpisodes = episodes.Values.Sum(static item => item.Duplicates);

        EpisodeRecord[] episodeRecords = episodes.Values
            .Select(static item => item.Record)
            .OrderBy(static item => item.SubjectId)
            .ThenBy(static item => item.Sort)
            .ThenBy(static item => item.Id)
            .ToArray();

        Dictionary<int, List<EpisodeRecord>> episodesBySubject = episodeRecords
            .GroupBy(static item => item.SubjectId)
            .ToDictionary(static group => group.Key, static group => group.ToList());

        SubjectRecord[] subjectRecords = subjects.Values
            .Select(item =>
            {
                SubjectRecord record = item.Record;
                if (!episodesBySubject.TryGetValue(record.Id, out List<EpisodeRecord>? subjectEpisodes))
                {
                    return record;
                }

                string airdate = subjectEpisodes.FirstOrDefault(static ep => ep.AirDate.Length > 0)?.AirDate ?? string.Empty;
                return record with { Eps = subjectEpisodes.Count, AirDate = airdate };
            })
            .OrderBy(static item => item.Id)
            .ToArray();

        if (subjectRecords.Length < options.MinimumSubjects)
        {
            throw new InvalidOperationException(
                $"Subject count {subjectRecords.Length.ToString(CultureInfo.InvariantCulture)} is below minimum {options.MinimumSubjects.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (episodeRecords.Length < options.MinimumEpisodes)
        {
            throw new InvalidOperationException(
                $"Episode count {episodeRecords.Length.ToString(CultureInfo.InvariantCulture)} is below minimum {options.MinimumEpisodes.ToString(CultureInfo.InvariantCulture)}.");
        }

        Directory.CreateDirectory(options.OutputDirectory);
        string staging = Path.Combine(options.OutputDirectory, $".staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);

        try
        {
            List<ManifestFile> files = new();
            files.AddRange(await WriteChunksAsync(staging, "bangumi-subjects-v1", "subjects", subjectRecords, options.ChunkSize, cancellationToken).ConfigureAwait(false));
            files.AddRange(await WriteChunksAsync(staging, "bangumi-episodes-v1", "episodes", episodeRecords, options.ChunkSize, cancellationToken).ConfigureAwait(false));

            var manifest = new DatasetManifest(
                SchemaVersion,
                options.UpstreamAsset.Name,
                options.GeneratedAt,
                new UpstreamManifest(
                    options.ReleaseApi,
                    options.UpstreamAsset.Name,
                    options.UpstreamAsset.DownloadUrl,
                    options.UpstreamAsset.UpdatedAt,
                    options.UpstreamAsset.Size),
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["subjects"] = subjectRecords.Length,
                    ["episodes"] = episodeRecords.Length
                },
                files);

            await WriteManifestLastAsync(staging, manifest, cancellationToken).ConfigureAwait(false);
            PublishStaging(options.OutputDirectory, staging);

            return new GenerationResult(manifest, skippedBadSubjectLines, skippedBadEpisodeLines, duplicateSubjects, duplicateEpisodes);
        }
        catch
        {
            Directory.Delete(staging, recursive: true);
            throw;
        }
    }

    private static async Task<Stream> OpenZipSourceAsync(string source, CancellationToken cancellationToken)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out Uri? uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            var httpClient = new HttpClient();
            HttpResponseMessage response = await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }

        return File.OpenRead(source);
    }

    private static async Task<int> ReadSubjectsAsync(
        ZipArchiveEntry entry,
        Dictionary<int, ParsedSubject> subjects,
        CancellationToken cancellationToken)
    {
        int badLines = 0;
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.AsSpan().Trim().IsEmpty)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (GetInt(root, "type") != 2)
                {
                    continue;
                }

                var record = new SubjectRecord(
                    GetRequiredInt(root, "id"),
                    GetString(root, "name_cn"),
                    GetString(root, "name"),
                    0,
                    string.Empty,
                    2);

                Upsert(subjects, record.Id, new ParsedSubject(record, CanonicalJson(record), 0));
            }
            catch (JsonException)
            {
                badLines++;
            }
            catch (InvalidOperationException)
            {
                badLines++;
            }
        }

        return badLines;
    }

    private static async Task<int> ReadEpisodesAsync(
        ZipArchiveEntry entry,
        Dictionary<int, ParsedEpisode> episodes,
        CancellationToken cancellationToken)
    {
        int badLines = 0;
        using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (line.AsSpan().Trim().IsEmpty)
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(line);
                JsonElement root = document.RootElement;
                if (GetInt(root, "type") != 0)
                {
                    continue;
                }

                var record = new EpisodeRecord(
                    GetRequiredInt(root, "id"),
                    GetRequiredInt(root, "subject_id"),
                    GetDouble(root, "sort"),
                    GetString(root, "name"),
                    GetString(root, "name_cn"),
                    0,
                    GetString(root, "airdate"));

                Upsert(episodes, record.Id, new ParsedEpisode(record, CanonicalJson(record), 0));
            }
            catch (JsonException)
            {
                badLines++;
            }
            catch (InvalidOperationException)
            {
                badLines++;
            }
        }

        return badLines;
    }

    private static void Upsert<T>(Dictionary<int, T> records, int id, T candidate)
        where T : IParsedRecord<T>
    {
        if (!records.TryGetValue(id, out T? existing))
        {
            records[id] = candidate;
            return;
        }

        T selected = string.CompareOrdinal(candidate.CanonicalJson, existing.CanonicalJson) < 0
            ? candidate.WithDuplicates(existing.Duplicates + 1)
            : existing.WithDuplicates(existing.Duplicates + 1);
        records[id] = selected;
    }

    private static string CanonicalJson(SubjectRecord record)
        => JsonSerializer.Serialize(record, AnimeGoJsonContext.Default.SubjectRecord);

    private static string CanonicalJson(EpisodeRecord record)
        => JsonSerializer.Serialize(record, AnimeGoJsonContext.Default.EpisodeRecord);

    private static async Task<List<ManifestFile>> WriteChunksAsync<T>(
        string stagingDirectory,
        string prefix,
        string kind,
        IReadOnlyList<T> records,
        int chunkSize,
        CancellationToken cancellationToken)
    {
        List<ManifestFile> files = new();
        for (int offset = 0, chunkIndex = 0; offset < records.Count; offset += chunkSize, chunkIndex++)
        {
            string fileName = $"{prefix}-{chunkIndex:00000}.jsonl.gz";
            string path = Path.Combine(stagingDirectory, fileName);
            int count = Math.Min(chunkSize, records.Count - offset);
            await WriteGzipJsonLinesAsync(path, records, offset, count, cancellationToken).ConfigureAwait(false);
            files.Add(new ManifestFile(fileName, kind, count, new FileInfo(path).Length, await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false)));
        }

        return files;
    }

    private static async Task WriteGzipJsonLinesAsync<T>(
        string path,
        IReadOnlyList<T> records,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        await using FileStream file = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await using var gzip = new GZipStream(file, CompressionLevel.SmallestSize, leaveOpen: false);

        for (int i = 0; i < count; i++)
        {
            T record = records[offset + i];
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

    private static async Task WriteManifestLastAsync(
        string stagingDirectory,
        DatasetManifest manifest,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(stagingDirectory, "manifest.json");
        await using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, AnimeGoJsonContext.Default.DatasetManifest, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private static void PublishStaging(string outputDirectory, string stagingDirectory)
    {
        foreach (string path in Directory.EnumerateFiles(outputDirectory, "bangumi-*-v1-*.jsonl.gz"))
        {
            File.Delete(path);
        }

        string manifestPath = Path.Combine(outputDirectory, "manifest.json");
        if (File.Exists(manifestPath))
        {
            File.Delete(manifestPath);
        }

        foreach (string file in Directory.EnumerateFiles(stagingDirectory))
        {
            string destination = Path.Combine(outputDirectory, Path.GetFileName(file));
            File.Move(file, destination);
        }

        Directory.Delete(stagingDirectory);
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static int GetRequiredInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result)
            ? result
            : throw new InvalidOperationException($"Missing or invalid integer '{name}'.");

    private static int GetInt(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

    private static double GetDouble(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : 0;

    private static string GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private interface IParsedRecord<TSelf>
    {
        string CanonicalJson { get; }

        int Duplicates { get; }

        TSelf WithDuplicates(int duplicates);
    }

    private sealed record ParsedSubject(SubjectRecord Record, string CanonicalJson, int Duplicates)
        : IParsedRecord<ParsedSubject>
    {
        public ParsedSubject WithDuplicates(int duplicates) => this with { Duplicates = duplicates };
    }

    private sealed record ParsedEpisode(EpisodeRecord Record, string CanonicalJson, int Duplicates)
        : IParsedRecord<ParsedEpisode>
    {
        public ParsedEpisode WithDuplicates(int duplicates) => this with { Duplicates = duplicates };
    }
}
