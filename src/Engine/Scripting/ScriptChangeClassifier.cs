using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// Classifies a script source change by comparing the previous and new syntax
/// trees (the analog of s&amp;box's Sandbox.Generator analysis). When the only
/// differences are inside method/accessor/constructor bodies — no added or
/// removed types, fields or members, no signature, hierarchy or using changes —
/// the reload can take the IL fast path and patch the live assembly in place.
///
/// Returns the set of changed member keys ("typeFullName::name::arity") when
/// the change qualifies, or null when the change is structural.
/// </summary>
internal static class ScriptChangeClassifier
{
    public static IReadOnlySet<string>? Classify(
        IReadOnlyDictionary<string, SyntaxTree> oldTrees,
        IReadOnlyDictionary<string, SyntaxTree> newTrees)
    {
        var oldFiles = new HashSet<string>(oldTrees.Keys, StringComparer.OrdinalIgnoreCase);
        var newFiles = new HashSet<string>(newTrees.Keys, StringComparer.OrdinalIgnoreCase);
        if (!oldFiles.SetEquals(newFiles))
            return null; // a file was added or removed

        var changed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in oldFiles)
        {
            if (oldTrees[file].GetRoot() is not CompilationUnitSyntax a ||
                newTrees[file].GetRoot() is not CompilationUnitSyntax b)
                return null;

            // Using directives change name resolution → structural.
            if (!AreEquivalent(a.Usings, b.Usings) || !AreEquivalent(a.Externs, b.Externs))
                return null;
            if (!CompareMemberLists(a.Members, b.Members, ns: "", typePath: "", changed))
                return null;
        }

