using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Path containment guard tests: <see cref="PathSecurity.IsWithinRepo"/> allows files inside the repo root and
/// REFUSES arbitrary absolute paths (the previous behavior would read e.g. ~/.aws/credentials). Pure string/path
/// logic — no disk access required.
/// </summary>
public class PathSecurityTests
{
    private static string Root => Path.Combine(Path.GetTempPath(), "CodeIndexSec_Root");

    [Fact]
    public void IsWithinRepo_ReturnsTrue_ForFileInsideRoot()
    {
        string inside = Path.Combine(Root, "Proj", "A.cs");

        PathSecurity.IsWithinRepo(inside, Root).Should().BeTrue();
    }

    [Fact]
    public void IsWithinRepo_ReturnsTrue_ForNestedFileInsideRoot()
    {
        string inside = Path.Combine(Root, "a", "b", "c", "deep.cs");

        PathSecurity.IsWithinRepo(inside, Root).Should().BeTrue();
    }

    [Fact]
    public void IsWithinRepo_ReturnsFalse_ForFileOutsideRoot()
    {
        string outside = Path.Combine(Path.GetTempPath(), "CodeIndexSecret.txt");

        PathSecurity.IsWithinRepo(outside, Root).Should().BeFalse();
    }

    [Fact]
    public void IsWithinRepo_ReturnsFalse_ForNullRoot()
    {
        PathSecurity.IsWithinRepo(Path.Combine(Root, "Proj", "A.cs"), null).Should().BeFalse();
    }

    [Fact]
    public void IsWithinRepo_ReturnsFalse_ForEmptyRoot()
    {
        PathSecurity.IsWithinRepo(Path.Combine(Root, "Proj", "A.cs"), string.Empty).Should().BeFalse();
    }

    [Fact]
    public void IsWithinRepo_ReturnsFalse_ForSiblingPrefixAttack()
    {
        // A dir whose path starts with root's string but isn't under it.
        string sibling = Root + "_evil" + Path.DirectorySeparatorChar + "x.cs";

        PathSecurity.IsWithinRepo(sibling, Root).Should().BeFalse();
    }

    [Fact]
    public void IsWithinRepo_ReturnsFalse_ForRootItself()
    {
        // The root directory itself (no trailing separator) is not "within" the root:
        // containment requires a path strictly under root + separator.
        PathSecurity.IsWithinRepo(Root, Root).Should().BeFalse();
    }

    [Fact]
    public void IsWithinRepo_IsCaseInsensitive()
    {
        string upperRoot = Root.ToUpperInvariant();
        string lowerInside = Path.Combine(Root.ToLowerInvariant(), "proj", "a.cs");

        PathSecurity.IsWithinRepo(lowerInside, upperRoot).Should().BeTrue();
    }

    [Fact]
    public void IsWithinRepo_NormalizesTrailingSeparatorOnRoot()
    {
        string rootWithTrailingSep = Root + Path.DirectorySeparatorChar;
        string inside = Path.Combine(Root, "Proj", "A.cs");

        PathSecurity.IsWithinRepo(inside, rootWithTrailingSep).Should().BeTrue();
    }

    [Fact]
    public void IsWithinRepo_NormalizesRelativeSegmentsInPath()
    {
        // A path that escapes via ".." resolves outside the root and is refused.
        string escaping = Path.Combine(Root, "Proj", "..", "..", "elsewhere.cs");

        PathSecurity.IsWithinRepo(escaping, Root).Should().BeFalse();
    }
}
