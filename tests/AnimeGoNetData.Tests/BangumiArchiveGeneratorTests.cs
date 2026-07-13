using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AnimeGoNetData.Core;

namespace AnimeGoNetData.Tests;

public sealed class BangumiArchiveGeneratorTests
{
    private static readonly ArchiveAsset Asset = new(
        "dump-2026-01-01.000000Z.zip",
        "https://example.test/dump.zip",
        DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
        123);

    [Fact]
    public async Task Generate_CleansChunksHashesAndWritesManifestLast()
    {
        string root = NewTempDirectory();
        string zip = FixtureZip.Create(
            root,
            [
                """{"id":2,"type":2,"name":"B","name_cn":"乙"}""",
                """{"id":1,"type":1,"name":"Book","name_cn":"书"}""",
                """{"id":1,"type":2,"name":"A2","name_cn":"甲二"}""",
                """{"id":1,"type":2,"name":"A1","name_cn":"甲一"}""",
                """{bad json"""
            ],
            [
                """{"id":12,"subject_id":1,"sort":2,"type":0,"name":"2","name_cn":"二","airdate":"2026-01-08"}""",
                """{"id":11,"subject_id":1,"sort":1,"type":0,"name":"1","name_cn":"一","airdate":"2026-01-01"}""",
                """{"id":13,"subject_id":1,"sort":3,"type":1,"name":"SP","name_cn":"SP","airdate":"2026-01-15"}""",
                """{"id":12,"subject_id":1,"sort":2,"type":0,"name":"2B","name_cn":"二B","airdate":"2026-01-08"}""",
                """{bad json"""
            ]);
        string output = Path.Combine(root, "out");

        GenerationResult result = await Generate(zip, output, chunkSize: 1);

        Assert.Equal(1, result.SkippedBadSubjectLines);
        Assert.Equal(1, result.SkippedBadEpisodeLines);
        Assert.Equal(1, result.DuplicateSubjects);
        Assert.Equal(1, result.DuplicateEpisodes);
        Assert.Equal(2, result.Manifest.RecordCounts["subjects"]);
        Assert.Equal(2, result.Manifest.RecordCounts["episodes"]);

        string[] files = Directory.GetFiles(output).Select(Path.GetFileName).Order().ToArray()!;
        Assert.Equal(
            [
                "bangumi-episodes-v1-00000.jsonl.gz",
                "bangumi-episodes-v1-00001.jsonl.gz",
                "bangumi-subjects-v1-00000.jsonl.gz",
                "bangumi-subjects-v1-00001.jsonl.gz",
                "manifest.json"
            ],
            files);

        string subjectLine = ReadGzipLines(Path.Combine(output, "bangumi-subjects-v1-00000.jsonl.gz")).Single();
        using JsonDocument subjectDocument = JsonDocument.Parse(subjectLine);
        JsonElement subject = subjectDocument.RootElement;
        Assert.Equal(1, subject.GetProperty("id").GetInt32());
        Assert.Equal("A1", subject.GetProperty("name").GetString());
        Assert.Equal(2, subject.GetProperty("eps").GetInt32());
        Assert.Equal("2026-01-01", subject.GetProperty("airdate").GetString());

        using JsonDocument manifestDocument = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "manifest.json")));
        JsonElement manifest = manifestDocument.RootElement;
        Assert.Equal(1, manifest.GetProperty("schema_version").GetInt32());
        Assert.Equal("dump-2026-01-01.000000Z.zip", manifest.GetProperty("dataset_version").GetString());
        Assert.Equal("2026-01-02T03:04:05+00:00", manifest.GetProperty("generated_at").GetString());

        foreach (JsonElement file in manifest.GetProperty("files").EnumerateArray())
        {
            string path = Path.Combine(output, file.GetProperty("path").GetString()!);
            Assert.Equal(new FileInfo(path).Length, file.GetProperty("bytes").GetInt64());
            Assert.Equal(Sha256(path), file.GetProperty("sha256").GetString());
        }
    }

    [Fact]
    public async Task Generate_IsDeterministicForSameInputAndOptions()
    {
        string root = NewTempDirectory();
        string zip = FixtureZip.Create(
            root,
            ["""{"id":1,"type":2,"name":"A","name_cn":"甲"}"""],
            ["""{"id":10,"subject_id":1,"sort":1,"type":0,"name":"1","name_cn":"一","airdate":"2026-01-01"}"""]);

        string output1 = Path.Combine(root, "out1");
        string output2 = Path.Combine(root, "out2");

        await Generate(zip, output1, chunkSize: 10);
        await Generate(zip, output2, chunkSize: 10);

        string[] files1 = Directory.GetFiles(output1).Select(Path.GetFileName).Order().ToArray()!;
        string[] files2 = Directory.GetFiles(output2).Select(Path.GetFileName).Order().ToArray()!;
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

        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(
            () => Generate(zip, Path.Combine(root, "out"), chunkSize: 10));

        Assert.Contains("episode.jsonlines", ex.Message, StringComparison.Ordinal);
    }

    private static Task<GenerationResult> Generate(string zip, string output, int chunkSize)
    {
        var generator = new BangumiArchiveGenerator();
        return generator.GenerateAsync(new GenerationOptions(
            output,
            zip,
            Asset,
            "https://api.example.test/latest",
            chunkSize,
            MinimumSubjects: 0,
            MinimumEpisodes: 0,
            GeneratedAt: DateTimeOffset.Parse("2026-01-02T03:04:05Z", CultureInfo.InvariantCulture)));
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
        List<string> lines = new();
        while (reader.ReadLine() is { } line)
        {
            lines.Add(line);
        }

        return lines.ToArray();
    }

    private static string Sha256(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
