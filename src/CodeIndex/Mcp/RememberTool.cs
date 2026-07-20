using System.ComponentModel;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class RememberTool
{
    [McpServerTool(Name = "remember")]
    [Description("Persist a short repo fact for FUTURE sessions (e.g. 'auth entry point is AuthController.Login', 'DTOs are generated — edit the .tt, not the .cs'). Notes are appended to get_onboarding and survive restart + reindex, so hard-won orientation is discovered once instead of re-derived every session. Local to this repo's cache; newest kept, deduped, capped.")]
    public static string Remember(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("The fact to remember — one short line")] string note)
    {
        return SessionNotes.Add(fileSystem, index.CacheDirectory, note);
    }
}
