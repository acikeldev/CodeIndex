using CodeIndex.Internal;
using CodeIndex.Models;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Composite relevance ranking for search_symbol results: match tier
/// (exact > prefix > substring > camelCase-initials) → type-before-member →
/// hand-written-before-generated → name → path → line.
/// </summary>
public class SymbolRankerTests
{
    private static SymbolSearchResult Result(
        string name,
        string? parentType = null,
        string file = @"C:\repo\Widget.cs",
        int startLine = 1)
    {
        return new SymbolSearchResult
        {
            Name = name,
            Kind = "Class",
            Project = "P",
            File = file,
            SourceFilePath = file,
            StartLine = startLine,
            LineCount = 1,
            ParentType = parentType,
        };
    }

    [Theory]
    [InlineData("Widget", "Widget", SymbolRanker.TierExact)]
    [InlineData("widget", "Widget", SymbolRanker.TierExact)]      // case-insensitive
    [InlineData("WidgetFactory", "Widget", SymbolRanker.TierPrefix)]
    [InlineData("MyWidget", "Widget", SymbolRanker.TierSubstring)]
    [InlineData("GetUsersByRole", "GUBR", SymbolRanker.TierSubsequence)]
    [InlineData("Foo", "Bar", SymbolRanker.TierNoMatch)]
    public void MatchTier_ClassifiesEachTier(string name, string query, int expectedTier)
    {
        SymbolRanker.MatchTier(name, query).Should().Be(expectedTier);
    }

    [Fact]
    public void KeyFor_OrdersExactBeforePrefixBeforeContainsBeforeSubsequence()
    {
        const string query = "GUBR";

        SymbolSortKey exact = SymbolRanker.KeyFor(Result("GUBR"), query);
        SymbolSortKey prefix = SymbolRanker.KeyFor(Result("GUBRExtra"), query);
        SymbolSortKey contains = SymbolRanker.KeyFor(Result("XGUBRY"), query);
        SymbolSortKey subsequence = SymbolRanker.KeyFor(Result("GetUsersByRole"), query);

        List<SymbolSortKey> keys = [subsequence, contains, prefix, exact];
        keys.Sort();

        keys.Should().Equal(exact, prefix, contains, subsequence);
    }

    [Fact]
    public void KeyFor_PutsTypesBeforeMembersWithinSameTier()
    {
        const string query = "Widget";

        SymbolSortKey type = SymbolRanker.KeyFor(Result("Widget", parentType: null), query);
        SymbolSortKey member = SymbolRanker.KeyFor(Result("Widget", parentType: "Container"), query);

        type.CompareTo(member).Should().BeLessThan(0);
    }

    [Fact]
    public void KeyFor_PutsHandWrittenBeforeGenerated()
    {
        const string query = "Widget";

        SymbolSortKey handWritten = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\Widget.cs"), query);
        SymbolSortKey generated = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\Widget.g.cs"), query);

        handWritten.CompareTo(generated).Should().BeLessThan(0);
    }

    [Fact]
    public void KeyFor_TieBreaksByNameThenPathThenLine()
    {
        const string query = "Widget";

        SymbolSortKey nameA = SymbolRanker.KeyFor(Result("WidgetA", file: @"C:\repo\A.cs"), query);
        SymbolSortKey nameB = SymbolRanker.KeyFor(Result("WidgetB", file: @"C:\repo\A.cs"), query);
        nameA.CompareTo(nameB).Should().BeLessThan(0);

        SymbolSortKey pathA = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\A.cs"), query);
        SymbolSortKey pathB = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\B.cs"), query);
        pathA.CompareTo(pathB).Should().BeLessThan(0);

        SymbolSortKey lineLow = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\A.cs", startLine: 5), query);
        SymbolSortKey lineHigh = SymbolRanker.KeyFor(Result("Widget", file: @"C:\repo\A.cs", startLine: 9), query);
        lineLow.CompareTo(lineHigh).Should().BeLessThan(0);
    }

    [Fact]
    public void KeyFor_IsReflexivelyEqualForIdenticalInput()
    {
        SymbolSortKey a = SymbolRanker.KeyFor(Result("Widget"), "Widget");
        SymbolSortKey b = SymbolRanker.KeyFor(Result("Widget"), "Widget");

        a.CompareTo(b).Should().Be(0);
    }

    [Fact]
    public void IsInitialsSubsequence_MatchesWordBoundaryInitials()
    {
        SymbolRanker.IsInitialsSubsequence("GetUsersByRole", "GUBR").Should().BeTrue();
    }

    [Theory]
    [InlineData("GetUsersByRole", "GUB")]     // partial prefix of initials
    [InlineData("get_users_by_role", "GUBR")] // underscore boundaries
    [InlineData("a.b.c.d", "BCD")]            // dot boundaries
    [InlineData("Order2Item", "OI")]          // digit->upper hump
    public void IsInitialsSubsequence_MatchesVariousBoundaries(string name, string query)
    {
        SymbolRanker.IsInitialsSubsequence(name, query).Should().BeTrue();
    }

    [Theory]
    [InlineData("GetUsersByRole", "GUBRX")] // query longer than available initials
    [InlineData("GetUsersByRole", "RB")]    // wrong order (R is last initial, no B after)
    [InlineData("GetUsersByRole", "GX")]    // second char never appears as an initial
    [InlineData("GetUsersByRole", "X")]     // length < 2 short-circuit
    [InlineData("", "GU")]                   // empty name
    [InlineData("GetUsersByRole", "G")]     // single char rejected
    public void IsInitialsSubsequence_RejectsNonMatches(string name, string query)
    {
        SymbolRanker.IsInitialsSubsequence(name, query).Should().BeFalse();
    }
}
