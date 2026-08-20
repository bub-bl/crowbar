using Crowbar.Engine.Scripting;

namespace Crowbar.UI.Tests.Scripting;

/// <summary>Engine-side holder through which a script object is reachable (the "world" side of the fence).</summary>
public sealed class PlayerHolder
{
    public object? Current;
}

public sealed class ScriptHotReloadTests
{
    [Fact]
    public void CompileAndRun()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Game.cs", """
            using Crowbar.Engine;
            namespace Game;
            public class Calculator
            {
                public int Add(int a, int b) => a + b;
                public TickGroup Group() => TickGroup.PostUpdate;
            }
            """);

        var compiler = new ScriptCompiler([typeof(ScriptHost).Assembly]);
        using var assembly = compiler.CompileDirectory(dir.Path, "CalcGame");

        var calc = assembly.CreateInstance("Game.Calculator")!;
        Assert.Equal(5, (int)calc.GetType().GetMethod("Add")!.Invoke(calc, [2, 3])!);
        // The script can reference engine types (the engine assembly is a reference).
        Assert.Equal("PostUpdate", calc.GetType().GetMethod("Group")!.Invoke(calc, null)!.ToString());
    }

    [Fact]
    public void ImplicitUsingsMatchSdkLibraryProject()
    {
        // The game project is a real library project with ImplicitUsings enabled; the
        // in-memory compiler must expose the SDK's default global usings (here:
        // System.Linq) so code that builds in the project also hot-reloads.
        using var dir = TestUi.TempDir("script");
        dir.Write("Game.cs", """
            using Crowbar.Engine.Scripting;
            namespace Game;
            public class Summing
            {
                public int Total() => new[] { 1, 2, 3 }.Sum(); // System.Linq, no using
                public string Greet() => string.Join("-", ["a", "b"]); // System
            }
            """);

        var compiler = new ScriptCompiler([typeof(ScriptHost).Assembly]);
        using var assembly = compiler.CompileDirectory(dir.Path, "ImplicitUsingsGame");

        var obj = assembly.CreateInstance("Game.Summing")!;
        Assert.Equal(6, (int)obj.GetType().GetMethod("Total")!.Invoke(obj, null)!);
        Assert.Equal("a-b", (string)obj.GetType().GetMethod("Greet")!.Invoke(obj, null)!);
    }

    [Fact]
    public void BuildArtifactsAreIgnored()
    {
        // The game project is a real library project: its bin/ and obj/ folders hold
        // generated files that must never be compiled into the hot-reloaded
        // assembly (a broken generated file must not break the script compile).
        using var dir = TestUi.TempDir("script");
        dir.Write("Game.cs", "namespace Game; public class Good { }");
        var objDir = System.IO.Path.Combine(dir.Path, "obj", "Debug", "net11.0");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(System.IO.Path.Combine(objDir, "Game.AssemblyInfo.cs"), "class Broken {");
        Directory.CreateDirectory(System.IO.Path.Combine(dir.Path, "bin", "Debug"));
        File.WriteAllText(System.IO.Path.Combine(dir.Path, "bin", "Debug", "Artifacts.cs"), "class AlsoBroken {");

        var compiler = new ScriptCompiler([typeof(ScriptHost).Assembly]);
        using var assembly = compiler.CompileDirectory(dir.Path, "ArtifactGame");

        Assert.NotNull(assembly.GetType("Game.Good"));
        Assert.Null(assembly.GetType("Game.Broken"));
        Assert.Null(assembly.GetType("Game.AlsoBroken"));
    }

    [Fact]
    public void StaticStateSurvivesReload()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("State.cs", """
            namespace Game;
            public static class State
            {
                public static int Counter = 7;
                public static string Tag = "hello";
            }
            """);

        using var host = new ScriptHost();
        var first = host.WatchDirectory(dir.Path, "StateGame");

        // Bump the static through the old assembly.
        first.GetType("Game.State")!.GetField("Counter")!.SetValue(null, 99);
        first.GetType("Game.State")!.GetField("Tag")!.SetValue(null, "mutated");

        // Edit: add a static and change an initializer.
        dir.Write("State.cs", """
            namespace Game;
            public static class State
            {
                public static int Counter = 1000;
                public static string Tag = "fresh";
                public static string Extra = "new";
            }
            """);

        Assert.True(host.Reload());

        var stateType = host.Current!.GetType("Game.State")!;
        Assert.Equal(99, (int)stateType.GetField("Counter")!.GetValue(null)!);      // preserved
        Assert.Equal("mutated", (string)stateType.GetField("Tag")!.GetValue(null)!); // preserved
        Assert.Equal("new", (string)stateType.GetField("Extra")!.GetValue(null)!);   // fresh default from new code
    }

    [Fact]
    public void ChangedInstanceInitializersApplyOnReload()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Gamemode.cs", """
            namespace Game;
            public class Gamemode
            {
                public int Score = 5;
                public string Name = "Demo";
            }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "InitializerGame");
        var holder = new PlayerHolder { Current = host.Current!.CreateInstance("Game.Gamemode") };
        host.WatchInstance(holder);

        dir.Write("Gamemode.cs", """
            namespace Game;
            public class Gamemode
            {
                public int Score = 42;
                public string Name = "Production";
            }
            """);

        Assert.True(host.Reload());
        var current = holder.Current!;
        Assert.Equal(42, (int)current.GetType().GetField("Score")!.GetValue(current)!);
        Assert.Equal("Production", current.GetType().GetField("Name")!.GetValue(current));
    }

    [Fact]
    public void WatchedInstanceGraphIsUpgraded()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Player.cs", """
            namespace Game;
            public class Player
            {
                public string Name = "anon";
                public int Score;
                public Player? Buddy;
            }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "PlayerGame");

        // Build a two-node graph with a cycle, held by an engine-side holder.
        var holder = new PlayerHolder();
        var alice = host.Current!.CreateInstance("Game.Player")!;
        var bob = host.Current!.CreateInstance("Game.Player")!;
        var playerType = host.Current.GetType("Game.Player")!;
        playerType.GetField("Name")!.SetValue(alice, "Alice");
        playerType.GetField("Score")!.SetValue(alice, 50);
        playerType.GetField("Name")!.SetValue(bob, "Bob");
        playerType.GetField("Buddy")!.SetValue(alice, bob);
        playerType.GetField("Buddy")!.SetValue(bob, alice); // cycle
        holder.Current = alice;

        host.WatchInstance(holder);

        // Edit: add a field to Player.
        dir.Write("Player.cs", """
            namespace Game;
            public class Player
            {
                public string Name = "anon";
                public int Score;
                public Player? Buddy;
                public int Lives = 3;
            }
            """);

        Assert.True(host.Reload());

        var current = holder.Current!;
        var newType = host.Current!.GetType("Game.Player")!;
        Assert.NotSame(playerType, current.GetType()); // upgraded to the reloaded type
        Assert.Equal("Alice", newType.GetField("Name")!.GetValue(current));
        Assert.Equal(50, newType.GetField("Score")!.GetValue(current));
        Assert.Equal(3, newType.GetField("Lives")!.GetValue(current)); // new field at default

        var bob2 = newType.GetField("Buddy")!.GetValue(current)!;
        Assert.NotSame(bob, bob2);
        Assert.Equal("Bob", newType.GetField("Name")!.GetValue(bob2));
        // Cycle preserved: Bob.Buddy is the same upgraded Alice.
        Assert.Same(current, newType.GetField("Buddy")!.GetValue(bob2));
    }

    [Fact]
    public void CollectionsAreUpgraded()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Team.cs", """
            using System.Collections.Generic;
            namespace Game;
            public class Player
            {
                public string Name = "anon";
            }
            public class Team
            {
                public Player[]? Members;
                public List<Player>? Queue;
            }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "TeamGame");

        var team = host.Current!.CreateInstance("Game.Team")!;
        var p1 = host.Current.CreateInstance("Game.Player")!;
        var p2 = host.Current.CreateInstance("Game.Player")!;
        var p3 = host.Current.CreateInstance("Game.Player")!;
        var playerType = host.Current.GetType("Game.Player")!;
        playerType.GetField("Name")!.SetValue(p1, "one");
        playerType.GetField("Name")!.SetValue(p2, "two");
        playerType.GetField("Name")!.SetValue(p3, "three");

        // Build the typed containers with the OLD assembly's element type, as a
        // live game would (the field types are Game.Player[] / List<Game.Player>).
        var members = Array.CreateInstance(playerType, 2);
        members.SetValue(p1, 0);
        members.SetValue(p2, 1);
        var queue = (System.Collections.IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(playerType))!;
        queue.Add(p3);

        var teamType = host.Current.GetType("Game.Team")!;
        teamType.GetField("Members")!.SetValue(team, members);
        teamType.GetField("Queue")!.SetValue(team, queue);

        var holder = new PlayerHolder { Current = team };
        host.WatchInstance(holder);

        Assert.NotNull(team.GetType().GetField("Members")!.GetValue(team)); // setup sanity

        dir.Write("Team.cs", """
            using System.Collections.Generic;
            namespace Game;
            public class Player
            {
                public string Name = "anon";
                public int Lives = 3;
            }
            public class Team
            {
                public Player[]? Members;
                public List<Player>? Queue;
            }
            """);

        Assert.True(host.Reload());

        var newTeam = holder.Current!;
        var newType = host.Current!.GetType("Game.Player")!;
        var newMembers = (Array)newTeam.GetType().GetField("Members")!.GetValue(newTeam)!;
        Assert.Equal(2, newMembers.Length);
        Assert.Equal("one", newType.GetField("Name")!.GetValue(newMembers.GetValue(0)));
        Assert.Equal("two", newType.GetField("Name")!.GetValue(newMembers.GetValue(1)));
        Assert.Equal(3, (int)newType.GetField("Lives")!.GetValue(newMembers.GetValue(0))!);

        var newQueue = (System.Collections.IList)newTeam.GetType().GetField("Queue")!.GetValue(newTeam)!;
        Assert.Equal("three", newType.GetField("Name")!.GetValue(newQueue[0]));
    }

    [Fact]
    public void RemovedTypeIsNulled()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Obsolete.cs", """
            namespace Game;
            public class Player { public int Score; }
            public class Obsolete { public int X; }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "ObsoleteGame");

        var holder = new PlayerHolder { Current = host.Current!.CreateInstance("Game.Obsolete")! };
        host.WatchInstance(holder);

        // Remove the Obsolete class entirely.
        dir.Write("Obsolete.cs", """
            namespace Game;
            public class Player { public int Score; }
            """);

        Assert.True(host.Reload());
        Assert.Null(holder.Current); // reference to the removed type becomes null
    }

    [Fact]
    public void CustomUpgraderRunsBeforeDefault()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Player.cs", """
            namespace Game;
            public class Player { public int Score; }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "CustomGame");

        var holder = new PlayerHolder { Current = host.Current!.CreateInstance("Game.Player")! };
        host.Current.GetType("Game.Player")!.GetField("Score")!.SetValue(holder.Current, 50);
        host.WatchInstance(holder);

        var doubler = new ScoreDoublingUpgrader();
        host.RegisterUpgrader(doubler);

        dir.Write("Player.cs", """
            namespace Game;
            public class Player { public int Score; public string Name = "x"; }
            """);

        Assert.True(host.Reload());
        Assert.True(doubler.Invoked);
        var newType = host.Current!.GetType("Game.Player")!;
        Assert.Equal(100, (int)newType.GetField("Score")!.GetValue(holder.Current!)!); // doubled by the custom upgrader
        Assert.Equal("x", (string)newType.GetField("Name")!.GetValue(holder.Current!)!); // default upgrader filled the rest
    }

    [Fact]
    public void CompileFailureKeepsPreviousAssembly()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Game.cs", """
            namespace Game;
            public class Good { public static int Value = 42; }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "FailGame");

        var failed = new List<ScriptReloadFailedEventArgs>();
        host.ReloadFailed += e => failed.Add(e);
        var reloaded = 0;
        host.Reloaded += _ => reloaded++;

        dir.Write("Game.cs", "namespace Game; public class Broken { public int Value = ; }"); // syntax error

        Assert.False(host.Reload());
        Assert.Single(failed);
        Assert.IsType<ScriptCompilationException>(failed[0].Error);
        Assert.Equal(0, reloaded);
        Assert.Equal(42, (int)host.Current!.GetType("Game.Good")!.GetField("Value")!.GetValue(null)!);
        Assert.NotNull(host.Current.GetType("Game.Good")); // previous assembly still active

        // A subsequent successful reload works again.
        dir.Write("Game.cs", """
            namespace Game;
            public class Fixed { public int Value = 7; }
            """);
        Assert.True(host.Reload());
        Assert.Equal(1, reloaded);
    }

    [Fact]
    public void SkipHotloadExcludesMembers()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("State.cs", """
            using Crowbar.Engine.Scripting;
            namespace Game;
            public static class State
            {
                public static int Kept = 1;
                [SkipHotload]
                public static int Skipped = 2;
            }
            """);

        using var host = new ScriptHost();
        var first = host.WatchDirectory(dir.Path, "SkipGame");
        first.GetType("Game.State")!.GetField("Kept")!.SetValue(null, 111);
        first.GetType("Game.State")!.GetField("Skipped")!.SetValue(null, 222);

        dir.Write("State.cs", """
            using Crowbar.Engine.Scripting;
            namespace Game;
            public static class State
            {
                public static int Kept = 1;
                [SkipHotload]
                public static int Skipped = 999;
            }
            """);

        Assert.True(host.Reload());
        var stateType = host.Current!.GetType("Game.State")!;
        Assert.Equal(111, (int)stateType.GetField("Kept")!.GetValue(null)!);   // migrated
        Assert.Equal(999, (int)stateType.GetField("Skipped")!.GetValue(null)!); // fresh default (skipped)
    }

    [Fact]
    public void ReloadedEventCarriesBothAssemblies()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Game.cs", "namespace Game; public class A { }");

        using var host = new ScriptHost();
        var first = host.WatchDirectory(dir.Path, "EventGame");

        ScriptReloadedEventArgs? args = null;
        host.Reloaded += e => args = e;

        dir.Write("Game.cs", "namespace Game; public class A { public int X; }");
        Assert.True(host.Reload());

        Assert.NotNull(args);
        Assert.Same(first, args!.Previous);
        Assert.Same(host.Current, args.Current);
        Assert.NotSame(first, host.Current);
    }

    private sealed class ScoreDoublingUpgrader : IInstanceUpgrader
    {
        public bool Invoked { get; private set; }

        public bool CanUpgrade(Type oldType, Type newType) => oldType.Name == "Player";

        public object Upgrade(object instance, Type newType, IUpgradeContext context)
        {
            Invoked = true;
            var score = (int)instance.GetType().GetField("Score")!.GetValue(instance)!;
            var upgraded = Activator.CreateInstance(newType)!;
            context.RegisterUpgraded(instance, upgraded);
            upgraded.GetType().GetField("Score")!.SetValue(upgraded, score * 2);
            return upgraded;
        }
    }
}
