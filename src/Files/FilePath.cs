using Zio;

namespace Crowbar.Files;

/// <summary>
/// A normalized, uniform path used by the engine and editor to address content.
/// Paths use the <c>/</c> separator on every platform and are normalized (trailing
/// separators and <c>.</c>/<c>..</c> segments are squashed), so they are stable
/// dictionary keys and behave identically across operating systems.
///
/// This is Crowbar.Files' own path type — it wraps Zio's <see cref="UPath"/>
/// internally, so consumers depend only on <c>Crowbar.Files</c>, never on the
/// underlying filesystem library.
/// </summary>
public readonly struct FilePath : IEquatable<FilePath>
{
    internal readonly UPath Path;

    /// <summary>An empty path (<c>""</c>).</summary>
    public static readonly FilePath Empty = new(UPath.Empty);

    /// <summary>The root path (<c>/</c>).</summary>
    public static readonly FilePath Root = new(UPath.Root);

    public FilePath(string path) => Path = new UPath(path);

    internal FilePath(UPath path) => Path = path;

    /// <summary>The normalized path.</summary>
    public string FullName => Path.FullName;

    /// <summary>True when the path starts with a leading <c>/</c>.</summary>
    public bool IsAbsolute => Path.IsAbsolute;

    /// <summary>True when the path does not start with a leading <c>/</c>.</summary>
    public bool IsRelative => Path.IsRelative;

    /// <summary>True when the path is the empty string.</summary>
    public bool IsEmpty => Path.IsEmpty;

    /// <summary>True when the path is null.</summary>
    public bool IsNull => Path.IsNull;

    public static implicit operator FilePath(string path) => new(path);

    public static explicit operator string(FilePath path) => path.FullName;

    /// <summary>Combines two paths (if the right side is absolute, it wins).</summary>
    public static FilePath Combine(FilePath left, FilePath right) => new(UPath.Combine(left.Path, right.Path));

    /// <summary>The <c>/</c> operator, equivalent to <see cref="Combine"/>.</summary>
    public static FilePath operator /(FilePath left, FilePath right) => Combine(left, right);

    /// <summary>The directory portion of the path.</summary>
    public FilePath GetDirectory() => new(Path.GetDirectory());

    /// <summary>The final path component, including its extension.</summary>
    public string GetName() => Path.GetName();

    /// <summary>The final path component without its extension (null when the path is null).</summary>
    public string? GetNameWithoutExtension() => Path.GetNameWithoutExtension();

    /// <summary>The extension with a leading dot, or null/empty when there is none.</summary>
    public string? GetExtensionWithDot() => Path.GetExtensionWithDot();

    /// <summary>Replaces the path's extension (with or without a leading dot).</summary>
    public FilePath ChangeExtension(string extension) => new(Path.ChangeExtension(extension));

    /// <summary>Adds a leading <c>/</c> if the path is relative.</summary>
    public FilePath ToAbsolute() => new(Path.ToAbsolute());

    /// <summary>Removes the leading <c>/</c> if the path is absolute.</summary>
    public FilePath ToRelative() => new(Path.ToRelative());

    /// <summary>True when the path is inside the given directory (optionally recursively).</summary>
    public bool IsInDirectory(FilePath directory, bool recursive) => Path.IsInDirectory(directory.Path, recursive);

    public bool Equals(FilePath other) => Path.Equals(other.Path);

    public override bool Equals(object? obj) => obj is FilePath other && Equals(other);

    public override int GetHashCode() => Path.GetHashCode();

    public override string ToString() => FullName;

    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);

    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);
}
