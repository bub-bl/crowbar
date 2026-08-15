using System.Text;

namespace Crowbar.FileSystems;

/// <summary>
/// A normalized, uniform path used by the engine and editor to address content.
/// Paths use the <c>/</c> separator on every platform and are normalized
/// (repeated separators, trailing separators and <c>.</c>/<c>..</c> segments are
/// collapsed), so they are stable dictionary keys and behave identically across
/// operating systems.
///
/// This is Crowbar.FileSystems's own path type with no dependency on a backing
/// filesystem library: the engine and editor code against this type, and each
/// <see cref="IFileSystem"/> implementation maps it onto its own coordinate
/// space.
/// </summary>
public readonly struct FilePath : IEquatable<FilePath>
{
    private readonly string _path; // normalized; "" = empty

    /// <summary>An empty path (<c>""</c>).</summary>
    public static readonly FilePath Empty = new(string.Empty);

    /// <summary>The root path (<c>/</c>).</summary>
    public static readonly FilePath Root = new("/");

    public FilePath(string path) => _path = Normalize(path ?? string.Empty);

    /// <summary>The normalized path (<c>/</c>-separated, no trailing separator except the root).</summary>
    public string FullName => _path;

    /// <summary>True when the path starts with a leading <c>/</c>.</summary>
    public bool IsAbsolute => _path.Length > 0 && _path[0] == '/';

    /// <summary>True when the path does not start with a leading <c>/</c>.</summary>
    public bool IsRelative => !IsAbsolute;

    /// <summary>True when the path is the empty string.</summary>
    public bool IsEmpty => _path.Length == 0;

    public static implicit operator FilePath(string path) => new(path);

    public static explicit operator string(FilePath path) => path.FullName;

    /// <summary>Combines two paths (an absolute right side wins).</summary>
    public static FilePath Combine(FilePath left, FilePath right)
        => right.IsAbsolute || left.IsEmpty ? right : new FilePath(left._path + "/" + right._path);

    /// <summary>The <c>/</c> operator, equivalent to <see cref="Combine"/>.</summary>
    public static FilePath operator /(FilePath left, FilePath right) => Combine(left, right);

    /// <summary>The directory portion of the path (empty for a bare name, root for a top-level absolute path).</summary>
    public FilePath GetDirectory()
    {
        if (_path is "/" or "")
            return Empty;

        var lastSeparator = _path.LastIndexOf('/');
        return lastSeparator switch
        {
            > 0 => new FilePath(_path[..lastSeparator]),
            0 => Root,
            _ => Empty
        };
    }

    /// <summary>The final path component, including its extension.</summary>
    public string GetName()
    {
        if (_path.Length == 0)
            return string.Empty;

        var lastSeparator = _path.LastIndexOf('/');
        return lastSeparator < 0 ? _path : _path[(lastSeparator + 1)..];
    }

    /// <summary>The final path component without its extension (a leading dot counts as a hidden name, not an extension).</summary>
    public string? GetNameWithoutExtension()
    {
        var name = GetName();
        var lastDot = name.LastIndexOf('.');
        return lastDot > 0 ? name[..lastDot] : name;
    }

    /// <summary>The extension with a leading dot, or an empty string when there is none.</summary>
    public string? GetExtensionWithDot()
    {
        var name = GetName();
        var lastDot = name.LastIndexOf('.');
        return lastDot > 0 && lastDot < name.Length - 1 ? name[lastDot..] : string.Empty;
    }

    /// <summary>Replaces the path's extension (with or without a leading dot; null removes it).</summary>
    public FilePath ChangeExtension(string? extension)
    {
        var withoutExtension = RemoveExtension();
        if (string.IsNullOrEmpty(extension))
            return withoutExtension;

        var dot = extension[0] == '.' ? extension : "." + extension;
        return new FilePath(withoutExtension.FullName + dot);
    }

    /// <summary>Adds a leading <c>/</c> if the path is relative.</summary>
    public FilePath ToAbsolute() => IsAbsolute ? this : Root / this;

    /// <summary>Removes the leading <c>/</c> if the path is absolute.</summary>
    public FilePath ToRelative() => IsAbsolute ? (_path == "/" ? Empty : new FilePath(_path[1..])) : this;

    /// <summary>True when the path is inside the given directory (optionally recursively).</summary>
    public bool IsInDirectory(FilePath directory, bool recursive)
    {
        if (directory.IsEmpty)
            return false;

        var target = _path;
        var dir = directory._path;
        if (dir.Length > 1 && dir[^1] == '/')
            dir = dir[..^1];

        if (!target.StartsWith(dir, StringComparison.Ordinal))
            return false;
        if (target.Length == dir.Length)
            return true;
        if (target[dir.Length] != '/')
            return false;
        return recursive || target.IndexOf('/', dir.Length + 1) < 0;
    }

    public bool Equals(FilePath other) => string.Equals(_path, other._path, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is FilePath other && Equals(other);

    public override int GetHashCode() => _path.GetHashCode();

    public override string ToString() => _path;

    public static bool operator ==(FilePath left, FilePath right) => left.Equals(right);

    public static bool operator !=(FilePath left, FilePath right) => !left.Equals(right);

    private FilePath RemoveExtension()
    {
        var lastSeparator = _path.LastIndexOf('/');
        var lastDot = _path.LastIndexOf('.');
        return lastDot > lastSeparator + 1 ? new FilePath(_path[..lastDot]) : this;
    }

    private static string Normalize(string path)
    {
        if (path.Length == 0)
            return string.Empty;

        var isAbsolute = path[0] == '/' || path[0] == '\\';
        var segments = new List<string>();
        var i = 0;
        while (i < path.Length)
        {
            while (i < path.Length && (path[i] == '/' || path[i] == '\\'))
                i++;
            var start = i;
            while (i < path.Length && path[i] != '/' && path[i] != '\\')
                i++;
            if (start == i)
                continue; // trailing separator

            var segment = path[start..i];
            if (segment == ".")
                continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..")
                    segments.RemoveAt(segments.Count - 1);
                else if (!isAbsolute)
                    segments.Add("..");
                continue;
            }

            segments.Add(segment);
        }

        if (segments.Count == 0)
            return isAbsolute ? "/" : string.Empty;

        var builder = new StringBuilder();
        if (isAbsolute)
            builder.Append('/');
        for (var k = 0; k < segments.Count; k++)
        {
            if (k > 0)
                builder.Append('/');
            builder.Append(segments[k]);
        }

        return builder.ToString();
    }
}
