using System.Reflection;

namespace Crowbar.Engine.Global;

/// <summary>
/// The type registry API — the s&amp;box-style <c>TypeLibrary</c>. Exposed globally
/// as <see cref="GlobalNamespaces.TypeLibrary"/>. Owns an instance of
/// <see cref="TypeRegistry"/> (the <see cref="Registry"/> property), the
/// engine's cacheable type index: assemblies register their component types
/// here (the engine, the game project, a gamemode) and the editor looks them up
/// by name. Everything is cached behind the registry — the library itself holds
/// no state of its own. Instantiating or attaching components is the editor's
/// job, not this library's.
/// </summary>
public sealed class TypeLibrary
{
    /// <summary>
    /// The type registry this library owns: the lazily built, cached index of
    /// every registered component type, keyed by CLR name.
    /// </summary>
    public TypeRegistry Registry { get; } = new();

    /// <summary>Every component type known to the runtime (engine + registered game project).</summary>
    public IReadOnlyList<Type> All => Registry.All;

    /// <summary>Resolves a component type by its CLR name, or null.</summary>
    public Type? Resolve(string typeName) => Registry.Resolve(typeName);

    /// <summary>Registers the component types of a (re)loaded game project assembly.</summary>
    public void Register(Assembly assembly) => Registry.RegisterAssembly(assembly);

    /// <summary>Drops the component types of an assembly that was unloaded (project switch, full reload).</summary>
    public void Unregister(Assembly assembly) => Registry.UnregisterAssembly(assembly);
}
