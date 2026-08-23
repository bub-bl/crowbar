using System.Reflection;

namespace Crowbar.Engine.Global;

/// <summary>
/// Discovers and dispatches console commands declared with
/// <see cref="ConCmdAttribute"/> on static methods and properties. Call
/// <see cref="Discover(Assembly)"/> for each assembly whose commands should
/// be registered, then <see cref="Execute(string)"/> from the console input.
/// </summary>
public static class ConCmdRegistry
{
    private static readonly Dictionary<string, ConCmd> Commands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every registered command.</summary>
    public static IReadOnlyDictionary<string, ConCmd> All => Commands;

    static ConCmdRegistry()
    {
        // Built-in commands.
        Commands["help"] = new ConCmd("help", "Lists available console commands.", args =>
        {
            var all = Commands.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase).ToArray();
            var lines = new List<string>(all.Length);
            foreach (var name in all)
            {
                var desc = Commands[name].Description;
                lines.Add(desc.Length > 0 ? $"{name} — {desc}" : name);
            }
            // No logging here: the command list is returned as sub-lines so the
            // console can attach them to the echoed command line entry, exactly
            // like an error's stack trace.
            return lines;
        });
    }

    /// <summary>
    /// Scans <paramref name="assembly"/> for <c>[ConCmd]</c>-decorated static
    /// methods and properties, registering each as a console command. Already
    /// registered names are replaced (last assembly wins), so a game project
    /// can shadow an engine command.
    /// </summary>
    public static void Discover(Assembly assembly)
    {
        foreach (var type in assembly.GetExportedTypes())
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var attr = method.GetCustomAttribute<ConCmdAttribute>();
                if (attr is null) continue;

                if (method.ReturnType != typeof(void))
                {
                    GlobalNamespaces.Log.Warn($"[ConCmd] '{attr.Name}' must return void; skipped.");
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length > 1 || (parameters.Length == 1 && parameters[0].ParameterType != typeof(string[])))
                {
                    GlobalNamespaces.Log.Warn($"[ConCmd] '{attr.Name}' signature must be void() or void(string[] args); skipped.");
                    continue;
                }

                var takesArgs = parameters.Length == 1;
                Commands[attr.Name] = new ConCmd(attr.Name, attr.Description, args =>
                {
                    try
                    {
                        if (takesArgs)
                            method.Invoke(null, [args]);
                        else
                            method.Invoke(null, null);
                    }
                    catch (TargetInvocationException ex)
                    {
                        GlobalNamespaces.Log.Error($"[ConCmd] '{attr.Name}': {ex.InnerException?.Message ?? ex.Message}");
                    }
                    return null;
                });
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                var attr = property.GetCustomAttribute<ConCmdAttribute>();
                if (attr is null) continue;

                var getter = property.GetMethod;
                var setter = property.SetMethod;

                Commands[attr.Name] = new ConCmd(attr.Name, attr.Description, args =>
                {
                    try
                    {
                        if (args.Length == 0)
                        {
                            // Read the current value.
                            if (getter is not null)
                            {
                                var value = getter.Invoke(null, null);
                                GlobalNamespaces.Log.Info($"[ConVar] {attr.Name} = {value}");
                            }
                            else
                            {
                                GlobalNamespaces.Log.Warn($"[ConVar] '{attr.Name}' is write-only.");
                            }
                        }
                        else if (setter is not null)
                        {
                            // Write the new value.
                            var targetType = property.PropertyType;
                            var value = Convert.ChangeType(string.Join(" ", args), targetType);
                            setter.Invoke(null, [value]);
                            GlobalNamespaces.Log.Info($"[ConVar] {attr.Name} = {value}");
                        }
                        else
                        {
                            GlobalNamespaces.Log.Warn($"[ConVar] '{attr.Name}' is read-only.");
                        }
                    }
                    catch (TargetInvocationException ex)
                    {
                        GlobalNamespaces.Log.Error($"[ConVar] '{attr.Name}': {ex.InnerException?.Message ?? ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        GlobalNamespaces.Log.Error($"[ConVar] '{attr.Name}': {ex.Message}");
                    }
                    return null;
                });
            }
        }
    }

    /// <summary>
    /// Tokenizes <paramref name="input"/> and dispatches to the first matching
    /// command. Unknown commands are logged as a warning. Input starting with
    /// whitespace, empty, or a comment (<c>//</c>) is silently ignored.
    /// Returns sub-lines produced by the command (e.g. <c>help</c>'s command
    /// list) for the caller to attach to its echoed command line, or
    /// <see langword="null"/> when the command logged its own output.
    /// </summary>
    public static IReadOnlyList<string>? Execute(string input)
    {
        var trimmed = input.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal))
            return null;

        var parts = Tokenize(trimmed);
        if (parts.Length == 0) return null;

        var name = parts[0];
        var args = parts.Length > 1 ? parts[1..] : [];

        if (Commands.TryGetValue(name, out var command))
        {
            try
            {
                return command.Execute(args);
            }
            catch (Exception ex)
            {
                GlobalNamespaces.Log.Error($"Command '{name}' failed: {ex.Message}", ex);
                return null;
            }
        }

        GlobalNamespaces.Log.Warn($"Unknown command: '{name}'. Type 'help' for available commands.");
        return null;
    }

    /// <summary>
    /// Returns command names that start with <paramref name="prefix"/>, for
    /// autocomplete suggestions.
    /// </summary>
    public static string[] GetSuggestions(string prefix) =>
        Commands.Keys.Where(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>
    /// Splits a console command line into tokens, respecting double-quoted
    /// strings (spaces inside quotes are part of the token).
    /// </summary>
    private static string[] Tokenize(string input)
    {
        var tokens = new List<string>();
        var pos = 0;
        while (pos < input.Length)
        {
            // Skip whitespace.
            while (pos < input.Length && char.IsWhiteSpace(input[pos]))
                pos++;
            if (pos >= input.Length) break;

            if (input[pos] == '"')
            {
                pos++; // skip opening quote
                var start = pos;
                while (pos < input.Length && input[pos] != '"')
                    pos++;
                tokens.Add(input[start..pos]);
                if (pos < input.Length) pos++; // skip closing quote
            }
            else
            {
                var start = pos;
                while (pos < input.Length && !char.IsWhiteSpace(input[pos]))
                    pos++;
                tokens.Add(input[start..pos]);
            }
        }
        return [.. tokens];
    }
}

/// <summary>A registered console command.</summary>
public sealed class ConCmd
{
    /// <summary>The command name (e.g. "help", "quit").</summary>
    public string Name { get; }

    /// <summary>Short description shown by the <c>help</c> command.</summary>
    public string Description { get; }

    private readonly Func<string[], IReadOnlyList<string>?> _execute;

    internal ConCmd(string name, string description, Func<string[], IReadOnlyList<string>?> execute)
    {
        Name = name;
        Description = description;
        _execute = execute;
    }

    /// <summary>
    /// Invokes the command with the given arguments. Returns sub-lines for the
    /// console to display under the echoed command line, or <see langword="null"/>
    /// when the command logged its own output.
    /// </summary>
    public IReadOnlyList<string>? Execute(string[] args) => _execute(args);
}