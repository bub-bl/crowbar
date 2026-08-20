using System.Text.Json;

namespace Crowbar.Engine;

/// <summary>
/// Converts a <see cref="CrowbarProjectData"/> to and from its on-disk JSON
/// form (the <c>.crproj</c> asset). The payload is plain data — name, version,
/// identity — so unlike <see cref="LevelSerializer"/> no property reflection or
/// custom converters are involved: the whole document is a straightforward
/// camelCase, pretty-printed JSON object.
///
/// Like the level format, the project format is versioned and read
/// tolerantly. The version check mirrors the level contract: a file written by
/// a <em>newer</em> format than this build understands fails loudly
/// (<see cref="InvalidDataException"/>) instead of being silently corrupted,
/// while a file from an older format loads with a warning (the future migration
/// point).
/// </summary>
public static class CrowbarProjectSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    /// <summary>Serializes a project into its JSON file form.</summary>
    public static string Serialize(CrowbarProjectData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return JsonSerializer.Serialize(data, Options);
    }

    /// <summary>
    /// Parses the JSON text of a <c>.crproj</c> file into its DTO, checking the
    /// format version first. A file from a newer format throws
    /// <see cref="InvalidDataException"/>; an older format loads with a warning
    /// (the migration point for future formats).
    /// </summary>
    public static CrowbarProjectData Deserialize(string json, Action<string>? warning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var data = JsonSerializer.Deserialize<CrowbarProjectData>(json, Options)
                   ?? throw new InvalidDataException("The project file is empty.");

        if (data.Format > CrowbarProjectFile.CurrentFormat)
            throw new InvalidDataException(
                $"The project file uses format {data.Format}, newer than this build's " +
                $"{CrowbarProjectFile.CurrentFormat}. Update the editor to open it.");

        if (data.Format < CrowbarProjectFile.CurrentFormat)
            warning?.Invoke(
                $"Project format {data.Format} is older than {CrowbarProjectFile.CurrentFormat}; " +
                "loading with best-effort compatibility.");

        return data;
    }
}