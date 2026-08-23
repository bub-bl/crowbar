using Crowbar.Engine;
using Crowbar.Engine.Global;

namespace Crowbar.Editor;

/// <summary>
/// Demo console commands exercising the <c>[ConCmd]</c> API. Discovered by
/// <see cref="ConCmdRegistry.Discover(System.Reflection.Assembly)"/> on the
/// editor assembly at startup; type <c>help</c> in the console to list them.
/// </summary>
public static class ConsoleTestCommands
{
    /// <summary>Prints its arguments back to the console.</summary>
    [ConCmd("echo", "Prints its arguments back to the console.")]
    public static void Echo(string[] args) => GlobalNamespaces.Log.Info(string.Join(" ", args));

    /// <summary>Adds two integers and prints the result.</summary>
    [ConCmd("sum", "Adds two integers and prints the result (usage: sum a b).")]
    public static void Sum(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[0], out var a) || !int.TryParse(args[1], out var b))
        {
            GlobalNamespaces.Log.Warn("Usage: sum &lt;a&gt; &lt;b&gt; (two integers).");
            return;
        }
        GlobalNamespaces.Log.Info($"{a} + {b} = {a + b}");
    }

    /// <summary>Prints the current wall-clock time.</summary>
    [ConCmd("time", "Prints the current wall-clock time.")]
    public static void Time() => GlobalNamespaces.Log.Info($"Current time: {DateTime.Now:HH:mm:ss}");

    /// <summary>Prints a random integer between 0 and 99.</summary>
    [ConCmd("random", "Prints a random integer between 0 and 99.")]
    public static void Random() => GlobalNamespaces.Log.Info($"Random value: {System.Random.Shared.Next(100)}");
}
