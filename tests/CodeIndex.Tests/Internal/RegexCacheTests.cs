using System.Text.RegularExpressions;
using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>Bounded interpreted-regex LRU (fixes the unbounded Compiled-regex leak).</summary>
public class RegexCacheTests
{
    [Fact]
    public void Get_ReturnsSameInstanceForSameKey()
    {
        Regex a = RegexCache.Get(@"\bCacheUnique_Same\b", RegexOptions.None);
        Regex b = RegexCache.Get(@"\bCacheUnique_Same\b", RegexOptions.None);
        a.Should().BeSameAs(b);
    }

    [Fact]
    public void Get_DistinctInstancesForDifferentOptions()
    {
        Regex a = RegexCache.Get("CacheUnique_Opt", RegexOptions.None);
        Regex b = RegexCache.Get("CacheUnique_Opt", RegexOptions.IgnoreCase);
        a.Should().NotBeSameAs(b);
    }

    [Fact]
    public void Get_ReturnedRegexIsNotCompiled_AndHasTimeout()
    {
        Regex r = RegexCache.Get("CacheUnique_NoCompile", RegexOptions.Compiled); // request Compiled...
        r.Options.HasFlag(RegexOptions.Compiled).Should().BeFalse();              // ...but it is stripped
        r.MatchTimeout.Should().NotBe(Regex.InfiniteMatchTimeout);
        r.MatchTimeout.Should().Be(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Get_StrippingCompiled_PreservesOtherOptions()
    {
        Regex r = RegexCache.Get("CacheUnique_PreserveOpts", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        r.Options.HasFlag(RegexOptions.Compiled).Should().BeFalse();
        r.Options.HasFlag(RegexOptions.IgnoreCase).Should().BeTrue();
    }

    [Fact]
    public void Get_EvictsColdestBeyondCapacity()
    {
        // Insert one distinctive entry, then flood with >128 distinct patterns to evict it, then re-get it and
        // confirm a NEW instance was built (the old one was evicted).
        Regex first = RegexCache.Get("CacheEvict_Anchor", RegexOptions.None);
        for (int i = 0; i < 200; i++)
        {
            RegexCache.Get("CacheEvict_Flood_" + i, RegexOptions.None);
        }
        Regex again = RegexCache.Get("CacheEvict_Anchor", RegexOptions.None);
        again.Should().NotBeSameAs(first);
    }

    [Fact]
    public void Get_RecentlyUsedEntrySurvivesEviction()
    {
        // Touching an entry moves it to the front of the LRU, so it should survive a flood that evicts colder ones.
        Regex anchor = RegexCache.Get("CacheHot_Anchor", RegexOptions.None);
        for (int i = 0; i < 200; i++)
        {
            RegexCache.Get("CacheHot_Flood_" + i, RegexOptions.None);
            RegexCache.Get("CacheHot_Anchor", RegexOptions.None); // keep the anchor hot on every iteration
        }
        Regex again = RegexCache.Get("CacheHot_Anchor", RegexOptions.None);
        again.Should().BeSameAs(anchor);
    }

    [Fact]
    public void Get_InvalidPattern_Throws()
    {
        Action act = () => RegexCache.Get("(unclosed", RegexOptions.None);
        act.Should().Throw<RegexParseException>();
    }

    [Fact]
    public void Get_ConcurrentSameKey_ReturnsSingleSharedInstance()
    {
        // Exercises the thread-safety path (including the second in-lock lookup): many threads racing on the same
        // fresh key must all converge on one cached instance.
        string pattern = "CacheConcurrent_" + Guid.NewGuid().ToString("N");
        Regex[] results = new Regex[64];

        Parallel.For(0, results.Length, i =>
        {
            results[i] = RegexCache.Get(pattern, RegexOptions.None);
        });

        Regex canonical = RegexCache.Get(pattern, RegexOptions.None);
        results.Should().OnlyContain(r => ReferenceEquals(r, canonical));
    }
}
