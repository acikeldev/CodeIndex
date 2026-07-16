using MessagePack;

namespace CodeIndex.Models;

[MessagePackObject]
public sealed class SourceFileIndex
{
    [Key(0)] public required string FileName { get; init; }
    [Key(1)] public required string SourceFilePath { get; init; }
    [Key(2)] public required string ProjectName { get; init; }
    [Key(3)] public string? Namespace { get; init; }
    // init (not set): a published snapshot's model graph must never be reassigned/mutated — the lock-free readers
    // depend on it. MessagePack populates init setters during deserialization.
    [Key(4)] public List<TypeInfo> Types { get; init; } = [];

    // For resolve_bare_name: what this file imports. Usings = plain `using X;` namespaces (incl. global usings);
    // UsingAliases = `using Alias = Fully.Qualified.Type;` (alias -> target). Captured syntactically by the parser.
    [Key(5)] public List<string> Usings { get; init; } = [];
    [Key(6)] public Dictionary<string, string> UsingAliases { get; init; } = [];

    // Which language this file was parsed from. Default CSharp (=0) so old v4 cache rows — which only ever
    // hold C# files — deserialize correctly with NO cache-schema bump. Lets C#-only readers scope out TS/SCSS.
    [Key(7)] public Language Language { get; init; } = Language.CSharp;

    // Runtime-wiring sites captured syntactically from this file (DI registrations, service-locator consumption,
    // legacy construction, reflective targets). Null when the file has none — most files. The solution-wide
    // interface->implementation closure is a derived, never-serialized graph built over these across all files.
    [Key(8)] public List<RuntimeRegistration>? Registrations { get; init; }

    // SITE-based framework roots originating in this file (e.g. `new ServiceHost(typeof(X))` marking X a root).
    // Declaration-local roots live as bitflags on TypeInfo/MemberInfo instead. Null when the file has none.
    [Key(9)] public List<FrameworkRootMark>? FrameworkRoots { get; init; }
}
