using System.Reflection;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// The default migration strategy: creates a new instance of the reloaded type
/// with its parameterless constructor and copies instance field values across,
/// upgrading each value recursively through <see cref="IUpgradeContext.Upgrade"/>.
/// The new instance is registered in the context before its fields are copied,
/// so cycles and shared references keep pointing at the same upgraded instance.
/// Fields marked with <see cref="SkipHotloadAttribute"/> are left at their
/// default value.
/// </summary>
public sealed class DefaultInstanceUpgrader : IInstanceUpgrader
{
    public bool CanUpgrade(Type oldType, Type newType)
        => !oldType.IsAbstract && !oldType.IsInterface && newType.GetConstructor(Type.EmptyTypes) is not null;

    public object Upgrade(object instance, Type newType, IUpgradeContext context)
    {
        var upgraded = Activator.CreateInstance(newType)!;
        context.RegisterUpgraded(instance, upgraded);

        foreach (var field in EnumerateInstanceFields(instance.GetType()))
        {
            if (field.IsDefined(typeof(SkipHotloadAttribute), inherit: true))
                continue;
            var newField = FindInstanceField(newType, field.Name);
            if (newField is null)
                continue;
            var value = field.GetValue(instance);
            try
            {
                newField.SetValue(upgraded, context.Upgrade(value));
            }
            catch (Exception)
            {
                // The field's type changed incompatibly — keep the new default.
            }
        }

        return upgraded;
    }

    /// <summary>Every instance field of the type and its base types, most-derived first.</summary>
    internal static IEnumerable<FieldInfo> EnumerateInstanceFields(Type type)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                yield return field;
        }
    }

    private static FieldInfo? FindInstanceField(Type type, string name)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var field = t.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }

        return null;
    }
}
