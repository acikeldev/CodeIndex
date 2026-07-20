using System.ComponentModel;
using System.Reflection;
using CodeIndex.Mcp;

namespace CodeIndex.Tests.Infrastructure;

/// <summary>
/// Reflection over the MCP tool surface, shared by the guard tests (prompt-cache hygiene, schema budget,
/// cap-not-paginate). Matches the tool attribute by full name so the test project needn't reference the MCP SDK.
/// </summary>
public static class McpToolSurface
{
    private const string McpServerToolTypeName = "ModelContextProtocol.Server.McpServerToolAttribute";

    /// <summary>Every method annotated as an MCP tool in the CodeIndex assembly.</summary>
    public static IEnumerable<MethodInfo> ToolMethods()
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

    /// <summary>The explicit tool Name set on the <c>[McpServerTool]</c> attribute.</summary>
    public static string? ToolName(MethodInfo method)
    {
        Attribute? attr = method.GetCustomAttributes().FirstOrDefault(a => a.GetType().FullName == McpServerToolTypeName);
        return attr?.GetType().GetProperty("Name")?.GetValue(attr) as string;
    }

    public static string? ToolDescription(MethodInfo method) =>
        method.GetCustomAttribute<DescriptionAttribute>()?.Description;

    public static string? ParameterDescription(ParameterInfo p) =>
        p.GetCustomAttribute<DescriptionAttribute>()?.Description;

    /// <summary>A parameter becomes a JSON-schema property unless the SDK resolves it from DI. The DI params are
    /// the registered services (all interfaces); everything else is caller-supplied schema.</summary>
    public static bool IsSchemaParameter(ParameterInfo p) => !p.ParameterType.IsInterface;
}
