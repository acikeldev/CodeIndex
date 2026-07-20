using CodeIndex.Internal;

namespace CodeIndex.Tests.Internal;

/// <summary>
/// Guards the content of the MCP server-instructions playbook: it must name the one-call composites and steer
/// agents off the expensive default chain, since this string is the steering that rides every turn.
/// </summary>
public sealed class ServerPlaybookTests
{
    [Fact]
    public void Instructions_NameTheOneCallComposites()
    {
        ServerPlaybook.Instructions.Should().Contain("explain_symbol");
        ServerPlaybook.Instructions.Should().Contain("prepare_change");
        ServerPlaybook.Instructions.Should().Contain("get_context_bundle");
    }

    [Fact]
    public void Instructions_SteerAwayFromTheChainAndGrep()
    {
        ServerPlaybook.Instructions.Should().Contain("Do NOT hand-run");
        ServerPlaybook.Instructions.Should().Contain("prefer these tools over grep");
        ServerPlaybook.Instructions.Should().Contain("ONE call");
    }

    [Fact]
    public void Instructions_SteerAgainstOverReadAndTowardGrepForPureLiterals()
    {
        // Guards the two anti-loss levers: trust composite output (stop re-opening resolved nodes), and route a
        // pure literal lookup entirely to grep (no schema-load tax).
        ServerPlaybook.Instructions.Should().Contain("TRUST the composite");
        ServerPlaybook.Instructions.Should().Contain("ripgrep IS the whole job");
    }
}