        return changed;
    }

    private static bool CompareMemberLists(
        SyntaxList<MemberDeclarationSyntax> aList, SyntaxList<MemberDeclarationSyntax> bList,
        string ns, string typePath, HashSet<string> changed)
    {
        var aMembers = aList.ToList();
        var bMembers = bList.ToList();
        if (aMembers.Count != bMembers.Count)
            return false; // a member was added or removed

        var bByIdentity = bMembers.GroupBy(Identity).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var a in aMembers)
        {
            if (!bByIdentity.TryGetValue(Identity(a), out var candidates) || candidates.Count == 0)
                return false;
            var b = candidates[0];
            candidates.RemoveAt(0);
            if (!CompareMember(a, b, ns, typePath, changed))
                return false;
        }

        return true;
    }

    private static bool CompareMember(
        MemberDeclarationSyntax a, MemberDeclarationSyntax b, string ns, string typePath, HashSet<string> changed)
    {
        switch (a)
        {
            case NamespaceDeclarationSyntax na when b is NamespaceDeclarationSyntax nb:
                return SyntaxFactory.AreEquivalent(na.Name, nb.Name)
                    && CompareMemberLists(na.Members, nb.Members, CombineNamespace(ns, na.Name), typePath, changed);

            case FileScopedNamespaceDeclarationSyntax na when b is FileScopedNamespaceDeclarationSyntax nb:
                return SyntaxFactory.AreEquivalent(na.Name, nb.Name)
                    && CompareMemberLists(na.Members, nb.Members, CombineNamespace(ns, na.Name), typePath, changed);

            case BaseTypeDeclarationSyntax ta when b is BaseTypeDeclarationSyntax tb:
                if (!SyntaxFactory.AreEquivalent(WithoutTypeBody(ta), WithoutTypeBody(tb)))
                    return false; // modifiers, base list or type parameters changed
                var childTypePath = typePath.Length == 0 ? ta.Identifier.Text : typePath + "+" + ta.Identifier.Text;
                return CompareMemberLists(MembersOf(ta), MembersOf(tb), ns, childTypePath, changed);

            case MethodDeclarationSyntax ma when b is MethodDeclarationSyntax mb:
                if (!SyntaxFactory.AreEquivalent(WithoutBody(ma), WithoutBody(mb)))
                    return false; // signature changed
                if (HasBody(ma) && !SyntaxFactory.AreEquivalent(ma, mb))
                    changed.Add(MethodKey(FullName(ns, typePath), ma.Identifier.Text, ma.ParameterList.Parameters.Count));
                return true;

            case ConstructorDeclarationSyntax ca when b is ConstructorDeclarationSyntax cb:
                if (!SyntaxFactory.AreEquivalent(WithoutBody(ca), WithoutBody(cb)))
                    return false;
                if (HasBody(ca) && !SyntaxFactory.AreEquivalent(ca, cb))
                    changed.Add(MethodKey(FullName(ns, typePath), ".ctor", ca.ParameterList.Parameters.Count));
                return true;

            case PropertyDeclarationSyntax pa when b is PropertyDeclarationSyntax pb:
                return CompareProperty(pa, pb, FullName(ns, typePath), changed);

            case IndexerDeclarationSyntax ia when b is IndexerDeclarationSyntax ib:
                return CompareIndexer(ia, ib, FullName(ns, typePath), changed);

            case FieldDeclarationSyntax fa when b is FieldDeclarationSyntax fb:
                // Fields define memory layout AND their initializer change is a
                // declaration-level change (new defaults only apply to fresh
                // assemblies) — any difference is structural, like s&amp;box.
                return SyntaxFactory.AreEquivalent(fa, fb);

            case EventDeclarationSyntax ea when b is EventDeclarationSyntax eb:
                if (!SyntaxFactory.AreEquivalent(ea.WithAccessorList(null), eb.WithAccessorList(null)))
                    return false;
                return CompareAccessorPairs(ea.AccessorList, eb.AccessorList, FullName(ns, typePath),
                    "add_" + ea.Identifier.Text, "remove_" + ea.Identifier.Text, changed);

            default:
                // Enums, delegates, event fields, operators, conversions: no
                // bodies to fast-path — any difference is structural.
                return SyntaxFactory.AreEquivalent(a, b);
        }
    }

    private static bool CompareProperty(
        PropertyDeclarationSyntax a, PropertyDeclarationSyntax b, string fullName, HashSet<string> changed)
    {
        if (!SyntaxFactory.AreEquivalent(WithoutBody(a), WithoutBody(b)))
            return false;

        if (a.ExpressionBody is not null)
        {
            if (!SyntaxFactory.AreEquivalent(a.ExpressionBody, b.ExpressionBody))
                changed.Add(MethodKey(fullName, "get_" + a.Identifier.Text, 0));
            return true;
        }

        return CompareAccessorPairs(a.AccessorList, b.AccessorList, fullName,
            "get_" + a.Identifier.Text, "set_" + a.Identifier.Text, changed);
    }

    private static bool CompareIndexer(
        IndexerDeclarationSyntax a, IndexerDeclarationSyntax b, string fullName, HashSet<string> changed)
    {
        if (!SyntaxFactory.AreEquivalent(WithoutBody(a), WithoutBody(b)))
            return false;

        var parameterCount = a.ParameterList.Parameters.Count;
        return CompareAccessorPairs(a.AccessorList, b.AccessorList, fullName,
            "get_Item", "set_Item", changed, getArity: parameterCount, setArity: parameterCount + 1);
    }

    private static bool CompareAccessorPairs(
        AccessorListSyntax? aList, AccessorListSyntax? bList, string fullName,
        string getterName, string setterName, HashSet<string> changed,
        int? getArity = null, int? setArity = null)
    {
        var aAccessors = aList?.Accessors.ToList() ?? [];
        var bAccessors = bList?.Accessors.ToList() ?? [];
        if (aAccessors.Count != bAccessors.Count)
            return false;

        foreach (var (aa, bb) in aAccessors.Zip(bAccessors))
        {
            if (!SyntaxFactory.AreEquivalent(
                    aa.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default),
                    bb.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)))
                return false; // accessor kind or modifiers changed

            if (aa.Body is null && aa.ExpressionBody is null)
                continue; // get; / set; — nothing to patch

            var isGet = aa.Keyword.Kind() is SyntaxKind.GetKeyword;
            var name = isGet ? getterName : setterName;
            var arity = isGet ? (getArity ?? 0) : (setArity ?? 1);
            changed.Add(MethodKey(fullName, name, arity));
        }

        return true;
    }

    private static bool HasBody(MethodDeclarationSyntax m) => m.Body is not null || m.ExpressionBody is not null;
    private static bool HasBody(ConstructorDeclarationSyntax c) => c.Body is not null || c.ExpressionBody is not null;

    private static string FullName(string ns, string typePath)
        => ns.Length == 0 ? typePath : ns + "." + typePath;

    private static string CombineNamespace(string ns, NameSyntax name)
    {
        var child = name.ToString();
        return ns.Length == 0 ? child : ns + "." + child;
    }

    private static string MethodKey(string typeFullName, string name, int arity)
        => $"{typeFullName}::{name}::{arity}";

    private static string Identity(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax x => $"M:{x.Identifier.Text}:{Params(x.ParameterList)}",
        ConstructorDeclarationSyntax x => $"C:{Params(x.ParameterList)}",
        PropertyDeclarationSyntax x => $"P:{x.Identifier.Text}",
        IndexerDeclarationSyntax x => $"I:{Params(x.ParameterList)}",
        FieldDeclarationSyntax x => "F:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventDeclarationSyntax x => $"E:{x.Identifier.Text}",
        EventFieldDeclarationSyntax x => "EF:" + string.Join(",", x.Declaration.Variables.Select(v => v.Identifier.Text)),
        BaseTypeDeclarationSyntax x => $"{x.Kind()}:{x.Identifier.Text}",
        DelegateDeclarationSyntax x => $"D:{x.Identifier.Text}:{Params(x.ParameterList)}",
        _ => m.Kind().ToString()
    };

    private static string Params(BaseParameterListSyntax parameters)
        => string.Join(",", parameters.Parameters.Select(p => p.Type?.NormalizeWhitespace().ToString() ?? ""));

    private static MemberDeclarationSyntax WithoutBody(MethodDeclarationSyntax m)
        => m.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default);

    private static MemberDeclarationSyntax WithoutBody(ConstructorDeclarationSyntax c)
        => c.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default);

    private static MemberDeclarationSyntax WithoutBody(PropertyDeclarationSyntax p)
        => p.WithAccessorList(null).WithExpressionBody(null).WithSemicolonToken(default);

    private static MemberDeclarationSyntax WithoutBody(IndexerDeclarationSyntax i)
        => i.WithAccessorList(null).WithExpressionBody(null).WithSemicolonToken(default);

    private static BaseTypeDeclarationSyntax WithoutTypeBody(BaseTypeDeclarationSyntax t) => t switch
    {
        ClassDeclarationSyntax c => c.WithMembers(default),
        StructDeclarationSyntax s => s.WithMembers(default),
        InterfaceDeclarationSyntax i => i.WithMembers(default),
        RecordDeclarationSyntax r => r.WithMembers(default),
        EnumDeclarationSyntax e => e.WithMembers(default),
        _ => t
    };

    private static SyntaxList<MemberDeclarationSyntax> MembersOf(BaseTypeDeclarationSyntax t) => t switch
    {
        ClassDeclarationSyntax c => c.Members,
        StructDeclarationSyntax s => s.Members,
        InterfaceDeclarationSyntax i => i.Members,
        RecordDeclarationSyntax r => r.Members,
        EnumDeclarationSyntax e => SyntaxFactory.List(e.Members.Cast<MemberDeclarationSyntax>()),
        _ => default
    };

    private static bool AreEquivalent<T>(SyntaxList<T> a, SyntaxList<T> b) where T : SyntaxNode
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (!SyntaxFactory.AreEquivalent(a[i], b[i]))
                return false;
        }

        return true;
    }
}
