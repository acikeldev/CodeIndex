using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

/// <summary>
/// Task-conditioned orientation: given a free-text task, anchor it to real repo types (by term match), then
/// return PageRank focused on those anchors + a dossier for the strongest one, on top of the cached repo
/// onboarding. Retrieval is best-effort and local (no embeddings), so it is GATED: when the task can't be
/// anchored to real symbols it says so and returns plain orientation rather than a confident-but-wrong guess —
/// a miss must never dump dead context as if it were relevant. Reuses <see cref="GetOnboardingTool"/> as the
/// repo-level base (that cache is what this tool's cache-hit/miss behaviour exercises).
/// </summary>
[McpServerToolType]
public static class GetTaskContextTool
{
    private const int TaskRepoMapBudget = 1500;
    private const int MaxAnchors = 6;
    private const int MinTermLength = 4;

    // Common task-verb / filler words that would over-match type names if used as anchors.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "this", "that", "with", "into", "from", "make", "when", "then", "also", "your", "code", "file",
        "test", "class", "have", "will", "need", "want", "them", "they", "some", "only", "using", "added",
        "should", "would", "could", "where", "which", "what", "about", "there", "their", "these", "those",
    };

    [McpServerTool(Name = "get_task_context")]
    [Description("Task-conditioned orientation for the START of an unfamiliar task: given a free-text task, returns the repo-relevant symbols (PageRank focused on the task's terms) + a dossier for the strongest anchor, on top of the cached repo onboarding. Best-effort LOCAL retrieval (no embeddings): when it can't anchor the task to real symbols it says so and returns plain orientation rather than a confident guess.")]
    public static string GetTaskContext(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("Free-text task description, e.g. 'add rate limiting to the login endpoint'")] string task)
    {
        string repoBase = GetOnboardingTool.GetOnboarding(index, fileSystem);
        List<string> anchors = MatchAnchors(index, task);

        if (anchors.Count == 0)
        {
            // Confidence gate: nothing matched. Return plain orientation, clearly labelled — never a wrong guess.
            return $"# Task context (low confidence)\n"
                + $"Couldn't anchor '{task}' to specific repo symbols — here is general orientation instead:\n\n"
                + repoBase;
        }

        StringBuilder sb = new();
        sb.AppendLine($"# Task context for: {task}");
        sb.AppendLine($"Anchored on: {string.Join(", ", anchors)}");
        sb.AppendLine();
        sb.AppendLine("## Task-relevant symbols");
        sb.AppendLine(index.GetRepoMap(anchors, TaskRepoMapBudget, null).TrimEnd());
        sb.AppendLine();
        sb.AppendLine($"## Anchor dossier: {anchors[0]}");
        sb.AppendLine(ExplainSymbolTool.ExplainSymbol(index, fileSystem, anchors[0]).TrimEnd());
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("## Repo at a glance");
        sb.AppendLine(repoBase.TrimEnd());
        return sb.ToString();
    }

    // Term-match the task against real TYPE names (the strongest anchors). No match -> low confidence.
    private static List<string> MatchAnchors(ICodeIndexStore index, string task)
    {
        HashSet<string> terms = new(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in task.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            string term = new(raw.Where(char.IsLetterOrDigit).ToArray());
            if (term.Length >= MinTermLength && !StopWords.Contains(term))
            {
                terms.Add(term);
            }
        }

        List<string> anchors = new();
        if (terms.Count == 0)
        {
            return anchors;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in index.TypeNames())
        {
            bool hit = false;
            foreach (string term in terms)
            {
                if (name.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }

            if (hit && seen.Add(name))
            {
                anchors.Add(name);
                if (anchors.Count >= MaxAnchors)
                {
                    break;
                }
            }
        }

        return anchors;
    }
}
