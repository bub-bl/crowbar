using Crowbar.Engine.Scripting;
using Microsoft.CodeAnalysis.CSharp;

namespace Crowbar.UI.Tests.Scripting;

public sealed class IlFastPathTests
{
    [Fact]
    public void BodyOnlyChangePatchesInPlaceAndPreservesIdentity()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Calc.cs", """
            namespace Game;
            public class Calc
            {
                public int Base = 10;
                public int Compute() { return Base * 3 + 7; }
                public string Label() { return "v1"; }
            }
            """);

        using var host = new ScriptHost();
        var first = host.WatchDirectory(dir.Path, "FastGame");

        var calc = first.CreateInstance("Game.Calc")!;
        var holder = new PlayerHolder { Current = calc };
        host.WatchInstance(holder);

        // Body-only change: only Compute()'s body differs.
        dir.Write("Calc.cs", """
            namespace Game;
            public class Calc
            {
                public int Base = 10;
                public int Compute() { return Base * 3 + 999; }
                public string Label() { return "v1"; }
            }
            """);

        ScriptReloadedEventArgs? args = null;
        host.Reloaded += e => args = e;

        Assert.True(host.Reload());

        Assert.NotNull(args);
        Assert.Equal(ScriptReloadMode.FastPath, args!.Mode);
        Assert.True(args.PatchedMethods >= 1);

        // Identity preserved: no instance upgrade, no assembly swap.
        Assert.Same(first, host.Current);
        Assert.Same(calc, holder.Current);
        // …but the instance now runs the NEW code.
        var result = holder.Current!.GetType().GetMethod("Compute")!.Invoke(holder.Current, null);
        Assert.Equal(10 * 3 + 999, (int)result!);
    }

    [Fact]
    public void StructuralChangeFallsBackToFullReload()
    {
        using var dir = TestUi.TempDir("script");
        dir.Write("Calc.cs", """
            namespace Game;
            public class Calc
            {
                public int Base = 10;
                public int Compute() { return Base * 3 + 7; }
            }
            """);

        using var host = new ScriptHost();
        host.WatchDirectory(dir.Path, "FullGame");

        var calc = host.Current!.CreateInstance("Game.Calc")!;
        host.Current.GetType("Game.Calc")!.GetField("Base")!.SetValue(calc, 21);
        var holder = new PlayerHolder { Current = calc };
        host.WatchInstance(holder);

        // Structural change: a field is added → must be a full reload.
        dir.Write("Calc.cs", """
            namespace Game;
            public class Calc
            {
                public int Base = 10;
                public int Extra = 1;
                public int Compute() { return Base * 3 + 7; }
            }
            """);

        ScriptReloadedEventArgs? args = null;
        host.Reloaded += e => args = e;

        Assert.True(host.Reload());

        Assert.NotNull(args);
        Assert.Equal(ScriptReloadMode.FullReload, args!.Mode);
        Assert.NotSame(calc, holder.Current); // instance upgraded (replaced)
        Assert.Equal(21, (int)holder.Current!.GetType().GetField("Base")!.GetValue(holder.Current)!); // state carried
        Assert.Equal(1, (int)holder.Current.GetType().GetField("Extra")!.GetValue(holder.Current)!); // new field default
    }

    // ---- Change classifier --------------------------------------------------

    [Fact]
    public void ClassifierAcceptsBodyOnlyAndListsChangedMethods()
    {
        var oldTree = CSharpSyntaxTree.ParseText("""
            namespace Game;
            public class C { public int A() { return 1; } public string B() { return "x"; } }
            """);
        var newTree = CSharpSyntaxTree.ParseText("""
            namespace Game;
            public class C { public int A() { return 2; } public string B() { return "x"; } }
            """);

        var changed = ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = newTree });

        Assert.NotNull(changed);
        Assert.Equal(["Game.C::A::0"], changed);
    }

    [Fact]
    public void ClassifierRejectsAddedField()
    {
        var oldTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A; }");
        var newTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A; public int B; }");

        Assert.Null(ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = newTree }));
    }

    [Fact]
    public void ClassifierRejectsAddedMethod()
    {
        var oldTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A() => 1; }");
        var newTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A() => 1; public int B() => 2; }");

        Assert.Null(ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = newTree }));
    }

    [Fact]
    public void ClassifierRejectsSignatureChange()
    {
        var oldTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A(int x) => x; }");
        var newTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A(string x) => 1; }");

        Assert.Null(ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = newTree }));
    }

    [Fact]
    public void ClassifierRejectsAddedFile()
    {
        var oldTree = CSharpSyntaxTree.ParseText("namespace Game; public class A { public int X() => 1; }");
        var newTree = CSharpSyntaxTree.ParseText("namespace Game; public class A { public int X() => 1; }");

        Assert.Null(ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["A.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["A.cs"] = oldTree, ["B.cs"] = newTree }));
    }

    [Fact]
    public void ClassifierAcceptsExpressionBodiedChange()
    {
        var oldTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A() => 1; }");
        var newTree = CSharpSyntaxTree.ParseText("namespace Game; public class C { public int A() => 2; }");

        var changed = ScriptChangeClassifier.Classify(
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = oldTree },
            new Dictionary<string, Microsoft.CodeAnalysis.SyntaxTree> { ["C.cs"] = newTree });

        Assert.NotNull(changed);
        Assert.Equal(["Game.C::A::0"], changed);
    }
}
