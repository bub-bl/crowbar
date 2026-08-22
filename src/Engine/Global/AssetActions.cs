using System.Reflection;

namespace Crowbar.Engine.Global;

/// <summary>A declared context-menu action resolved for one file: id, label and menu ordering.</summary>
internal readonly record struct AssetActionInfo(string Id, string Label, bool IsDanger, int Order);

/// <summary>
/// The registry of context-menu actions declared with <see cref="AssetActionAttribute"/>
/// on static methods — the extension point that lets any game assembly contribute
/// actions to the editor's content panel without touching the editor: declare a
/// static void method taking <see cref="AssetActionContext"/>, mark it
/// <c>[AssetAction]</c>, and the action shows on every file whose extension
/// matches (or on every file when no filter is given). <see cref="Register"/>
/// scans a registered assembly's methods — the composition root calls it
/// alongside <see cref="ResourceLibrary.Register"/> for the engine, editor and
/// game assemblies — the editor host publishes <see cref="ForPath"/> results in
/// the content snapshot, and executes handlers through <see cref="Execute"/>.
/// <see cref="Unregister"/> drops an assembly's actions on hot reload, like
/// <see cref="ResourceLibrary.Unregister"/>.
///
/// The registry is editor integration, not game-facing API: game code only
/// declares actions with <see cref="AssetActionAttribute"/> and receives the
/// <see cref="AssetActionContext"/>. It is internal to the engine and reached
/// by the editor through InternalsVisibleTo.
/// </summary>
internal static class AssetActions
{
    private static readonly Lock Lock = new();
    private static readonly Dictionary<string, Entry> Actions = new(StringComparer.Ordinal);
    private static readonly HashSet<Assembly> Registered = [];

    private sealed class Entry
    {
        public required string Id;
        public required string Label;
        public required bool IsDanger;
        public required int Order;
        public required Action<AssetActionContext> Handler;
        public required Assembly Owner;

        /// <summary>Matching extensions (normalized); empty means the action applies to every file.</summary>
        public required string[] Extensions;
    }

    /// <summary>
    /// Registers the <c>[AssetAction]</c> methods of <paramref name="assembly"/>:
    /// each marked static method becomes one action whose filter is the union of
    /// its attributes' extensions (or every file when none is given). Idempotent
    /// like <see cref="ResourceLibrary.Register"/>: an assembly already registered
    /// is skipped. Two methods declaring the same action id fail fast — ids are
    /// the dispatch key, so they must be unique across registered assemblies.
    /// </summary>
    public static void Register(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Lock)
        {
            if (!Registered.Add(assembly))
                return;

            foreach (var type in assembly.GetTypes())
            {
                foreach (var method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy))
                {
                    var attributes = method.GetCustomAttributes<AssetActionAttribute>().ToArray();
                    if (attributes.Length == 0)
                        continue;

                    var first = attributes[0];
                    var extensions = new List<string>();
                    foreach (var attribute in attributes)
                    {
                        if (attribute.Extensions is not null)
                        {
                            foreach (var extension in attribute.Extensions)
                            {
                                var normalized = NormalizeExtension(extension);
                                if (normalized.Length > 0)
                                    extensions.Add(normalized);
                            }
                        }

                        if (attribute.AssetType is { } assetType)
                        {
                            // A type filter resolves to the type's registered
                            // extensions, so the action and the loader agree on
                            // which files it covers.
                            var declared = assetType.GetCustomAttribute<AssetTypeAttribute>()?.Extensions;
                            if (declared is null)
                            {
                                throw new InvalidOperationException(
                                    $"The [AssetAction] on '{method.DeclaringType?.Name}.{method.Name}' references " +
                                    $"'{assetType.Name}', which is not marked [AssetType]; an asset action can only " +
                                    "target a registered asset type.");
                            }
                            foreach (var extension in declared)
                            {
                                var normalized = NormalizeExtension(extension);
                                if (normalized.Length > 0)
                                    extensions.Add(normalized);
                            }
                        }
                    }

                    var handler = CompileHandler(method, first.Id);
                    if (Actions.ContainsKey(first.Id))
                    {
                        throw new InvalidOperationException(
                            $"The asset action id '{first.Id}' is already declared (by '{first.Label}'); action ids " +
                            "must be unique across registered assemblies.");
                    }

                    Actions[first.Id] = new Entry
                    {
                        Id = first.Id,
                        Label = first.Label,
                        IsDanger = first.IsDanger,
                        Order = first.Order,
                        Handler = handler,
                        Owner = assembly,
                        Extensions = extensions.ToArray()
                    };
                }
            }
        }
    }

    /// <summary>Drops every action declared by <paramref name="assembly"/> (hot reload, project switch).</summary>
    public static void Unregister(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);

        lock (Lock)
        {
            Registered.Remove(assembly);
            foreach (var id in Actions.Where(entry => entry.Value.Owner == assembly).Select(entry => entry.Key).ToArray())
                Actions.Remove(id);
        }
    }

    /// <summary>
    /// The actions applying to the file at <paramref name="path"/>, ordered by
    /// <see cref="AssetActionAttribute.Order"/> then id. The match is by the
    /// path's extension; an action without any declared filter applies to every
    /// file.
    /// </summary>
    public static IReadOnlyList<AssetActionInfo> ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var extension = NormalizeExtension(Path.GetExtension(path));

        lock (Lock)
        {
            var result = new List<AssetActionInfo>(Actions.Count);
            foreach (var entry in Actions.Values)
            {
                if (entry.Extensions.Length > 0 && !entry.Extensions.Contains(extension))
                    continue;
                result.Add(new AssetActionInfo(entry.Id, entry.Label, entry.IsDanger, entry.Order));
            }

            result.Sort(static (a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order) : string.Compare(a.Id, b.Id, StringComparison.Ordinal));
            return result;
        }
    }

    /// <summary>
    /// Runs the handler of the action <paramref name="id"/> with the file's
    /// logical content path. Returns false when no registered action has that
    /// id (e.g. the declaring game assembly was hot-reloaded away), so the host
    /// can report the miss. <paramref name="notify"/> is wired into the
    /// context's <see cref="AssetActionContext.Notify"/> callback.
    /// </summary>
    public static bool Execute(string id, string path, Action<string, string, string>? notify = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        Action<AssetActionContext> handler;
        lock (Lock)
        {
            if (!Actions.TryGetValue(id, out var entry))
                return false;
            handler = entry.Handler;
        }

        // Invoked outside the lock: a handler may query or execute other
        // actions without deadlocking.
        handler(new AssetActionContext(path, notify));
        return true;
    }

    /// <summary>
    /// Compiles the marked method into the action's handler. The signature is
    /// enforced at registration (a static void method taking a single
    /// AssetActionContext) so a bad declaration fails fast at startup instead
    /// of on first click.
    /// </summary>
    private static Action<AssetActionContext> CompileHandler(MethodInfo method, string id)
    {
        var parameters = method.GetParameters();
        if (method.ReturnType != typeof(void) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(AssetActionContext))
        {
            throw new InvalidOperationException(
                $"The asset action '{id}' on '{method.DeclaringType?.Name}.{method.Name}' must be a static method " +
                "returning void and taking a single AssetActionContext parameter.");
        }

        return (Action<AssetActionContext>)method.CreateDelegate(typeof(Action<AssetActionContext>));
    }

    /// <summary>Canonical extension form: trimmed, leading dot stripped, lower-cased.</summary>
    private static string NormalizeExtension(string extension) =>
        extension.Trim().TrimStart('.').ToLowerInvariant();
}
