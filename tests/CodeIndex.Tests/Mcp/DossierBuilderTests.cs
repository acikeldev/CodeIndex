using CodeIndex.Mcp;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Direct tests for <see cref="DossierBuilder.Assemble"/>: an empty/whitespace section body is skipped (not
/// rendered as a bare heading), and once the budget is exhausted the remaining sections are replaced by the
/// truncation note while the first section is always kept.
/// </summary>
public sealed class DossierBuilderTests
{
    [Fact]
    public void Assemble_SkipsEmptySectionBodies()
    {
        List<(string Heading, string Body)> sections = new()
        {
            ("Real", "content here"),
            ("Empty", "   "),
        };

        string output = DossierBuilder.Assemble("# t", sections);

        output.Should().Contain("## Real");
        output.Should().NotContain("## Empty");
        output.Should().NotContain("Response budget reached");
    }

    [Fact]
    public void Assemble_KeepsFirstSectionThenReplacesOverflowWithNote()
    {
        // ~22.5k tokens (chars/4) — over the ~19.5k budget — so the first section is kept and the second dropped.
        string big = new('x', 90_000);
        List<(string Heading, string Body)> sections = new()
        {
            ("First", big),
            ("Second", "should be dropped"),
        };

        string output = DossierBuilder.Assemble("# t", sections);

        output.Should().Contain("## First");
        output.Should().NotContain("## Second");
        output.Should().Contain("Response budget reached");
    }
}
