using AnimeGoNetData.Core;

namespace AnimeGoNetData.Tests;

public sealed class ReleaseAssetSelectorTests
{
    [Fact]
    public void SelectLatestZip_Ignores7zAndUsesUpdatedAt()
    {
        const string json = """
{
  "tag_name": "archive",
  "assets": [
    { "name": "dump-new.7z", "browser_download_url": "https://example.test/new.7z", "updated_at": "2026-01-03T00:00:00Z", "size": 9 },
    { "name": "dump-old.zip", "browser_download_url": "https://example.test/old.zip", "updated_at": "2026-01-01T00:00:00Z", "size": 1 },
    { "name": "dump-new.zip", "browser_download_url": "https://example.test/new.zip", "updated_at": "2026-01-02T00:00:00Z", "size": 2 }
  ]
}
""";

        ArchiveAsset selected = ReleaseAssetSelector.SelectLatestZip(json);

        Assert.Equal("dump-new.zip", selected.Name);
        Assert.Equal("https://example.test/new.zip", selected.DownloadUrl);
        Assert.Equal(2, selected.Size);
        Assert.Equal("archive", selected.Release);
    }
}
