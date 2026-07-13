namespace CodeIndex.Internal;

/// <summary>
/// Success-path "next step" footers. Most tools only hinted a next step on the MISS path, so an agent that got a
/// result had no nudge toward the one-call composite and defaulted to the expensive chain
/// (search → members → source → refs). These footers are a few tokens each; the client re-bills them at cache
/// rates over the remaining turns, which is far cheaper than the extra round-trip a chain would cost.
///
/// They live only on the two chain-ENTRY tools that the dossier tools do NOT compose (search_symbol,
/// get_file_outline) — footering a composed tool (get_type_members, find_references, …) would leak the hint into
/// explain_symbol / prepare_change output.
/// </summary>
internal static class Steering
{
    public const string SymbolDossierHint =
        "\n\nTip: for this symbol's members + source + references in ONE call, use explain_symbol; to scope an edit, prepare_change.";

    public const string OutlineDossierHint =
        "\n\nTip: for one type's members + source + references in a single call, use explain_symbol (prepare_change before an edit).";
}
