namespace Crowbar.Engine.Global;

/// <summary>
/// Marks a static method or property as a console command (convar). Methods
/// must be <c>void()</c> or <c>void(string[] args)</c>. Properties become
/// convars: <c>get</c> reads the value, <c>set</c> writes it. The
/// <see cref="ConCmdRegistry"/> discovers these at startup and the console UI
/// dispatches typed commands to them.
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = false)]
public sealed class ConCmdAttribute : Attribute
{
    /// <summary>The command name (e.g. "help", "quit", "fps_max").</summary>
    public string Name { get; }

    /// <summary>Short description shown by the <c>help</c> command.</summary>
    public string Description { get; }

    public ConCmdAttribute(string name, string description = "")
    {
        Name = name;
        Description = description;
    }
}