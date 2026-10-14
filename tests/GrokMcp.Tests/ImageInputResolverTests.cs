using GrokMcp.Services;

namespace GrokMcp.Tests;

public class ImageInputResolverTests : IDisposable
{
    private readonly ImageInputResolver _resolver = new();
    private readonly List<string> _tempFiles = new();

    public void Dispose()
    {
        foreach (var f in _tempFiles)
            try { if (File.Exists(f)) File.Delete(f); } catch { }
    }

    [Fact]
    public void Http_url_passes_through_unchanged()
    {
        var url = "https://example.com/image.jpg";
        Assert.Equal(url, _resolver.ResolveDataUri(url));
    }

    [Fact]
    public void Data_uri_passes_through_unchanged()
    {
        var uri = "data:image/png;base64,iVBORw0KGgo=";
        Assert.Equal(uri, _resolver.ResolveDataUri(uri));
    }

    [Fact]
    public void Absolute_path_returns_data_uri_with_sniffed_mime()
    {
        var path = WriteTempFile(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0xAA, 0xBB }, ".jpg");
        var result = _resolver.ResolveDataUri(path);
        Assert.StartsWith("data:image/jpeg;base64,", result);
    }

    [Fact]
    public void Absolute_path_missing_file_throws_with_path_in_message()
    {
        var fake = Path.Combine(Path.GetTempPath(), $"does-not-exist-{Guid.NewGuid():N}.png");
        var ex = Assert.Throws<FileNotFoundException>(() => _resolver.Resolve(fake));
        Assert.Contains(fake, ex.Message);
    }

    [Fact]
    public void Raw_base64_is_wrapped_as_png_data_uri()
    {
        var b64 = "AAAA";
        Assert.Equal($"data:image/png;base64,{b64}", _resolver.ResolveDataUri(b64));
    }

    [Fact]
    public void Raw_jpeg_base64_is_not_mistaken_for_a_unix_path()
    {
        // JPEG base64 starts with "/9j/", which Unix treats as a fully qualified path.
        var b64 = Convert.ToBase64String(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10 });
        Assert.StartsWith("/9j/", b64);
        Assert.Equal($"data:image/jpeg;base64,{b64}", _resolver.ResolveDataUri(b64));
    }

    [Fact]
    public void Missing_unix_style_path_still_throws()
    {
        // Contains a '.', so it can never be base64; must stay a file-not-found error everywhere.
        var fake = $"/no-such-dir-{Guid.NewGuid():N}/image.png";
        if (!Path.IsPathFullyQualified(fake)) return; // not a path shape on Windows
        Assert.Throws<FileNotFoundException>(() => _resolver.Resolve(fake));
    }

    private string WriteTempFile(byte[] bytes, string ext)
    {
        var path = Path.Combine(Path.GetTempPath(), $"grok-mcp-test-{Guid.NewGuid():N}{ext}");
        File.WriteAllBytes(path, bytes);
        _tempFiles.Add(path);
        return path;
    }
}
