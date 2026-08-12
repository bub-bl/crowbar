using System.Collections;
using System.Reflection;

namespace Crowbar.Engine.Scripting;

/// <summary>
/// Migrates state from an old script assembly to a reloaded one (the analog
/// of s&amp;box's hot reload heap walk): static field values are carried across,
/// and object graphs rooted at watched instances are upgraded — old-assembly
/// objects are replaced by new-assembly instances with identity preserved,
/// while engine/BCL objects are walked so their nested script references get
/// upgraded too.
///
/// Containers whose element type is an old-assembly type (Player[], List&lt;Player&gt;,
/// Dictionary&lt;string, Player&gt;) cannot hold reloaded instances and are rebuilt
/// with the new element types; containers typed with engine/BCL element types
/// (List&lt;Component&gt;, object[]) are upgraded in place since new script
/// instances are assignable to them.
///
/// One instance is created per reload, so identity is scoped to a single swap:
/// two references to the same old object always resolve to the same upgraded
/// object within one reload.
/// </summary>
internal sealed class HotReloadUpgrader : IUpgradeContext
{
    private readonly Assembly _oldAssembly;
    private readonly IReadOnlyDictionary<string, Type> _newTypes;
    private readonly IReadOnlyList<IInstanceUpgrader> _upgraders;
    private readonly IReadOnlySet<string> _changedFieldInitializers;

    // Old object → upgraded object (identity preserved for script objects and rebuilt containers).
    private readonly Dictionary<object, object> _upgraded = new(ReferenceEqualityComparer.Instance);
    // Objects currently being upgraded/walked (cycle guard).
    private readonly HashSet<object> _walking = new(ReferenceEqualityComparer.Instance);
    // Engine/BCL objects already walked in place (shared references are not re-walked).
    private readonly HashSet<object> _walked = new(ReferenceEqualityComparer.Instance);

    public HotReloadUpgrader(Assembly oldAssembly, IReadOnlyDictionary<string, Type> newTypes,
        IReadOnlyList<IInstanceUpgrader> upgraders, IReadOnlySet<string>? changedFieldInitializers = null)
    {
        _oldAssembly = oldAssembly;
        _newTypes = newTypes;
        _upgraders = upgraders;
        _changedFieldInitializers = changedFieldInitializers ?? new HashSet<string>(StringComparer.Ordinal);
    }

    /// <summary>Whether a field's edited initializer should win over migrated instance state.</summary>
    public bool ShouldUseNewDefault(Type oldType, string fieldName) =>
        _changedFieldInitializers.Contains($"{oldType.FullName}::{fieldName}");

    public Type? ResolveNewType(Type oldType) => _newTypes.GetValueOrDefault(oldType.FullName ?? string.Empty);

    public void RegisterUpgraded(object old, object upgraded) => _upgraded[old] = upgraded;

    /// <summary>Number of old-assembly objects replaced during the walk (identity map size).</summary>
    public int UpgradedCount => _upgraded.Count;

    public object? Upgrade(object? value)
    {
        if (value is null)
            return null;

        var type = value.GetType();
        if (IsLeaf(type))
            return value;
        if (_upgraded.TryGetValue(value, out var upgraded))
            return upgraded;

        // Containers first: an array's Assembly is its element type's assembly
        // (e.g. Game.Player[] reports the script assembly), so containers must
        // never enter the old-assembly branch below — they are engine/BCL
        // objects whose element types may be script types.
        if (value is Array or IList or IDictionary)
        {
            if (_walking.Contains(value))
                return value;
            _walking.Add(value);
            try
            {
                return value switch
                {
                    Array array => UpgradeArray(array),
                    IList list => UpgradeList(list),
                    _ => UpgradeDictionary((IDictionary)value)
                };
            }
            finally
            {
                _walking.Remove(value);
                _walked.Add(value);
            }
        }

