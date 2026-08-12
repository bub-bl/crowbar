namespace Crowbar.Engine.Scripting;

/// <summary>
/// Per-type migration logic used when a script type is replaced by a reloaded
/// version (the analog of s&amp;box's IInstanceUpgrader). Register implementations
/// through <see cref="ScriptHost.RegisterUpgrader"/>; the host always falls back
/// to <see cref="DefaultInstanceUpgrader"/> when no registered upgrader claims
/// a type, so custom upgraders run first.
/// </summary>
public interface IInstanceUpgrader
{
    /// <summary>Whether this upgrader handles migrating instances of <paramref name="oldType"/>.</summary>
    bool CanUpgrade(Type oldType, Type newType);

    /// <summary>
    /// Migrates one instance from the old assembly to a new-assembly instance
    /// of <paramref name="newType"/>. Call <see cref="IUpgradeContext.RegisterUpgraded"/>
    /// with the created instance before migrating its fields so cycles and
    /// shared references resolve to the same upgraded instance.
    /// </summary>
    object Upgrade(object instance, Type newType, IUpgradeContext context);
}

/// <summary>
/// Provides the reload upgrade operation with type resolution and recursion
/// (with identity preservation) so custom upgraders can migrate nested state.
/// </summary>
public interface IUpgradeContext
{
    /// <summary>The new-assembly type for an old-assembly type, or null when the type was removed.</summary>
    Type? ResolveNewType(Type oldType);

    /// <summary>
    /// Upgrades a value recursively. Old-assembly objects are replaced by
    /// new-assembly instances (identity preserved); engine and BCL objects are
    /// walked in place so their nested script references are upgraded too.
    /// </summary>
    object? Upgrade(object? value);

    /// <summary>Records that <paramref name="old"/> has been upgraded to <paramref name="upgraded"/>.</summary>
    void RegisterUpgraded(object old, object upgraded);
}
