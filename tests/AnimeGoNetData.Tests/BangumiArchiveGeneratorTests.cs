using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeGoNetData.Core;

namespace AnimeGoNetData.Tests;

public sealed class BangumiArchiveGeneratorTests
{
    [Fact]
    public async Task Generate_EmitsDataManifestV1AssetsHashesAndStrictOfflineZip()
    {
        string root = NewTempDirectory();
        string zip = FixtureZip.Create(
            root,
            [
                """{"id":1,"type":2,"name":" A\u0001 ","name_cn":"甲","date":"2026-01-01"}""",
                """{"id":2,"type":2,"name":"B","name_cn":"","date":"bad"}""",
                """{"id":3,"type":1,"name":"Book","name_cn":"书"}"""
            ],
            [
                """{"id":12,"subject_id":1,"sort":2.5,"type":0,"airdate":"2026-01-08"}""",
                """{"id":11,"subject_id":1,"sort":1,"type":0,"airdate":"2026-01-01"}""",
                """{"id":21,"subject_id":2,"sort":1,"type":0,"airdate":"bad"}""",
                """{"id":31,"subject_id":3,"sort":1,"type":0,"airdate":"2026-01-01"}""",
                """{"id":13,"subject_id":1,"sort":3,"type":1,"airdate":"2026-01-15"}"""
            ]);
        string output = Path.Combine(root, "out");
        string upstreamSha256 = Sha256(zip);

        GenerationResult result = await Generate(zip, output, upstreamSha256, subjectsPerShard: 1);

        Assert.Equal("2026.01.02.1", result.Manifest.DataVersion);
        Assert.Equal("2026-01-02T03:04:05.0000000+00:00", result.Manifest.GeneratedAtUtc);
        Assert.Equal("0.1.0", result.Manifest.MinimumClientVersion);
        Assert.Equal(upstreamSha256, result.Manifest.Upstream.Sha256);
        Assert.Equal(2, result.Manifest.Totals.Subjects);
        Assert.Equal(3, result.Manifest.Totals.Episodes);
        Assert.Equal(4, result.Manifest.Assets.Count);

        string manifestPath = Path.Combine(output, "manifest.json");
        EquivalentManifestParser.Parse(await File.ReadAllBytesAsync(manifestPath));

        HashSet<string> expectedReleaseNames = result.Manifest.Assets.Select(static asset => asset.FileName).ToHashSet(StringComparer.Ordinal);
        expectedReleaseNames.Add("manifest.json");
        expectedReleaseNames.Add("SHA256SUMS");
        expectedReleaseNames.Add("animegonetdata-2026.01.02.1-offline.zip");
        Assert.Equal(expectedReleaseNames.Order(StringComparer.Ordinal), Directory.GetFiles(output).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        foreach (DataManifestAsset asset in result.Manifest.Assets)
        {
            string path = Path.Combine(output, asset.FileName);
            Assert.Equal(new FileInfo(path).Length, asset.SizeBytes);
            Assert.Equal(Sha256(path), asset.Sha256);
            Assert.StartsWith("https://github.com/example/AnimeGoNetData/releases/download/2026.01.02.1/", asset.Url, StringComparison.Ordinal);
        }

        string firstSubjectLine = ReadGzipLines(Path.Combine(output, "bangumi-subjects-v1-000001-000001.jsonl.gz")).Single();
        Assert.Equal("""{"id":1,"name":"A","name_cn":"\u7532","air_date":"2026-01-01","episode_count":2}""", firstSubjectLine);

        string secondSubjectLine = ReadGzipLines(Path.Combine(output, "bangumi-subjects-v1-000002-000002.jsonl.gz")).Single();
        Assert.Equal("""{"id":2,"name":"B","name_cn":null,"air_date":null,"episode_count":1}""", secondSubjectLine);

        string[] firstEpisodeLines = ReadGzipLines(Path.Combine(output, "bangumi-episodes-v1-000001-000001.jsonl.gz"));
        Assert.Equal(
            [
                """{"id":11,"subject_id":1,"sort":1,"episode":"1","air_date":"2026-01-01"}""",
                """{"id":12,"subject_id":1,"sort":2,"episode":"2.5","air_date":"2026-01-08"}"""
            ],
            firstEpisodeLines);

        AssertOfflineZipMatchesManifest(output, result.Manifest);
        AssertChecksums(output);
    }

    [Fact]
    public async Task Generate_IsDeterministicForSameInputAndOptions()
    {
        string root = NewTempDirectory();
        string zip = FixtureZip.Create(
            root,
            ["""{"id":1,"type":2,"name":"A","name_cn":"甲","date":"2026-01-01"}"""],
            ["""{"id":10,"subject_id":1,"sort":1,"type":0,"airdate":"2026-01-01"}"""]);
        string hash = Sha256(zip);
        string output1 = Path.Combine(root, "out1");
        string output2 = Path.Combine(root, "out2");

        await Generate(zip, output1, hash, subjectsPerShard: 10);
        await Generate(zip, output2, hash, subjectsPerShard: 10);

        string[] files1 = Directory.GetFiles(output1).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        string[] files2 = Directory.GetFiles(output2).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;
        Assert.Equal(files1, files2);
        foreach (string file in files1)
        {
            Assert.Equal(
                await File.ReadAllBytesAsync(Path.Combine(output1, file)),
                await File.ReadAllBytesAsync(Path.Combine(output2, file)));
        }
    }

    [Fact]
    public async Task Generate_FailsWhenEntryIsMissing()
    {
        string root = NewTempDirectory();
        string zip = FixtureZip.Create(
            root,
            ["""{"id":1,"type":2,"name":"A","name_cn":"甲"}"""],
            episodes: null);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => Generate(zip, Path.Combine(root, "out"), Sha256(zip), subjectsPerShard: 10));

        Assert.Contains("episode.jsonlines", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Generate_FailsOnBadJsonAndDuplicateIds()
    {
        string root = NewTempDirectory();
        string badZip = FixtureZip.Create(
            Path.Combine(root, "bad"),
            ["""{"id":1,"type":2,"name":"A"}""", """{bad json"""],
            ["""{"id":10,"subject_id":1,"sort":1,"type":0}"""]);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => Generate(badZip, Path.Combine(root, "bad-out"), Sha256(badZip), subjectsPerShard: 10));

        string duplicateZip = FixtureZip.Create(
            Path.Combine(root, "dup"),
            ["""{"id":1,"type":2,"name":"A"}""", """{"id":1,"type":2,"name":"B"}"""],
            ["""{"id":10,"subject_id":1,"sort":1,"type":0}"""]);

        InvalidDataException ex = await Assert.ThrowsAsync<InvalidDataException>(
            () => Generate(duplicateZip, Path.Combine(root, "dup-out"), Sha256(duplicateZip), subjectsPerShard: 10));
        Assert.Contains("duplicate anime Subject ID", ex.Message, StringComparison.Ordinal);
    }

    private static Task<GenerationResult> Generate(string zip, string output, string upstreamSha256, int subjectsPerShard)
    {
        var generator = new BangumiArchiveGenerator();
        return generator.GenerateAsync(new GenerationOptions(
            output,
            zip,
            new ArchiveAsset(Path.GetFileName(zip), zip, DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture), new FileInfo(zip).Length, "archive"),
            upstreamSha256,
            "2026.01.02.1",
            new Uri("https://github.com/example/AnimeGoNetData/releases/download/2026.01.02.1/"),
            "0.1.0",
            subjectsPerShard,
            MinimumSubjects: 1,
            MinimumEpisodes: 1,
            GeneratedAtUtc: DateTimeOffset.Parse("2026-01-02T03:04:05.0000000+00:00", CultureInfo.InvariantCulture)));
    }

