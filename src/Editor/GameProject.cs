using Crowbar.Engine.Audio;
using Crowbar.Engine.Scripting;
using Crowbar.FileSystems;
using Crowbar.UI;

namespace Crowbar.Editor;

/// <summary>
/// The game project: a real .NET library project (Game/Game.csproj) referencing
/// the engine. It is loaded at runtime into its own collectible assembly context
/// — the engine and the editor never reference the project — and hot-reloaded on
/// every edit (IL fast path when only method bodies change, otherwise a full
/// reload with state migration). Its component types are registered in
/// <see cref="GlobalNamespaces.TypeLibrary"/> so the editor can attach them; its
/// code publishes editor status through <see cref="StatusBar"/>. Owned by the
/// <see cref="Editor"/> instance; compilation results surface through the
/// injected <see cref="NotificationWindow"/>.
/// </summary>
public sealed class GameProject
{
    private readonly NotificationWindow _notificationWindow;
    private ScriptHost? _host;

    public GameProject(NotificationWindow notificationWindow) => _notificationWindow = notificationWindow;

    /// <summary>
    /// (Re)loads the game project from the current project root: compiles every
    /// *.cs file under <see cref="FileSystem.Project"/>'s root into the
    /// collectible assembly context, registers its component types so the editor
    /// can attach them, and watches the directory for hot reload. Calling it
    /// again (project switch) compiles the new project from scratch. Must run
    /// before the level is loaded: only its registered component types resolve
    /// when a saved document is materialized.
    /// </summary>
    public void Start()
    {
        try
        {
            // The game project is the whole project (FileSystem.Project): "." is its root.
            const string gameDirectory = ".";
            var host = EnsureHost();
            var previous = host.Current;
            host.WatchDirectory(gameDirectory, "GameProject");
            // A project switch or full reload compiled a new assembly: drop the
            // previous generation's component types, then register the fresh
            // project's so they become attachable.
            if (previous is not null)
                TypeLibrary.Unregister(previous.Assembly);
            if (host.Current is { } current)
                TypeLibrary.Register(current.Assembly);
            // The project's own code publishes editor status through
            // Editor.StatusBar; clear a previous project's entries so only the
            // live project's registrations survive.
            StatusBar.Clear();
            Log.Info($"[Scripting] Game project loaded: {host.Current?.TypesByFullName.Count ?? 0} type(s) from the project");
        }
        catch (Exception ex)
        {
            Log.Warn($"[Scripting] Failed to load the game project: {ex.Message}");
            UiNotifications.Show("Script", "Failed to load the game project", "error");
        }
    }

    /// <summary>Applies detected hot reloads and prunes the expired toasts (call once per frame).</summary>
    public void Update()
    {
        _host?.Update();
        UiNotifications.PruneExpired();
    }

    /// <summary>Stops watching and unloads the game assembly (application shutdown).</summary>
    public void Dispose()
    {
        _host?.Dispose();
        _host = null;
    }

    private ScriptHost EnsureHost()
    {
        if (_host is not null)
            return _host;
        _host = new ScriptHost(new ScriptCompiler(
        [
            typeof(ScriptHost).Assembly,        // Crowbar.Engine
            typeof(FileSystemService).Assembly, // Crowbar.FileSystem
            typeof(PropertyEditor).Assembly,    // Crowbar.UI
        ]));
        _host.Reloaded += OnReloaded;
        _host.ReloadFailed += OnReloadFailed;
        return _host;
    }

    private void OnReloaded(ScriptReloadedEventArgs e)
    {
        var detail = e.Mode switch
        {
            ScriptReloadMode.FastPath => $"IL fast path: {e.PatchedMethods} method(s) patched in {e.Duration.TotalMilliseconds:0} ms",
            ScriptReloadMode.FullReload => $"Full reload: {e.UpgradedInstances} instance(s) migrated in {e.Duration.TotalMilliseconds:0} ms",
            _ => $"Loaded in {e.Duration.TotalMilliseconds:0} ms"
        };
        Log.Info($"[Scripting] Hot reload OK ({e.Mode}): {detail}");
        UiNotifications.Show("Hot reload", detail, "success");
        // A compilation result is exactly what the popup exists to display.
        _notificationWindow.Show();

        // A full reload swaps the live assembly: point the registered game
        // components at the new generation, so the Add Component list offers the
        // reloaded types (not the unloaded ones). The IL fast path keeps the
        // live assembly unchanged, so the types stay valid.
        if (e.Mode == ScriptReloadMode.FullReload)
        {
            // A full reload only runs when a previous assembly exists.
            TypeLibrary.Unregister(e.Previous!.Assembly);
            TypeLibrary.Register(e.Current.Assembly);
        }
    }

    private void OnReloadFailed(ScriptReloadFailedEventArgs e)
    {
        Log.Warn($"[Scripting] Hot reload FAILED: {e.Error.Message}");
        Audio.Play("Assets/Sounds/ui_compilation_error.wav", bus: AudioBusName.Ui);
        UiNotifications.Show("Hot reload", $"Failed: {e.Error.Message}", "error");
        // A compilation error is exactly what the popup exists to display.
        _notificationWindow.Show();
    }
}
