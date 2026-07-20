using CodeIndex.Internal;
using CodeIndex.Tests.Infrastructure;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Cross-session notes store: persists agent-authored facts to the cache dir, dedups, caps, renders an onboarding
/// section newest-first, and returns nothing (byte-identical onboarding) when empty. Separate file from the
/// build-keyed digest, so notes survive a reindex.
/// </summary>
public sealed class SessionNotesTests
{
    private const string CacheDir = @"C:\repo\.codeindex";
    private readonly InMemoryFileSystem _fs = new();

    [Fact]
    public void Add_ThenLoad_RoundTrips()
    {
        SessionNotes.Add(_fs, CacheDir, "auth entry point is AuthController.Login");

        List<string> notes = SessionNotes.Load(_fs, CacheDir);
        notes.Should().ContainSingle().Which.Should().Be("auth entry point is AuthController.Login");
    }

    [Fact]
    public void Add_Dedups_AndMovesToNewest()
    {
        SessionNotes.Add(_fs, CacheDir, "fact A");
        SessionNotes.Add(_fs, CacheDir, "fact B");
        SessionNotes.Add(_fs, CacheDir, "fact A");   // duplicate

        List<string> notes = SessionNotes.Load(_fs, CacheDir);
        notes.Should().Equal("fact B", "fact A");    // A deduped and moved to newest (end)
    }

    [Fact]
    public void Add_CapsAtMostRecent()
    {
        for (int i = 0; i < 60; i++)
        {
            SessionNotes.Add(_fs, CacheDir, $"note {i}");
        }

        List<string> notes = SessionNotes.Load(_fs, CacheDir);
        notes.Count.Should().Be(50);
        notes.Should().Contain("note 59");     // newest kept
        notes.Should().NotContain("note 0");   // oldest dropped
    }

    [Fact]
    public void Add_Normalizes_MultilineToOneLine()
    {
        SessionNotes.Add(_fs, CacheDir, "  line one\nline two\r\n   spaced   out  ");

        SessionNotes.Load(_fs, CacheDir).Should().ContainSingle()
            .Which.Should().Be("line one line two spaced out");
    }

    [Fact]
    public void Add_EmptyNote_IsIgnored()
    {
        string result = SessionNotes.Add(_fs, CacheDir, "   ");

        result.Should().Contain("empty note ignored");
        SessionNotes.Load(_fs, CacheDir).Should().BeEmpty();
    }

    [Fact]
    public void RenderSection_EmptyWhenNoNotes()
    {
        SessionNotes.RenderSection(_fs, CacheDir).Should().BeEmpty();
    }

    [Fact]
    public void RenderSection_NewestFirst()
    {
        SessionNotes.Add(_fs, CacheDir, "older");
        SessionNotes.Add(_fs, CacheDir, "newer");

        string section = SessionNotes.RenderSection(_fs, CacheDir);
        section.Should().Contain("Notes from previous sessions");
        section.IndexOf("newer", StringComparison.Ordinal)
            .Should().BeLessThan(section.IndexOf("older", StringComparison.Ordinal), "newest note is listed first");
    }
}
