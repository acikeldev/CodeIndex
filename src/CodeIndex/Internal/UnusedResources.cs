using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using CodeIndex.Abstractions;
using CodeIndex.Models;

namespace CodeIndex.Internal;

/// <summary>
/// Resource keys nothing refers to — the dead weight a string table or theme accumulates after screens are
/// removed. Two search_structural patterns:
/// <list type="bullet">
/// <item><c>unused-resx</c>: <c>&lt;data name="X"&gt;</c> keys of every neutral-culture .resx under an indexed
/// project (culture files such as <c>Strings.en.resx</c> carry the same keys and are skipped; so are WinForms form
/// resx files, whose <c>button1.Text</c>-style keys are applied by the designer, not referenced).</item>
/// <item><c>unused-xaml-key</c>: <c>x:Key</c> resources (brushes, styles, templates) of indexed XAML files.</item>
/// </list>
/// "Used" means the key occurs as a whole word in any indexed C# or XAML file outside generated code (so the
/// strongly-typed <c>Strings.Designer.cs</c> accessor does not count, but <c>Strings.X</c>, <c>"X"</c>,
/// <c>{local:Tr X}</c>, <c>{DynamicResource X}</c> and <c>FindResource("X")</c> do); an x:Key's own definitions are
/// not uses. A key whose leading part appears as a string literal start (<c>"Perm_" + id</c>,
/// <c>$"Perm_{id}"</c>) is listed separately as possibly built at runtime rather than reported unused.
/// Conservative by design: a key mentioned only in a comment counts as used.
/// </summary>
internal static partial class UnusedResources
{
    public const string ResxPattern = "unused-resx";
    public const string XamlKeyPattern = "unused-xaml-key";

