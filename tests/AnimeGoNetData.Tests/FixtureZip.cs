using System.IO.Compression;
using System.Text;

namespace AnimeGoNetData.Tests;

internal static class FixtureZip
{
    public static string Create(string directory, string[]? subjects, string[]? episodes, string[]? relations = null)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "fixture.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (subjects is not null)
        {
            WriteEntry(zip, "subject.jsonlines", subjects);
        }

        if (episodes is not null)
        {
            WriteEntry(zip, "episode.jsonlines", episodes);
        }

        if (relations is not null)
        {
            WriteEntry(zip, "subject-relations.jsonlines", relations);
        }

        return path;
    }

    private static void WriteEntry(ZipArchive zip, string name, string[] lines)
    {
        ZipArchiveEntry entry = zip.CreateEntry(name, CompressionLevel.NoCompression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        foreach (string line in lines)
        {
            writer.WriteLine(line);
        }
    }
}
