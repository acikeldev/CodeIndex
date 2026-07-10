using System.Security.Cryptography;
using System.Text;
using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// codeindex.json config: parsing (comments/trailing commas, invalid, null), defaults, and the
/// cacheDir resolution branches (repo / user-profile / explicit path). Driven entirely off the shared
/// in-memory file system and an injected local-app-data seam, so nothing touches the real disk or profile.
/// </summary>
public class CodeIndexConfigTests
{
    private const string RepoRoot = @"C:\repo";
    private static readonly string ConfigPath = Path.Combine(RepoRoot, "codeindex.json");

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        InMemoryFileSystem fs = new();

        CodeIndexConfig c = CodeIndexConfig.Load(RepoRoot, fs);

        c.Should().BeSameAs(CodeIndexConfig.Default);
        c.CacheDir.Should().BeNull();
        c.LooseProjects.Should().BeFalse();
    }

    [Fact]
    public void Default_HasExpectedValues()
    {
        CodeIndexConfig c = CodeIndexConfig.Default;

        c.CacheDir.Should().BeNull();
        c.Exclude.Should().BeNull();
        c.GeneratedGlobs.Should().BeNull();
        c.LooseProjects.Should().BeFalse();
        c.IndexTypeScript.Should().BeTrue();
    }

    [Fact]
    public void Load_ParsesKeys_WithCommentsAndTrailingCommas()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(
            ConfigPath,
            "{\n  // cache in the user profile\n  \"cacheDir\": \"user\",\n  \"looseProjects\": true,\n  \"exclude\": [\"vendor\"],\n}\n");

        CodeIndexConfig c = CodeIndexConfig.Load(RepoRoot, fs);

        c.CacheDir.Should().Be("user");
        c.LooseProjects.Should().BeTrue();
        c.Exclude.Should().Equal("vendor");
    }

    [Fact]
    public void Load_ParsesAllKeys_CaseInsensitively()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(
            ConfigPath,
            "{ \"CacheDir\": \"C:\\\\cache\", \"GeneratedGlobs\": [\"**/*.g.cs\"], \"IndexTypeScript\": false }");

        CodeIndexConfig c = CodeIndexConfig.Load(RepoRoot, fs);

        c.CacheDir.Should().Be(@"C:\cache");
        c.GeneratedGlobs.Should().Equal("**/*.g.cs");
        c.IndexTypeScript.Should().BeFalse();
    }

    [Fact]
    public void Load_InvalidJson_FallsBackToDefaults()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(ConfigPath, "{ this is not valid json ");

        CodeIndexConfig c = CodeIndexConfig.Load(RepoRoot, fs); // must not throw

        c.Should().BeSameAs(CodeIndexConfig.Default);
        c.CacheDir.Should().BeNull();
    }

    [Fact]
    public void Load_JsonNullLiteral_ReturnsDefault()
    {
        InMemoryFileSystem fs = new();
        fs.AddFile(ConfigPath, "null");

        CodeIndexConfig c = CodeIndexConfig.Load(RepoRoot, fs);

        c.Should().BeSameAs(CodeIndexConfig.Default);
    }

    [Fact]
    public void ResolveCacheDirectory_DefaultsToRepo_WhenCacheDirNull()
    {
        CodeIndexConfig c = new();

        string dir = c.ResolveCacheDirectory(RepoRoot);

        dir.Should().Be(Path.Combine(RepoRoot, ".codeindex"));
    }

    [Fact]
    public void ResolveCacheDirectory_Repo_IsExplicitlyHonored()
    {
        CodeIndexConfig c = new() { CacheDir = "REPO" }; // case-insensitive

        string dir = c.ResolveCacheDirectory(RepoRoot);

        dir.Should().Be(Path.Combine(RepoRoot, ".codeindex"));
    }

    [Fact]
    public void ResolveCacheDirectory_ExplicitPath_ReturnedVerbatim()
    {
        CodeIndexConfig c = new() { CacheDir = @"D:\somewhere\else" };

        string dir = c.ResolveCacheDirectory(RepoRoot);

        dir.Should().Be(@"D:\somewhere\else");
    }

    [Fact]
    public void ResolveCacheDirectory_UserProfile_IsOutsideRepo_UsingInjectedSeam()
    {
        const string appData = @"C:\Users\Test\AppData\Local";
        CodeIndexConfig c = new() { CacheDir = "user", LocalAppDataProvider = () => appData };

        string dir = c.ResolveCacheDirectory(RepoRoot);

        dir.Should().NotContain(RepoRoot);
        dir.Should().StartWith(Path.Combine(appData, "CodeIndex"));
    }

    [Fact]
    public void ResolveCacheDirectory_UserProfile_HashMatchesByteForByte()
    {
        const string appData = @"C:\Users\Test\AppData\Local";
        CodeIndexConfig c = new() { CacheDir = "USER", LocalAppDataProvider = () => appData };

        string key = Path.GetFullPath(RepoRoot).ToLowerInvariant();
        string expectedHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key)))[..12];

        string dir = c.ResolveCacheDirectory(RepoRoot);

        dir.Should().Be(Path.Combine(appData, "CodeIndex", expectedHash));
        expectedHash.Length.Should().Be(12);
    }
}
