using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>Covers the output-shaping helpers: token estimation and match-line clipping.</summary>
public class OutputTests
{
    // ----- EstimateTokens -----

    [Fact]
    public void EstimateTokens_EmptyStringIsZero()
    {
        Output.EstimateTokens(string.Empty).Should().Be(0);
    }

    [Theory]
    [InlineData(1, 1)]   // ceil(1/4) = 1
    [InlineData(4, 1)]   // ceil(4/4) = 1
    [InlineData(5, 2)]   // ceil(5/4) = 2
    [InlineData(8, 2)]   // ceil(8/4) = 2
    [InlineData(9, 3)]   // ceil(9/4) = 3
    public void EstimateTokens_RoundsUpCharsOverFour(int length, int expected)
    {
        Output.EstimateTokens(new string('a', length)).Should().Be(expected);
    }

    // ----- ClipLine: short line -----

    [Fact]
    public void ClipLine_ShortLineUnchanged()
    {
        Output.ClipLine("short", "hor", 200).Should().Be("short");
    }

    [Fact]
    public void ClipLine_LineExactlyAtMaxUnchanged()
    {
        string line = new('a', 100);
        Output.ClipLine(line, null, 100).Should().Be(line);
    }

    // ----- ClipLine: no focus / focus not found -----

    [Fact]
    public void ClipLine_LongLineNoFocusKeepsHead()
    {
        string line = new('x', 500);
        string clipped = Output.ClipLine(line, null, 100);

        clipped.Length.Should().BeLessThanOrEqualTo(104); // 100 + " …"
        clipped.Should().EndWith("…");
        clipped.Should().StartWith(new string('x', 100));
    }

    [Fact]
    public void ClipLine_LongLineFocusAbsentKeepsHead()
    {
        // focus is non-null but does not occur in the line -> IndexOf returns -1 -> head kept.
        string line = new('y', 500);
        string clipped = Output.ClipLine(line, "NOTHERE", 100);

        clipped.Should().Be(new string('y', 100) + " …");
    }

    // ----- ClipLine: focus found, centred -----

    [Fact]
    public void ClipLine_LongLineIsClippedAndKeepsFocus()
    {
        string line = new string('a', 300) + "NEEDLE" + new string('b', 300);
        string clipped = Output.ClipLine(line, "NEEDLE", 100);

        clipped.Length.Should().BeLessThan(line.Length);
        clipped.Should().Contain("NEEDLE");
        clipped.Should().Contain("…");
    }

    [Fact]
    public void ClipLine_FocusInMiddleHasBothEllipses()
    {
        // focus far from both ends -> a leading "… " and trailing " …".
        string line = new string('a', 300) + "NEEDLE" + new string('b', 300);
        string clipped = Output.ClipLine(line, "NEEDLE", 100);

        clipped.Should().StartWith("… ");
        clipped.Should().EndWith(" …");
    }

    [Fact]
    public void ClipLine_FocusNearStartHasNoLeadingEllipsis()
    {
        // focus at index 0 -> start clamps to 0 -> no prefix, but tail is cut so suffix present.
        string line = "NEEDLE" + new string('b', 300);
        string clipped = Output.ClipLine(line, "NEEDLE", 100);

        clipped.Should().StartWith("NEEDLE");
        clipped.Should().NotStartWith("… ");
        clipped.Should().EndWith(" …");
    }

    [Fact]
    public void ClipLine_FocusNearEndHasNoTrailingEllipsis()
    {
        // focus near the end -> end clamps to line.Length, start re-expands left -> prefix present, no suffix.
        string line = new string('a', 300) + "NEEDLE";
        string clipped = Output.ClipLine(line, "NEEDLE", 100);

        clipped.Should().StartWith("… ");
        clipped.Should().EndWith("NEEDLE");
        clipped.Should().NotEndWith(" …");
    }

    [Fact]
    public void ClipLine_ClippedWindowNeverExceedsMaxPlusEllipses()
    {
        string line = new string('a', 300) + "NEEDLE" + new string('b', 300);
        string clipped = Output.ClipLine(line, "NEEDLE", 100);

        // window is at most `max` chars, plus a 2-char prefix and 2-char suffix.
        clipped.Length.Should().BeLessThanOrEqualTo(104);
    }
}
