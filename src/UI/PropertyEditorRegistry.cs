using System.Text.RegularExpressions;

namespace Crowbar.UI;

/// <summary>
/// Maps a property's CLR type to the editor component tag that renders it.
/// The map is populated from the <c>[EditorProperty(typeof(...))]</c>
/// declarations on the per-type editor components, so a new editor registers
/// itself and no dispatcher code has to be edited. Keys are canonical full
/// type names (<c>System.Single</c>, <c>System.Numerics.Vector3</c>, …) so
/// they match the names the inspector state builder emits.
/// </summary>
public static class PropertyEditorRegistry
{
    /// <summary>Editor tag used when no editor declares the property type.</summary>
    public const string FallbackTag = "ObjectEditor";

    private static readonly Lock Lock = new();
    private static readonly Dictionary<string, string> ByType = new(StringComparer.Ordinal);

    /// <summary>Registers every <c>[EditorProperty]</c> declaration found in a component's source.</summary>
    public static void RegisterFromSource(string source, string tagName)
    {
        foreach (var typeName in ExtractTypeNames(source))
            Register(typeName, tagName);
    }

    /// <summary>Registers one type → editor-tag mapping.</summary>
    public static void Register(string typeName, string tagName)
    {
        if (string.IsNullOrEmpty(typeName) || string.IsNullOrEmpty(tagName))
            return;
        lock (Lock)
        {
            ByType[typeName] = tagName;
        }
    }

    /// <summary>Returns the editor tag for a property type, or <see cref="FallbackTag"/>.</summary>
    public static string ResolveTag(string typeName) =>
        ByType.TryGetValue(typeName, out var tag) ? tag : FallbackTag;

    /// <summary>Clears the registry (test isolation).</summary>
    internal static void Reset()
    {
        lock (Lock)
        {
            ByType.Clear();
        }
    }

    private static IEnumerable<string> ExtractTypeNames(string source)
    {
        // Registration runs before the component is compiled, so the
        // declaration is read directly from the component source.
        foreach (Match match in Regex.Matches(source,
                     @"\[EditorProperty(?:Attribute)?\s*\(\s*typeof\s*\(\s*([^)]+?)\s*\)\s*\)\s*\]"))
        {
            var resolved = ResolveTypeName(match.Groups[1].Value);
            if (resolved is not null)
                yield return resolved;
        }
    }

    private static string? ResolveTypeName(string name)
    {
        name = name.Trim();
        var keyword = name switch
        {
            "float" => "System.Single",
            "double" => "System.Double",
            "int" => "System.Int32",
            "uint" => "System.UInt32",
            "long" => "System.Int64",
            "ulong" => "System.UInt64",
            "short" => "System.Int16",
            "ushort" => "System.UInt16",
            "byte" => "System.Byte",
            "sbyte" => "System.SByte",
            "decimal" => "System.Decimal",
            "bool" => "System.Boolean",
            "char" => "System.Char",
            "string" => "System.String",
            "object" => "System.Object",
            _ => null
        };
        if (keyword is not null)
            return keyword;

        // Fully-qualified names resolve directly; simple names (Vector3, …)
        // resolve through the common namespaces or the loaded assemblies.
        var type = Type.GetType(name, false, false)
                   ?? ResolveByNamespace(name)
                   ?? AppDomain.CurrentDomain.GetAssemblies()
                       .Select(assembly => assembly.GetType(name, false, false))
                       .FirstOrDefault(found => found is not null);
        return type?.FullName ?? name;
    }

    private static Type? ResolveByNamespace(string name)
    {
        if (name.Contains('.'))
            return null;
        foreach (var ns in new[] { "System", "System.Numerics", "System.Collections.Generic" })
        {
            var type = Type.GetType(ns + "." + name, false, false);
            if (type is not null)
                return type;
        }

        return null;
    }
}