        if (type.Assembly == _oldAssembly)
        {
            // A root being migrated in place (see UpgradeRoot) — cycles back to
            // it keep the caller's reference, not a copy.
            if (_walking.Contains(value))
                return value;
            if (ResolveNewType(type) is not { } newType)
                return null; // the type was removed

            foreach (var upgrader in _upgraders)
            {
                if (!upgrader.CanUpgrade(type, newType))
                    continue;
                _walking.Add(value);
                try
                {
                    return upgrader.Upgrade(value, newType, this);
                }
                finally
                {
                    _walking.Remove(value);
                }
            }

            // No upgrader could migrate the type (e.g. no parameterless
            // constructor): keep the old instance but migrate its nested state.
            UpgradeFieldsInPlace(value, type);
            return value;
        }

        // Engine/BCL object: it may reference script objects, so walk it in place.
        if (_walking.Contains(value))
            return value;

        _walking.Add(value);
        try
        {
            if (!_walked.Contains(value))
                UpgradeFieldsInPlace(value, type);
            return value;
        }
        finally
        {
            _walking.Remove(value);
            _walked.Add(value);
        }
    }

    /// <summary>
    /// Migrates the static field values of every type in the old script
    /// assembly into the reloaded types (matched by field name). Const fields
    /// and fields marked with <see cref="SkipHotloadAttribute"/> are excluded;
    /// removed types lose their statics.
    /// </summary>
    public void MigrateStatics(IEnumerable<Type> oldTypes)
    {
        foreach (var oldType in oldTypes)
        {
            if (oldType.IsDefined(typeof(SkipHotloadAttribute), inherit: true))
                continue;
            if (ResolveNewType(oldType) is not { } newType)
                continue;

            foreach (var field in oldType.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (field.IsLiteral || field.IsDefined(typeof(SkipHotloadAttribute), inherit: true))
                    continue;
                var newField = FindStaticField(newType, field.Name);
                if (newField is null)
                    continue;

                var value = field.GetValue(null);
                try
                {
                    newField.SetValue(null, Upgrade(value));
                }
                catch (Exception)
                {
                    // The field's type changed incompatibly — keep the new default.
                }
            }
        }
    }

    /// <summary>
    /// Upgrades the object graph reachable from <paramref name="root"/> in
    /// place. If the root itself is an old-assembly object its fields are
    /// migrated in place (the root reference lives in caller code and cannot
    /// be replaced); otherwise the whole root is walked, replacing reachable
    /// script objects.
    /// </summary>
    public void UpgradeRoot(object root)
    {
        if (root is null)
            return;

        var type = root.GetType();
        if (type.Assembly == _oldAssembly)
        {
            _walking.Add(root);
            try
            {
                UpgradeFieldsInPlace(root, type);
            }
            finally
            {
                _walking.Remove(root);
                _walked.Add(root);
            }

            return;
        }

        Upgrade(root);
    }

    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
        type == typeof(DateTime) || type == typeof(TimeSpan) || type == typeof(Guid) ||
        type == typeof(Type) || typeof(Delegate).IsAssignableFrom(type);

    private void UpgradeFieldsInPlace(object target, Type type)
    {
        foreach (var field in DefaultInstanceUpgrader.EnumerateInstanceFields(type))
        {
            if (field.IsDefined(typeof(SkipHotloadAttribute), inherit: true))
                continue;

            object? value;
            object? upgraded;
            try
            {
                value = field.GetValue(target);
                upgraded = Upgrade(value);
            }
            catch (Exception)
            {
                continue;
            }

            if (!ReferenceEquals(upgraded, value) || value is not null && value.GetType().IsValueType)
            {
                try
                {
                    field.SetValue(target, upgraded);
                }
                catch (Exception)
                {
                    // Readonly/typed field that rejects the write — keep as is.
                }
            }
        }
    }

    private object UpgradeArray(Array array)
    {
        if (_upgraded.TryGetValue(array, out var done))
            return done;

        var elementType = array.GetType().GetElementType()!;
        // An array typed with an old-assembly element type cannot hold reloaded
        // instances — rebuild it with the new element type.
        var newElementType = array.Rank == 1 ? ResolveNewType(elementType) : null;
        if (array.Rank == 1 && elementType.Assembly == _oldAssembly && newElementType is not null)
        {
            var rebuilt = Array.CreateInstance(newElementType, array.Length);
            _upgraded[array] = rebuilt;
            for (var i = 0; i < array.Length; i++)
            {
                var element = array.GetValue(i);
                rebuilt.SetValue(Upgrade(element), i);
            }

            return rebuilt;
        }

