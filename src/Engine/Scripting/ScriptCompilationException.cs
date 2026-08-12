namespace Crowbar.Engine.Scripting;

/// <summary>Thrown when a set of script files fails to compile.</summary>
public sealed class ScriptCompilationException : Exception
{
    public ScriptCompilationException(string message) : base(message)
    {
    }

    public ScriptCompilationException(string message, Exception inner) : base(message, inner)
    {
    }
}
