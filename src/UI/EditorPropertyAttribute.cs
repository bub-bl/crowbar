namespace Crowbar.UI;

/// <summary>
/// Declares the CLR property type a per-type editor component renders. Editor
/// components apply it through the <c>@attribute</c> directive
/// (e.g. <c>@attribute [EditorProperty(typeof(float))]</c>); the runtime reads
/// the declaration from the component source and builds the type → editor map
/// used by <see cref="PropertyEditorRegistry"/>. Adding an editor therefore
/// never requires touching the dispatcher.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
public sealed class EditorPropertyAttribute : Attribute
{
    public EditorPropertyAttribute(Type propertyType)
    {
        PropertyType = propertyType;
    }

    /// <summary>The CLR type the decorated editor component renders.</summary>
    public Type PropertyType { get; }
}
