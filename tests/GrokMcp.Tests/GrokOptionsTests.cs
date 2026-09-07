using GrokMcp.Config;

namespace GrokMcp.Tests;

// Each test mutates real process environment variables, scoped to a unique key prefix
// to avoid bleed between parallel test classes. xUnit serializes tests within a class.
public class GrokOptionsTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly List<string> _setEnvKeys = new();
    private readonly string _prefix = $"TEST_GROK_{Guid.NewGuid():N}_";

    public GrokOptionsTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "grok-mcp-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        foreach (var k in _setEnvKeys)
            Environment.SetEnvironmentVariable(k, null);
        if (Directory.Exists(_tmpDir)) Directory.Delete(_tmpDir, recursive: true);
    }

    [Fact]
    public void LoadEnvFile_missing_file_is_noop()
    {
        var missing = Path.Combine(_tmpDir, "absent.env");
        var record = Record.Exception(() => GrokOptions.LoadEnvFile(missing));
        Assert.Null(record);
    }

    [Fact]
    public void LoadEnvFile_basic_key_value_is_set()
    {
        var key = _prefix + "K1";
        var path = WriteFile($"{key}=hello");
        GrokOptions.LoadEnvFile(path);
        Track(key);
        Assert.Equal("hello", Environment.GetEnvironmentVariable(key));
    }

    [Fact]
    public void LoadEnvFile_strips_surrounding_quotes()
    {
        var k1 = _prefix + "Q1";
        var k2 = _prefix + "Q2";
        var path = WriteFile($"{k1}=\"with spaces\"\n{k2}='single quoted'");
        GrokOptions.LoadEnvFile(path);
        Track(k1); Track(k2);
        Assert.Equal("with spaces", Environment.GetEnvironmentVariable(k1));
        Assert.Equal("single quoted", Environment.GetEnvironmentVariable(k2));
    }

    [Fact]
    public void LoadEnvFile_skips_comments_and_blank_lines()
    {
        var key = _prefix + "C1";
        var path = WriteFile($"# this is a comment\n\n{key}=ok\n# trailing comment");
        GrokOptions.LoadEnvFile(path);
        Track(key);
        Assert.Equal("ok", Environment.GetEnvironmentVariable(key));
    }

    // The defaults are the release contract: each name is checked against xAI's live inventory
    // before a release (see CLAUDE.md), so a silent drift here should fail loudly.
    [Fact]
    public void Model_defaults_match_the_release_contract()
    {
        var o = new GrokOptions();
        Assert.Equal("grok-4.6", o.ChatModel);
        Assert.Equal("grok-4.6", o.CreativeModel);
        Assert.Equal("grok-4.20-0309-non-reasoning", o.FastModel);
        Assert.Equal("grok-4.20-multi-agent-0309", o.MultiAgentModel);
        Assert.Equal("grok-imagine-image-2.0", o.ImageModel);
        Assert.Equal("grok-imagine-video-1.5", o.VideoModel);
    }

    [Fact]
    public void BindFromEnvironment_reads_GROK_MCP_VIDEO_MODEL_override()
    {
        const string key = "GROK_MCP_VIDEO_MODEL";
        Environment.SetEnvironmentVariable(key, "custom-video-model");
        Track(key);

        var o = new GrokOptions();
        GrokOptions.BindFromEnvironment(o);

        Assert.Equal("custom-video-model", o.VideoModel);
    }

    [Fact]
    public void LoadEnvFile_does_not_overwrite_existing_env_var()
    {
        var key = _prefix + "P1";
        Environment.SetEnvironmentVariable(key, "from-real-env");
        Track(key);

        var path = WriteFile($"{key}=from-file");
        GrokOptions.LoadEnvFile(path);

        Assert.Equal("from-real-env", Environment.GetEnvironmentVariable(key));
    }

    private string WriteFile(string contents)
    {
        var path = Path.Combine(_tmpDir, $"{Guid.NewGuid():N}.env");
        File.WriteAllText(path, contents);
        return path;
    }

    private void Track(string key) => _setEnvKeys.Add(key);
}
