using Crowbar.UI;

namespace Crowbar.UI.Tests;

/// <summary>Helpers shared by the UI test suites.</summary>
public static class TestUi
{
    /// <summary>Creates a UiSystem sized to a fixed viewport.</summary>
    public static UiSystem Create(int width = 640, int height = 480)
    {
        var ui = new UiSystem();
        ui.Screen.SetViewport(width, height);
        ui.Renderer.Resize(width, height);
        return ui;
    }

    /// <summary>Depth-first search for the first panel matching the predicate.</summary>
    public static Panel? Find(Panel? root, Func<Panel, bool> predicate)
    {
        if (root is null) return null;
        if (predicate(root)) return root;
        foreach (var child in root.Children)
        {
            var result = Find(child, predicate);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>Every panel matching the predicate, in document order.</summary>
    public static List<Panel> FindAll(Panel? root, Func<Panel, bool> predicate)
    {
        var result = new List<Panel>();
        if (root is null) return result;
        if (predicate(root)) result.Add(root);
        foreach (var child in root.Children) result.AddRange(FindAll(child, predicate));
        return result;
    }

    /// <summary>All visible text in document order.</summary>
    public static List<string> Texts(Panel? root)
    {
        var result = new List<string>();
        if (root is null) return result;
        if (!string.IsNullOrEmpty(root.Text)) result.Add(root.Text);
        foreach (var child in root.Children) result.AddRange(Texts(child));
        return result;
    }

    /// <summary>Creates a unique temporary directory that is deleted on dispose.</summary>
    public static TempDirectory TempDir(string prefix) => new(prefix);
}

/// <summary>Disposable temporary directory for file-based registration tests.</summary>
public sealed class TempDirectory : IDisposable
{
    public string Path { get; }

    public TempDirectory(string prefix)
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Write(string fileName, string content)
    {
        var path = System.IO.Path.Combine(Path, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
    }
}
