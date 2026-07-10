using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// NameSuggester near-name tiers (exact / prefix / substring / bounded-edit) and the "Did you mean …?" formatting.
/// Pure string logic — no filesystem or index involved.
/// </summary>
public class DidYouMeanTests
{
    private static readonly string[] Names = ["StudyInfo", "ExperimentalFeature", "ExperimentalFeaturesService", "OrgSetting"];

    [Fact]
    public void Nearest_EmptyQuery_ReturnsEmpty() =>
        NameSuggester.Nearest("", Names).Should().BeEmpty();

    [Fact]
    public void Nearest_NullQuery_ReturnsEmpty() =>
        NameSuggester.Nearest(null!, Names).Should().BeEmpty();

    [Fact]
    public void Nearest_Prefix_CandidateStartsWithQuery() =>
        NameSuggester.Nearest("Study", Names).Should().Contain("StudyInfo");

    [Fact]
    public void Nearest_Prefix_QueryStartsWithCandidate() =>
        // query longer than candidate; candidate is a prefix of the query
        NameSuggester.Nearest("StudyInfoExtra", ["StudyInfo"]).Should().Contain("StudyInfo");

    [Fact]
    public void Nearest_Substring_CandidateContainsQuery() =>
        // "feat" is neither a prefix nor an exact match, but is contained in "experimentalfeature"
        NameSuggester.Nearest("Feat", Names).Should().Contain("ExperimentalFeature");

    [Fact]
    public void Nearest_Substring_QueryContainsCandidate() =>
        // candidate "Feature" is contained inside the query, not a prefix of it
        NameSuggester.Nearest("MyFeatureName", ["Feature"]).Should().Contain("Feature");

    [Fact]
    public void Nearest_SingularPluralTypo() =>
        NameSuggester.Nearest("ExperimentalFeatures", Names).Should().Contain("ExperimentalFeature");

    [Fact]
    public void Nearest_EditDistanceTypo_Transposition() =>
        // "StduyInfo" is a transposition of "StudyInfo": edit distance within threshold, not a substring
        NameSuggester.Nearest("StduyInfo", Names).Should().Contain("StudyInfo");

    [Fact]
    public void Nearest_ShortSubstringGate_TwoCharQueryNotTreatedAsSubstring() =>
        // qLen < 3 disables the substring tier; length delta then exceeds the edit threshold → nothing close
        NameSuggester.Nearest("ab", ["xaby"]).Should().BeEmpty();

    [Fact]
    public void Nearest_FarOffQuery_SameLength_ReturnsEmpty() =>
        // same length as a candidate but every character differs → bounded edit-distance rejects it
        NameSuggester.Nearest("aaaaa", ["bbbbb"]).Should().BeEmpty();

    [Fact]
    public void Nearest_FarOffQuery_ReturnsEmpty() =>
        NameSuggester.Nearest("ZZZQWERTY", Names).Should().BeEmpty();

    [Fact]
    public void Nearest_SkipsNullAndEmptyCandidates() =>
        NameSuggester.Nearest("Study", [null!, "", "StudyInfo"]).Should().ContainSingle().Which.Should().Be("StudyInfo");

    [Fact]
    public void Nearest_DeduplicatesRepeatedCandidate()
    {
        IReadOnlyList<string> result = NameSuggester.Nearest("Study", ["StudyInfo", "StudyInfo"]);
        result.Should().ContainSingle().Which.Should().Be("StudyInfo");
    }

    [Fact]
    public void Nearest_ExactRanksAheadOfPrefix()
    {
        IReadOnlyList<string> result = NameSuggester.Nearest("Study", ["StudyInfo", "Study"]);
        result[0].Should().Be("Study");
    }

    [Fact]
    public void Nearest_PrefixRanksAheadOfFuzzy()
    {
        // "Studx" is a prefix-tier match (query prefixes "Studxenon"? no) — use an actual prefix vs a fuzzy candidate
        IReadOnlyList<string> result = NameSuggester.Nearest("Study", ["StudyInfo", "Stydy"]);
        result[0].Should().Be("StudyInfo"); // prefix tier beats the fuzzy "Stydy"
        result.Should().Contain("Stydy");
    }

    [Fact]
    public void Nearest_PrefixTie_OrdersByShorterLengthDelta()
    {
        IReadOnlyList<string> result = NameSuggester.Nearest("Study", ["StudyInfo", "StudyX"]);
        result[0].Should().Be("StudyX"); // smaller length delta ranks first
    }

    [Fact]
    public void Nearest_LongQuery_UsesWiderEditThreshold() =>
        // length > 8 → threshold 3; two substitutions comfortably qualify as fuzzy
        NameSuggester.Nearest("ExperXmentZl", ["Experimental"]).Should().Contain("Experimental");

    [Fact]
    public void Nearest_HonorsMaxAndIsDeterministic()
    {
        string[] many = ["Aaa1", "Aaa2", "Aaa3", "Aaa4", "Aaa5", "Aaa6", "Aaa7"];
        IReadOnlyList<string> r1 = NameSuggester.Nearest("Aaa", many, 5);
        IReadOnlyList<string> r2 = NameSuggester.Nearest("Aaa", many, 5);
        r1.Should().HaveCount(5);
        r2.Should().Equal(r1);
    }

    [Fact]
    public void Nearest_TieBrokenByOrdinalName()
    {
        string[] many = ["Aaa7", "Aaa3", "Aaa1", "Aaa5", "Aaa2", "Aaa6", "Aaa4"];
        IReadOnlyList<string> result = NameSuggester.Nearest("Aaa", many, 5);
        result.Should().Equal("Aaa1", "Aaa2", "Aaa3", "Aaa4", "Aaa5");
    }

    [Fact]
    public void DidYouMean_FormatsSuggestions()
    {
        string result = NameSuggester.DidYouMean("Study", Names);
        result.Should().StartWith("\nDid you mean: ");
        result.Should().Contain("StudyInfo");
        result.Should().EndWith("?");
    }

    [Fact]
    public void DidYouMean_NoMatches_ReturnsEmpty() =>
        NameSuggester.DidYouMean("ZZZQWERTY", Names).Should().BeEmpty();

    [Fact]
    public void DidYouMean_EmptyQuery_ReturnsEmpty() =>
        NameSuggester.DidYouMean("", Names).Should().BeEmpty();
}
