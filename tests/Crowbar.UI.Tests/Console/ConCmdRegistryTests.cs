using Crowbar.Engine;
using Crowbar.Engine.Global;

namespace Crowbar.Console.Tests;

[Collection("Console")]
public class ConCmdRegistryTests
{
    private static Log LogInstance => GlobalNamespaces.Log;

    public ConCmdRegistryTests()
    {
        LogInstance.Clear();
        Crowbar.Engine.Global.ConCmdRegistry.Discover(typeof(TestCommands).Assembly);
    }

    [Fact]
    public void Help_ReturnsCommandListAsDetails()
    {
        var prevCount = LogInstance.Entries.Count;
        // help does not log on its own: it returns the command list as
        // sub-lines so the console attaches them to the echoed command line.
        var details = Crowbar.Engine.Global.ConCmdRegistry.Execute("help");
        Assert.Empty(LogInstance.Entries.Skip(prevCount));
        Assert.NotNull(details);
        Assert.Contains(details!, d => d.Contains("help"));
        Assert.Contains(details!, d => d.Contains("test_simple"));
    }

    [Fact]
    public void Execute_MethodWithoutArgs_Invokes()
    {
        TestCommands.SimpleCalled = false;
        Crowbar.Engine.Global.ConCmdRegistry.Execute("test_simple");
        Assert.True(TestCommands.SimpleCalled);
    }

    [Fact]
    public void Execute_MethodWithArgs_ReceivesTokens()
    {
        TestCommands.LastArgs = null;
        Crowbar.Engine.Global.ConCmdRegistry.Execute("test_args hello world");
        Assert.NotNull(TestCommands.LastArgs);
        Assert.Equal(["hello", "world"], TestCommands.LastArgs!);
    }

    [Fact]
    public void Execute_UnknownCommand_LogsWarning()
    {
        var prevCount = LogInstance.Entries.Count;
        Crowbar.Engine.Global.ConCmdRegistry.Execute("nonexistent_cmd");
        var after = LogInstance.Entries;
        var newEntries = after.Skip(prevCount).ToList();
        Assert.Contains(newEntries, e => e.Message.Contains("Unknown command") && e.Level == LogLevel.Warning);
    }

    [Fact]
    public void Execute_EmptyInput_DoesNothing()
    {
        var prevCount = LogInstance.Entries.Count;
        Crowbar.Engine.Global.ConCmdRegistry.Execute("");
        Crowbar.Engine.Global.ConCmdRegistry.Execute("   ");
        Crowbar.Engine.Global.ConCmdRegistry.Execute("// comment");
        Assert.Equal(prevCount, LogInstance.Entries.Count);
    }

    [Fact]
    public void GetSuggestions_ReturnsMatchingCommands()
    {
        var suggestions = Crowbar.Engine.Global.ConCmdRegistry.GetSuggestions("test_");
        Assert.Contains("test_simple", suggestions);
        Assert.Contains("test_args", suggestions);
    }
}

/// <summary>Test commands registered by <see cref="ConCmdRegistry.Discover"/>.</summary>
public static class TestCommands
{
    public static bool SimpleCalled;
    public static string[]? LastArgs;

    [ConCmd("test_simple", "A no-arg test command.")]
    public static void TestSimple() => SimpleCalled = true;

    [ConCmd("test_args", "A test command with arguments.")]
    public static void TestArgs(string[] args) => LastArgs = args;
}