using System.ComponentModel;
using System.Reflection;
using CodeIndex.Mcp;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Tool-surface budget guard. The full tool list (names + descriptions + parameter schemas) rides the cached
/// conversation prefix and is a FIXED per-session tax — paid before the first useful token. The token-efficiency
/// research is clear on the levers here:
///   * $ref schema dedup is a non-lever for this server — every tool takes only primitives (string/int/bool),
///     so there is no repeated complex object schema to deduplicate.
///   * Deferred / lazy tool loading (Claude Code ToolSearch, Pi lazy skills) is a CLIENT capability; a stdio
///     server cannot defer its own schemas.
///   * The descriptions are STEERING — they drive tool selection, and a wrong-tool turn costs far more than the
///     bytes a terser description would save. So the goal is not to shrink them but to KEEP THE SURFACE BOUNDED.
/// These guards fail if the surface bloats: tool count past a cap (the Serena ">20 tools and users hit context
/// limits faster" lesson) or the schema footprint past a ceiling. The sibling <see cref="PromptCacheHygieneTests"/>
/// guards that the surface stays byte-stable.
/// </summary>
public sealed class ToolSchemaBudgetTests
{
    private const string McpServerToolTypeName = "ModelContextProtocol.Server.McpServerToolAttribute";

    private static IEnumerable<MethodInfo> ToolMethods()
    {
        foreach (Type type in typeof(RepoMapTool).Assembly.GetTypes())
        {
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
            {
                if (method.GetCustomAttributes().Any(a => a.GetType().FullName == McpServerToolTypeName))
                {
                    yield return method;
                }
            }
        }
    }

    // DI-injected service params (the registered interfaces) are not schema; everything else is.
    private static bool IsSchemaParameter(ParameterInfo p) => !p.ParameterType.IsInterface;

    // Description + name characters of the whole tool surface — a stable proxy for the schema's token footprint.
    private static int SurfaceChars()
    {
        int chars = 0;
        foreach (MethodInfo tool in ToolMethods())
        {
            chars += (tool.GetCustomAttribute<DescriptionAttribute>()?.Description?.Length ?? 0);
            chars += tool.Name.Length;
            foreach (ParameterInfo p in tool.GetParameters().Where(IsSchemaParameter))
            {
                chars += (p.Name?.Length ?? 0);
                chars += (p.GetCustomAttribute<DescriptionAttribute>()?.Description?.Length ?? 0);
            }
        }

        return chars;
    }

    [Fact]
    public void ToolCount_StaysBounded()
    {
        int count = ToolMethods().Count();
        count.Should().BeLessThanOrEqualTo(24,
            $"the tool surface is a fixed per-session prefix tax and clients degrade past ~20 tools (measured {count}). "
            + "Consolidate into a composite before adding another top-level tool.");
    }

    [Fact]
    public void SchemaFootprint_StaysUnderBudget()
    {
        int chars = SurfaceChars();
        int approxTokens = (int)Math.Ceiling(chars / 4.0);

        // Current footprint is ~2.6k tokens; the ceiling leaves headroom for a few descriptions to grow but trips
        // on real bloat. Raising it is a deliberate decision, not an accident — hence the guard.
        approxTokens.Should().BeLessThanOrEqualTo(3600,
            $"tool-schema footprint (~{approxTokens} tokens over {chars} chars) is paid every session before the first "
            + "answer; keep it lean by folding navigation chains into composites rather than adding tools/params.");
    }
}
