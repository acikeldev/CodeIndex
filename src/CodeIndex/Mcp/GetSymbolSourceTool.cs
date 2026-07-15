using System.ComponentModel;
using System.Text;
using CodeIndex.Abstractions;
using CodeIndex.Internal;
using ModelContextProtocol.Server;

namespace CodeIndex.Mcp;

[McpServerToolType]
public static class GetSymbolSourceTool
{
    // Default window when a caller reads by line without giving a length.
    private const int DefaultWindowLines = 40;

    [McpServerTool(Name = "get_symbol_source")]
    [Description("Read source for a member (or type) BY NAME via member= — the line range is looked up from the index, so you do NOT need line numbers — or a raw line window via startLine+lineCount. Only indexed or in-repo files can be read. Pass type=/namespace=/project= to disambiguate a shared member name; for overloads, copy an exact startLine+lineCount from the ambiguity list.")]
    public static string GetSymbolSource(
        ICodeIndexStore index,
        IFileSystem fileSystem,
        [Description("File path or filename. Required for the line-window mode; an optional scope filter when member= is set.")] string? file = null,
        [Description("Start line (1-based). Line-window mode; ignored when member= is set.")] int? startLine = null,
        [Description("Lines to read (defaults to a small window). Line-window mode.")] int? lineCount = null,
        [Description("Member (or type) name to resolve from the index; its line range is looked up automatically.")] string? member = null,
        [Description("Enclosing type name to disambiguate member=.")] string? type = null,
        [Description("Namespace to disambiguate member= (full or trailing segment).")] string? @namespace = null,
        [Description("Project to disambiguate member=.")] string? project = null)
    {
        if (member is not null)
        {
            MemberResolver.MemberLocation? loc = MemberResolver.Resolve(index, member, file, type, @namespace, project, startLine, out string? memberError);
            if (loc is null)
            {
                return memberError!;
            }

            // A member renders as "Type.signature"; a whole-type fallback (empty TypeName) renders as "class Foo".
            string qualified = string.IsNullOrEmpty(loc.TypeName) ? loc.Signature : $"{loc.TypeName}.{loc.Signature}";
            IReadOnlyDictionary<string, string> projectDirs = index.ProjectDirsByName();
            string header = $"# {qualified} [{GroupedMatchOutput.RelPath(loc.SourceFilePath, loc.Project, projectDirs)}:{loc.StartLine}+{loc.LineCount}] ({loc.Project})";
            return header + Environment.NewLine + ReadWindow(index, fileSystem, loc.SourceFilePath, loc.StartLine, loc.LineCount);
        }

        if (file is null || startLine is null)
        {
            return "Provide member= (resolve source by name) or file= + startLine (raw line window).";
        }

        return ReadWindow(index, fileSystem, file, startLine.Value, lineCount ?? DefaultWindowLines);
    }

    // Filename resolution + path-containment guard + gutter read. Extracted unchanged so every existing positional
    // caller (SpeculativeAppendix, DossierBuilder, the tests) stays byte-identical.
    private static string ReadWindow(ICodeIndexStore index, IFileSystem fileSystem, string file, int startLine, int lineCount)
    {
        string filePath = file;
        if (!Path.IsPathRooted(file) || !fileSystem.FileExists(file))
        {
            string? resolved = index.ResolveSourceFilePath(file);
            if (resolved is not null)
            {
                filePath = resolved;
            }
        }

        // Security: the arg comes from an agent (possibly prompt-injected). Only read a file that is INDEXED, or
        // one that resolves to a path INSIDE the repo root — never an arbitrary absolute path like ~/.aws/credentials.
        string full = Path.GetFullPath(filePath);
        bool allowed = index.IsIndexedPath(full) || index.IsIndexedPath(filePath) || PathSecurity.IsWithinRepo(full, index.RepoRoot);
        if (!allowed)
        {
            return $"Refused: '{file}' is not an indexed file and is outside the repository. Pass a filename or path from search results.";
        }

        if (!fileSystem.FileExists(full))
        {
            return $"Source file not found: {full}";
        }

        string[] allLines = fileSystem.ReadAllLines(full);
        int start = Math.Max(0, startLine - 1); // convert to 0-based
        if (start >= allLines.Length)
        {
            return $"Start line {startLine} is beyond file length ({allLines.Length} lines).";
        }

        int count = Math.Min(lineCount, allLines.Length - start);
        StringBuilder sb = new();
        for (int i = start; i < start + count; i++)
        {
            sb.AppendLine($"{i + 1,5}| {allLines[i]}");
        }

        return sb.ToString();
    }

    // NOTE: member-mode results are NOT covered here (AMBIGUOUS / "Member ... not found" / "too large" / the usage
    // guidance, and a stale-index sentinel that trails the header). The only callers that embed a result AS source
    // (DossierBuilder, SpeculativeAppendix) always call in line-window mode, so they never see those. Any future
    // caller that uses member= mode internally must not feed the result to IsErrorResult / embed it as code.
    // The non-source sentinels GetSymbolSource can return (refused path / missing file / start-past-EOF). Callers
    // that embed the result AS source — the dossiers and the speculative appendix — must check this first so an
    // error string is never presented as code (e.g. on a stale index after the file shrank or was deleted).
    internal static bool IsErrorResult(string result) =>
        result.StartsWith("Refused:", StringComparison.Ordinal)
        || result.StartsWith("Source file not found:", StringComparison.Ordinal)
        || result.StartsWith("Start line ", StringComparison.Ordinal);
}
