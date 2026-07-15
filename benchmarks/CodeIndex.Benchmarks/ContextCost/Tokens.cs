namespace CodeIndex.Benchmarks.ContextCost;

/// <summary>
/// Token estimator used for BOTH sides of the context-cost comparison. <c>ceil(chars / 4)</c> is
/// the widely-used rule-of-thumb for English + code, and is exactly the estimator CodeIndex uses
/// internally (<c>Output.EstimateTokens</c>).
///
/// Because it is applied identically to the baseline and to CodeIndex, the REDUCTION PERCENTAGE is
/// robust to the exact tokenizer: swap in a real BPE tokenizer and the ratio barely moves. The
/// absolute token counts are approximate; the percentages are the point.
/// </summary>
internal static class Tokens
{
    public static int Estimate(string text) =>
        string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);
}
