using CodeIndex.Abstractions;
using CodeIndex.Caching;
using CodeIndex.Models;

namespace CodeIndex.Tests.Caching;

public sealed class MessagePackIndexCacheTests
{
    private readonly IFileSystem _fs = Substitute.For<IFileSystem>();
    private const string RepoRoot = @"C:\Repo";
    private const string CachePath = @"C:\Repo\.codeindex\cache.bin";
    private const string CacheDir = @"C:\Repo\.codeindex";

    private readonly MessagePackIndexCache _cache;

    public MessagePackIndexCacheTests()
    {
        _cache = new MessagePackIndexCache(RepoRoot, _fs);
    }

    // ── Save ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Save_CreatesDirectoryAndWritesBytes()
    {
        IReadOnlyList<ProjectIndex> projects = [MakeProject("App")];

        _cache.Save(projects);

        _fs.Received(1).EnsureDirectoryExists(CacheDir);
        _fs.Received(1).WriteAllBytes(CachePath, Arg.Is<byte[]>(b => b.Length > 0));
    }

    [Fact]
    public void Save_EmptyList_WritesBytes()
    {
        _cache.Save([]);

        _fs.Received(1).WriteAllBytes(CachePath, Arg.Is<byte[]>(b => b.Length > 0));
    }

    [Fact]
    public void Save_WriteThrows_DoesNotPropagate()
    {
        _fs.When(f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
           .Do(_ => throw new IOException("disk full"));

        // Must not throw
        _cache.Invoking(c => c.Save([MakeProject("App")])).Should().NotThrow();
    }

    [Fact]
    public void Save_EnsureDirectoryThrows_DoesNotPropagate()
    {
        _fs.When(f => f.EnsureDirectoryExists(Arg.Any<string>()))
           .Do(_ => throw new UnauthorizedAccessException());

        _cache.Invoking(c => c.Save([MakeProject("App")])).Should().NotThrow();
    }

    // ── TryLoad ───────────────────────────────────────────────────────────────

    [Fact]
    public void TryLoad_CacheFileNotFound_ReturnsNull()
    {
        _fs.FileExists(CachePath).Returns(false);

        IReadOnlyList<ProjectIndex>? result = _cache.TryLoad();

        result.Should().BeNull();
        _fs.DidNotReceive().ReadAllBytes(Arg.Any<string>());
    }

    [Fact]
    public void TryLoad_CorruptBytes_ReturnsNull()
    {
        _fs.FileExists(CachePath).Returns(true);
        _fs.ReadAllBytes(CachePath).Returns([0xFF, 0x00, 0xAB]);

        IReadOnlyList<ProjectIndex>? result = _cache.TryLoad();

        result.Should().BeNull();
    }

    [Fact]
    public void TryLoad_ReadThrows_ReturnsNull()
    {
        _fs.FileExists(CachePath).Returns(true);
        _fs.When(f => f.ReadAllBytes(Arg.Any<string>()))
           .Do(_ => throw new IOException("locked"));

        IReadOnlyList<ProjectIndex>? result = _cache.TryLoad();

        result.Should().BeNull();
    }

    // ── Round-trip ────────────────────────────────────────────────────────────

    [Fact]
    public void SaveAndLoad_RoundTrips_SingleProject()
    {
        byte[]? written = null;
        _fs.When(f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
           .Do(ci => written = ci.Arg<byte[]>());
        _fs.FileExists(CachePath).Returns(true);
        _fs.ReadAllBytes(CachePath).Returns(_ => written!);

        ProjectIndex project = MakeProject("MyApp");
        _cache.Save([project]);

        IReadOnlyList<ProjectIndex>? loaded = _cache.TryLoad();

        loaded.Should().NotBeNull();
        loaded!.Should().ContainSingle().Which.Name.Should().Be("MyApp");
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_SourceFilesAndTypes()
    {
        byte[]? written = null;
        _fs.When(f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
           .Do(ci => written = ci.Arg<byte[]>());
        _fs.FileExists(CachePath).Returns(true);
        _fs.ReadAllBytes(CachePath).Returns(_ => written!);

        MemberInfo member = new()
        {
            Name = "GetUser",
            Kind = SymbolKind.Method,
            Signature = "public string GetUser()",
            ReturnType = "string",
            StartLine = 5,
            EndLine = 8,
        };
        TypeInfo type = new()
        {
            Name = "UserService",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = ["IUserService"],
            Members = [member],
            StartLine = 1,
            EndLine = 20,
        };
        SourceFileIndex file = new()
        {
            FileName = "UserService.cs",
            FullPath = @"C:\Repo\UserService.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        ProjectIndex project = new()
        {
            Name = "App",
            Directory = @"C:\Repo\App",
            ProjectFilePath = @"C:\Repo\App\App.csproj",
            SourceFiles = [file],
        };

        _cache.Save([project]);
        IReadOnlyList<ProjectIndex>? loaded = _cache.TryLoad();

        loaded.Should().NotBeNull();
        ProjectIndex p = loaded!.Single();
        p.Name.Should().Be("App");

        SourceFileIndex f2 = p.SourceFiles.Single();
        f2.FileName.Should().Be("UserService.cs");
        f2.Namespace.Should().Be("App");
        f2.IndexedAtUtc.Should().Be(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));

        TypeInfo t2 = f2.Types.Single();
        t2.Name.Should().Be("UserService");
        t2.Kind.Should().Be(SymbolKind.Class);
        t2.BaseTypes.Should().ContainSingle().Which.Should().Be("IUserService");

        MemberInfo m2 = t2.Members.Single();
        m2.Name.Should().Be("GetUser");
        m2.Kind.Should().Be(SymbolKind.Method);
        m2.ReturnType.Should().Be("string");
        m2.StartLine.Should().Be(5);
    }

    [Fact]
    public void SaveAndLoad_RoundTrips_NullReturnType()
    {
        byte[]? written = null;
        _fs.When(f => f.WriteAllBytes(Arg.Any<string>(), Arg.Any<byte[]>()))
           .Do(ci => written = ci.Arg<byte[]>());
        _fs.FileExists(CachePath).Returns(true);
        _fs.ReadAllBytes(CachePath).Returns(_ => written!);

        MemberInfo ctor = new()
        {
            Name = "UserService",
            Kind = SymbolKind.Constructor,
            Signature = "public UserService()",
            ReturnType = null,
            StartLine = 3,
            EndLine = 5,
        };
        TypeInfo type = new()
        {
            Name = "UserService",
            Namespace = "App",
            Kind = SymbolKind.Class,
            BaseTypes = [],
            Members = [ctor],
            StartLine = 1,
            EndLine = 10,
        };
        SourceFileIndex file = new()
        {
            FileName = "UserService.cs",
            FullPath = @"C:\Repo\UserService.cs",
            Namespace = "App",
            Types = [type],
            IndexedAtUtc = DateTime.UtcNow,
        };

        _cache.Save([new ProjectIndex
        {
            Name = "App",
            Directory = @"C:\Repo",
            ProjectFilePath = @"C:\Repo\App.csproj",
            SourceFiles = [file],
        }]);

        IReadOnlyList<ProjectIndex>? loaded = _cache.TryLoad();

        loaded!.Single().SourceFiles.Single().Types.Single().Members.Single()
            .ReturnType.Should().BeNull();
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ProjectIndex MakeProject(string name) => new()
    {
        Name = name,
        Directory = @"C:\Repo\App",
        ProjectFilePath = @"C:\Repo\App\App.csproj",
    };
}