    private static readonly HashSet<string> PrunedDirNames =
        new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".git", ".vs", "node_modules", ".codeindex" };

    public static bool HasPattern(string pattern) => pattern.Trim() is ResxPattern or XamlKeyPattern;

    public static string PatternHelp() =>
        $"  {ResxPattern} — .resx keys referenced nowhere in C#/XAML (dead UI strings)\n"
        + $"  {XamlKeyPattern} — XAML x:Key resources (brushes, styles, templates) referenced nowhere";

    private readonly record struct KeyDef(string Key, string Path, string Project, int Line);

    public static string Search(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, IReadOnlyDictionary<string, string> projectDirs,
        string pattern, string? project, int max, int perFileCap)
    {
        bool resx = pattern.Trim() == ResxPattern;
        List<KeyDef> defs = resx
            ? ResxKeys(fileSystem, projectDirs, project)
            : XamlKeys(fileSystem, files, project);
        string what = resx ? ".resx" : "XAML x:Key";
        if (defs.Count == 0)
        {
            return $"{pattern}: no {what} keys found{Scope(project)}.";
        }

        Usage usage = CollectUsage(fileSystem, files);
        HashSet<string> unusedKeys = new(StringComparer.Ordinal);
        foreach (KeyDef d in defs)
        {
            if (!usage.Tokens.Contains(d.Key))
            {
                unusedKeys.Add(d.Key);
            }
        }

        Dictionary<string, string> dynamicHint = new(StringComparer.Ordinal);
        foreach (string key in unusedKeys)
        {
            string? prefix = Prefixes(key).FirstOrDefault(usage.QuotedStarts.ContainsKey);
            if (prefix is not null)
            {
                dynamicHint[key] = $"\"{prefix}…\" in {usage.QuotedStarts[prefix]}";
            }
        }

        List<KeyDef> unused = defs.Where(d => unusedKeys.Contains(d.Key) && !dynamicHint.ContainsKey(d.Key)).ToList();
        List<KeyDef> maybeDynamic = defs.Where(d => dynamicHint.ContainsKey(d.Key)).ToList();
        int fileCount = defs.Select(d => d.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        StringBuilder sb = new();
        sb.AppendLine($"{pattern}: {unused.Select(d => d.Key).Distinct().Count()} unused of {defs.Select(d => d.Key).Distinct().Count()} distinct {what} keys in {fileCount} files{Scope(project)}"
            + (maybeDynamic.Count > 0 ? $" (+{maybeDynamic.Select(d => d.Key).Distinct().Count()} possibly built at runtime, listed last)" : string.Empty));
        sb.AppendLine("Used = the key as a whole word in any indexed C#/XAML file outside generated code (a comment mention counts as used).");
        sb.AppendLine();

        int emitted = 0;
        foreach (IGrouping<string, KeyDef> g in unused.GroupBy(d => d.Path, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (emitted >= max)
            {
                break;
            }

            List<KeyDef> keys = g.OrderBy(d => d.Line).ToList();
            sb.AppendLine($"== {GroupedMatchOutput.RelPath(g.Key, keys[0].Project, projectDirs)} — {keys.Count} unused ==");
            foreach (KeyDef d in keys.Take(perFileCap))
            {
                if (emitted >= max)
                {
                    break;
                }

                sb.AppendLine($"  {d.Line}| {d.Key}");
                emitted++;
            }

            if (keys.Count > perFileCap)
            {
                sb.AppendLine($"  … {keys.Count - perFileCap} more in this file (raise perFileMax).");
            }
        }

        if (maybeDynamic.Count > 0 && emitted < max)
        {
            sb.AppendLine();
            sb.AppendLine("== possibly built at runtime (a string literal starts with the key's leading part) — check before removing ==");
            foreach (KeyDef d in maybeDynamic.DistinctBy(d => d.Key, StringComparer.Ordinal).OrderBy(d => d.Key, StringComparer.Ordinal).Take(Math.Max(0, max - emitted)))
            {
                sb.AppendLine($"  {Path.GetFileName(d.Path)}:{d.Line}| {d.Key}  ← {dynamicHint[d.Key]}");
            }
        }

        if (unused.Count == 0 && maybeDynamic.Count == 0)
        {
            sb.AppendLine("  (every key is referenced)");
        }

        return sb.ToString();
    }

    // ── Definitions ──────────────────────────────────────────────────────────────────────────────────────

    private static List<KeyDef> ResxKeys(IFileSystem fileSystem, IReadOnlyDictionary<string, string> projectDirs, string? project)
    {
        List<KeyDef> defs = new();
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach ((string name, string dir) in projectDirs)
        {
            if (project is not null && !name.Equals(project, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (string path in FilesUnder(fileSystem, dir, ".resx"))
            {
                if (!seen.Add(path) || IsCultureSpecific(path) || IsFormResx(fileSystem, path))
                {
                    continue;
                }

                XDocument doc;
                try
                {
                    doc = XDocument.Parse(fileSystem.ReadAllText(path), LoadOptions.SetLineInfo);
                }
                catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
                {
                    continue; // unreadable / malformed resx — nothing to report from it
                }

                foreach (XElement data in doc.Root?.Elements("data") ?? [])
                {
                    string? key = data.Attribute("name")?.Value;
                    if (key is null || !Identifier().IsMatch(key))
                    {
                        continue; // designer metadata (">>button1.Name", "$this.Text") or non-identifier keys
                    }

                    defs.Add(new KeyDef(key, path, name, ((IXmlLineInfo)data).LineNumber));
                }
            }
        }

        return defs;
    }

    private static List<KeyDef> XamlKeys(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files, string? project)
    {
        List<KeyDef> defs = new();
        FileReader reader = new(fileSystem);
        foreach (SourceFileIndex f in files.Where(f => f.Language == Language.Xaml)
            .Where(f => project is null || f.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(f => f.SourceFilePath, StringComparer.OrdinalIgnoreCase))
        {
            FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
            if (!read.Success)
            {
                continue;
            }

            for (int i = 0; i < read.Lines!.Length; i++)
            {
                foreach (Match m in XKey().Matches(read.Lines[i]))
                {
                    string key = m.Groups[1].Value;
                    if (Identifier().IsMatch(key))
                    {
                        defs.Add(new KeyDef(key, f.SourceFilePath, f.ProjectName, i + 1));
                    }
                }
            }
        }

        return defs;
    }

    // ── Usage corpus ─────────────────────────────────────────────────────────────────────────────────────

    private sealed record Usage(HashSet<string> Tokens, Dictionary<string, string> QuotedStarts);

    // Every identifier token of every indexed, non-generated C#/XAML file (x:Key definitions removed), plus the
    // identifier that opens a string literal ("Perm_" + id → "Perm_") with the first file it was seen in.
    private static Usage CollectUsage(IFileSystem fileSystem, IReadOnlyList<SourceFileIndex> files)
    {
        ConcurrentDictionary<string, byte> tokens = new(StringComparer.Ordinal);
        ConcurrentDictionary<string, string> quoted = new(StringComparer.Ordinal);
        FileReader reader = new(fileSystem);
        Parallel.ForEach(files.Where(f => f.Language is Language.CSharp or Language.Xaml)
            .Where(f => !GeneratedFileClassifier.IsGenerated(f.SourceFilePath))
            .DistinctBy(f => f.SourceFilePath, StringComparer.OrdinalIgnoreCase), f =>
        {
            FileReader.ReadResult read = reader.ReadFileLines(f.SourceFilePath);
            if (!read.Success)
            {
                return;
            }

            foreach (string raw in read.Lines!)
            {
                string line = f.Language == Language.Xaml ? XKey().Replace(raw, string.Empty) : raw;
                foreach (Match m in Token().Matches(line))
                {
                    tokens.TryAdd(m.Value, 0);
                }

                foreach (Match m in QuotedStart().Matches(line))
                {
                    quoted.TryAdd(m.Groups[1].Value, f.FileName);
                }
            }
        });

        return new Usage(new HashSet<string>(tokens.Keys, StringComparer.Ordinal), new Dictionary<string, string>(quoted, StringComparer.Ordinal));
    }

    // Leading parts a runtime-built key could be assembled from: "Perm_Edit2" → "Perm_Edit", "Perm_". A bare word
    // before the separator ("Perm") is not one — it is far too often an ordinary string ("Save" vs "Save_Title").
    internal static IEnumerable<string> Prefixes(string key)
    {
        string noDigits = key.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        if (noDigits.Length > 0 && noDigits.Length < key.Length)
        {
            yield return noDigits;
        }

        for (int i = key.LastIndexOf('_'); i > 0; i = i > 1 ? key.LastIndexOf('_', i - 1) : -1)
        {
            yield return key[..(i + 1)];
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────

    private static IEnumerable<string> FilesUnder(IFileSystem fileSystem, string root, string extension)
    {
        Stack<string> stack = new();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            List<FileSystemEntry> entries;
            try
            {
                entries = fileSystem.EnumerateDirectoryEntries(dir).ToList();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or DirectoryNotFoundException or IOException)
            {
                continue; // inaccessible subtree — skip it, keep the rest
            }

            foreach (FileSystemEntry e in entries)
            {
                if (e.IsDirectory)
                {
                    if (!PrunedDirNames.Contains(e.Name) && !e.IsReparsePoint)
                    {
                        stack.Push(e.FullPath);
                    }
                }
                else if (e.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                {
                    yield return e.FullPath;
                }
            }
        }
    }

    // "Strings.en.resx", "Strings.pt-BR.resx" — a culture satellite of "Strings.resx".
    internal static bool IsCultureSpecific(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        int dot = name.LastIndexOf('.');
        return dot > 0 && CultureSuffix().IsMatch(name[(dot + 1)..]);
    }

    // A WinForms form/control resx sits next to its "<Name>.cs"; a string table has only "<Name>.Designer.cs".
    private static bool IsFormResx(IFileSystem fileSystem, string path) =>
        fileSystem.FileExists(Path.ChangeExtension(path, ".cs"));

    private static string Scope(string? project) => project is null ? string.Empty : $" in project '{project}'";

    [GeneratedRegex(@"^[\p{L}_][\p{L}\p{Nd}_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();

    [GeneratedRegex(@"[\p{L}_][\p{L}\p{Nd}_]*", RegexOptions.CultureInvariant)]
    private static partial Regex Token();

    [GeneratedRegex(@"[""']([\p{L}_][\p{L}\p{Nd}_]*)", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedStart();

    [GeneratedRegex(@"\bx:Key\s*=\s*""([^""]*)""", RegexOptions.CultureInvariant)]
    private static partial Regex XKey();

    [GeneratedRegex(@"^[a-z]{2,3}(-[A-Za-z0-9]{2,8})*$", RegexOptions.CultureInvariant)]
    private static partial Regex CultureSuffix();
}
