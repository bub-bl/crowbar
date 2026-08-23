using System.Net;
using Microsoft.AspNetCore.Components;

namespace Crowbar.UI;

/// <summary>
/// Resolves the editor component for a property's CLR type through
/// <see cref="PropertyEditorRegistry"/> and renders it, passing the uniform
/// name/value/indent/key contract every editor shares. The registry is built
/// from the <c>[EditorProperty]</c> declarations on the editor components, so
/// adding an editor never requires editing this dispatcher.
/// </summary>
public sealed class PropertyEditor : RazorPanel
{
    [Parameter]
    public string TypeName { get; set; } = "System.Object";

    [Parameter]
    public string Name { get; set; } = string.Empty;

    [Parameter]
    public string Value { get; set; } = string.Empty;

    [Parameter]
    public int Indent { get; set; }

    [Parameter]
    public string Key { get; set; } = string.Empty;

    public override Task ExecuteAsync()
    {
        var propertyType = ResolveType(TypeName);
        var tag = propertyType?.IsEnum == true ? "EnumEditor" : PropertyEditorRegistry.ResolveTag(TypeName);
        var typeAttribute = propertyType?.IsEnum == true ? $" TypeName=\"{Attr(TypeName)}\"" : string.Empty;
        WriteLiteral($"<{tag} Name=\"{Attr(Name)}\" Value=\"{Attr(Value)}\" Key=\"{Attr(Key)}\" Indent=\"{Indent}\"{typeAttribute} />");
        return Task.CompletedTask;
    }

    protected override int BuildHash() =>
        HashCode.Combine(TypeName, PropertyEditorRegistry.ResolveTag(TypeName), ResolveType(TypeName)?.IsEnum, Name, Value, Key, Indent);

    private static string Attr(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static Type? ResolveType(string name) =>
        Type.GetType(name, false, false) ??
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(name, false, false))
            .FirstOrDefault(type => type is not null);
}