    private static string NewTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "AnimeGoNetDataTests", Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] ReadGzipLines(string path)
    {
        using FileStream file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        List<string> lines = [];
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static void AssertOfflineZipMatchesManifest(string output, DataManifest manifest)
    {
        string zipPath = Path.Combine(output, $"animegonetdata-{manifest.DataVersion}-offline.zip");
        using var zip = ZipFile.OpenRead(zipPath);
        string[] names = zip.Entries.Select(static entry => entry.FullName).Order(StringComparer.Ordinal).ToArray();
        string[] expected = new[] { "manifest.json" }
            .Concat(manifest.Assets.Select(static asset => asset.FileName))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(expected, names);
        Assert.DoesNotContain(zip.Entries, static entry => entry.FullName.Contains('/', StringComparison.Ordinal) || entry.FullName.Contains('\\', StringComparison.Ordinal));

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            string sourcePath = Path.Combine(output, entry.FullName);
            using Stream entryStream = entry.Open();
            using var memory = new MemoryStream();
            entryStream.CopyTo(memory);
            Assert.Equal(File.ReadAllBytes(sourcePath), memory.ToArray());
        }
    }

    private static void AssertChecksums(string output)
    {
        string[] lines = File.ReadAllLines(Path.Combine(output, "SHA256SUMS"));
        foreach (string line in lines)
        {
            string[] parts = line.Split("  ", StringSplitOptions.None);
            Assert.Equal(2, parts.Length);
            Assert.Equal(Sha256(Path.Combine(output, parts[1])), parts[0]);
        }
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexStringLower(hash);
    }
}

internal static class EquivalentManifestParser
{
    public static void Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        JsonElement root = document.RootElement;
        Assert.Equal(1, root.GetProperty("schema_version").GetInt32());
        Assert.Matches("^[a-z0-9][a-z0-9._-]{0,63}$", root.GetProperty("data_version").GetString()!);
        Assert.Equal(TimeSpan.Zero, DateTimeOffset.ParseExact(root.GetProperty("generated_at_utc").GetString()!, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Offset);
        Assert.True(Version.TryParse(root.GetProperty("minimum_client_version").GetString(), out _));
        Assert.Matches("^[0-9a-f]{64}$", root.GetProperty("upstream").GetProperty("sha256").GetString()!);

        long subjects = 0;
        long episodes = 0;
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (JsonElement asset in root.GetProperty("assets").EnumerateArray())
        {
            string kind = asset.GetProperty("kind").GetString()!;
            string fileName = asset.GetProperty("file_name").GetString()!;
            Assert.True(kind is "subjects" or "episodes");
            Assert.Equal(Path.GetFileName(fileName), fileName);
            Assert.EndsWith(".jsonl.gz", fileName, StringComparison.Ordinal);
            Assert.True(names.Add(fileName));
            Assert.True(Uri.TryCreate(asset.GetProperty("url").GetString(), UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https");
            Assert.True(asset.GetProperty("size_bytes").GetInt64() > 0);
            Assert.Matches("^[0-9a-f]{64}$", asset.GetProperty("sha256").GetString()!);
            Assert.True(asset.GetProperty("record_count").GetInt64() > 0);
            Assert.True(asset.GetProperty("subject_id_min").GetInt32() > 0);
            Assert.True(asset.GetProperty("subject_id_max").GetInt32() >= asset.GetProperty("subject_id_min").GetInt32());
            if (kind == "subjects") subjects += asset.GetProperty("record_count").GetInt64();
            else episodes += asset.GetProperty("record_count").GetInt64();
        }

        Assert.Equal(subjects, root.GetProperty("totals").GetProperty("subjects").GetInt64());
        Assert.Equal(episodes, root.GetProperty("totals").GetProperty("episodes").GetInt64());
    }
}
