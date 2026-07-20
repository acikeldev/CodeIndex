using System.ComponentModel;
using System.Reflection;
using System.Text.RegularExpressions;
using CodeIndex.Internal;
using CodeIndex.Mcp;

namespace CodeIndex.Tests.Mcp;

/// <summary>
/// Prompt-cache hygiene guard. The MCP client caches the conversation PREFIX (tools -> system -> messages) and
/// re-bills it every turn at cache rates (~0.1x). Any byte change in the tool schemas or the server-instructions
/// rider invalidates that whole prefix, so both must be machine- and time-independent (byte-stable across
/// restarts). Attribute-based tool descriptions and a <c>const</c> playbook make this structurally true today;
/// these tests keep it true — they fail the moment someone inlines an absolute path, a live count, or otherwise
/// makes the cached prefix vary. They also enforce schema completeness (every tool + schema parameter documented),
/// which improves tool selection and cuts wrong-tool turns.
/// </summary>
public sealed class PromptCacheHygieneTests
{
    // Absolute-filesystem-path markers. These are the realistic way machine-specific text leaks into a schema
    // (someone hardcodes their repo path as an example). Deliberately NOT matching bare years/numbers — real
    // descriptions legitimately say "default 2000, max 20000".
    private static readonly Regex VolatileMarker = new(
        @"[A-Za-z]:\\|/home/|/Users/|/root/|/mnt/|(?<![A-Za-z])/c/",
        RegexOptions.Compiled);

    private const string McpServerToolTypeName = "ModelContextProtocol.Server.McpServerToolAttribute";

    private static IEnumerable<MethodInfo> ToolMethods()
    {
        Assembly asm = typeof(RepoMapTool).Assembly;
        foreach (Type type in asm.GetTypes())
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

    private static string? ToolName(MethodInfo method)
    {
        Attribute? attr = method.GetCustomAttributes().FirstOrDefault(a => a.GetType().FullName == McpServerToolTypeName);
        return attr?.GetType().GetProperty("Name")?.GetValue(attr) as string;
    }

    // A parameter becomes a JSON-schema property unless the SDK resolves it from DI. The DI params here are the
    // registered services (IFileSystem, ICodeIndexStore, ICodeIndexCache) — all interfaces. Everything else
    // (string/int/bool/enum + their nullables) is caller-supplied schema and must be documented.
    private static bool IsSchemaParameter(ParameterInfo p) => !p.ParameterType.IsInterface;

    [Fact]
    public void EveryTool_HasAStableExplicitNameAndDescription()
    {
        List<MethodInfo> tools = ToolMethods().ToList();
        tools.Should().NotBeEmpty("the assembly must expose MCP tools");
        tools.Count.Should().BeGreaterThanOrEqualTo(20, "the known tool surface is 20 tools");

        foreach (MethodInfo tool in tools)
        {
            string? name = ToolName(tool);
            name.Should().NotBeNullOrWhiteSpace(
                $"{tool.DeclaringType?.Name}.{tool.Name} must set an explicit stable tool Name (a method rename must not silently change the schema)");

            string? description = tool.GetCustomAttribute<DescriptionAttribute>()?.Description;
            description.Should().NotBeNullOrWhiteSpace($"tool '{name}' must carry a [Description] — it is part of the cached tool schema");
        }
    }

    [Fact]
    public void EverySchemaParameter_IsDocumented()
    {
        foreach (MethodInfo tool in ToolMethods())
        {
            string name = ToolName(tool) ?? tool.Name;
            foreach (ParameterInfo p in tool.GetParameters().Where(IsSchemaParameter))
            {
                string? desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
                desc.Should().NotBeNullOrWhiteSpace(
                    $"parameter '{p.Name}' of tool '{name}' is a schema property and must be documented for reliable tool selection");
            }
        }
    }

    [Fact]
    public void NoToolSchema_ContainsMachineSpecificPaths()
    {
        foreach (MethodInfo tool in ToolMethods())
        {
            string name = ToolName(tool) ?? tool.Name;

            string? toolDesc = tool.GetCustomAttribute<DescriptionAttribute>()?.Description;
            if (toolDesc is not null)
            {
                VolatileMarker.IsMatch(toolDesc).Should().BeFalse(
                    $"tool '{name}' description must not embed an absolute path — it would make the cached tool schema machine-specific");
            }

            foreach (ParameterInfo p in tool.GetParameters())
            {
                string? desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description;
                if (desc is not null)
                {
                    VolatileMarker.IsMatch(desc).Should().BeFalse(
                        $"parameter '{p.Name}' of tool '{name}' must not embed an absolute path in its schema description");
                }
            }
        }
    }

    [Fact]
    public void ServerInstructions_AreMachineAndTimeIndependent()
    {
        // The playbook rides the system prompt on every turn at cache rates. If it embedded a live count, a repo
        // path, or a timestamp it would differ across restarts and bust the whole prefix cache every time.
        VolatileMarker.IsMatch(ServerPlaybook.Instructions).Should().BeFalse(
            "ServerInstructions must not embed a machine-specific path");
        ServerPlaybook.Instructions.Should().NotContain("DateTime");
        ServerPlaybook.Instructions.Should().NotContainEquivalentOf("Guid.New");
    }
}