        // Engine/BCL element types: new script instances are assignable, so the
        // array can be upgraded in place.
        for (var i = 0; i < array.Length; i++)
        {
            var element = array.GetValue(i);
            var upgraded = Upgrade(element);
            if (!ReferenceEquals(upgraded, element) || element is not null && element.GetType().IsValueType)
            {
                try
                {
                    array.SetValue(upgraded, i);
                }
                catch (Exception)
                {
                    // Multi-dimensional / fixed-size edge cases — keep as is.
                }
            }
        }

        return array;
    }

    private object UpgradeList(IList list)
    {
        if (_upgraded.TryGetValue(list, out var done))
            return done;

        var listType = list.GetType();
        if (TryRebuildContainer(listType, out var rebuiltType) &&
            rebuiltType.GetConstructor(Type.EmptyTypes) is not null &&
            rebuiltType.GetMethod("Add") is { } add)
        {
            var rebuilt = Activator.CreateInstance(rebuiltType)!;
            _upgraded[list] = rebuilt;
            foreach (var item in list)
                add.Invoke(rebuilt, [Upgrade(item)]);
            return rebuilt;
        }

        // Object- or engine-typed lists can hold reloaded instances in place.
        for (var i = 0; i < list.Count; i++)
        {
            var element = list[i];
            var upgraded = Upgrade(element);
            if (!ReferenceEquals(upgraded, element) || element is not null && element.GetType().IsValueType)
            {
                try
                {
                    list[i] = upgraded;
                }
                catch (Exception)
                {
                    // Read-only lists — their internal state is still walked.
                }
            }
        }

        return list;
    }

    private object UpgradeDictionary(IDictionary dictionary)
    {
        if (_upgraded.TryGetValue(dictionary, out var done))
            return done;

        var dictionaryType = dictionary.GetType();
        if (TryRebuildContainer(dictionaryType, out var rebuiltType) &&
            rebuiltType.GetConstructor(Type.EmptyTypes) is not null)
        {
            var rebuilt = (IDictionary)Activator.CreateInstance(rebuiltType)!;
            _upgraded[dictionary] = rebuilt;

            foreach (DictionaryEntry entry in dictionary)
            {
                var key = Upgrade(entry.Key);
                var value = Upgrade(entry.Value);
                try
                {
                    if (key is null)
                        continue;
                    rebuilt[key] = value;
                }
                catch (Exception)
                {
                    // Key collision or null key — best effort.
                }
            }

            return rebuilt;
        }

        // No parameterless constructor to rebuild with — walk internals instead.
        UpgradeFieldsInPlace(dictionary, dictionaryType);
        return dictionary;
    }

    /// <summary>
    /// For a generic container with any old-assembly type argument, computes
    /// the equivalent container closed over the new types (e.g.
    /// List&lt;Player_old&gt; → List&lt;Player_new&gt;). Returns false when nothing changed.
    /// </summary>
    private bool TryRebuildContainer(Type containerType, out Type rebuiltType)
    {
        if (containerType.IsGenericType)
        {
            var arguments = containerType.GetGenericArguments();
            var newArguments = new Type[arguments.Length];
            var changed = false;
            for (var i = 0; i < arguments.Length; i++)
            {
                if (arguments[i].Assembly == _oldAssembly && ResolveNewType(arguments[i]) is { } newArgument)
                {
                    newArguments[i] = newArgument;
                    changed = true;
                }
                else
                {
                    newArguments[i] = arguments[i];
                }
            }

            if (changed)
            {
                rebuiltType = containerType.GetGenericTypeDefinition().MakeGenericType(newArguments);
                return true;
            }
        }

        rebuiltType = containerType;
        return false;
    }

    private static FieldInfo? FindStaticField(Type type, string name)
    {
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
        {
            var field = t.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field is not null)
                return field;
        }

        return null;
    }
}
