using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crowbar.Engine.Serialization;

namespace Crowbar.Engine;

/// <summary>
/// Converts a runtime <see cref="Level"/> to and from its on-disk JSON form
/// (the <c>.level</c> asset). The file never serializes runtime objects
/// directly: <see cref="Serialize"/> builds the plain-data
/// <see cref="LevelFileData"/> DTO, <see cref="Deserialize"/> parses the JSON
/// back into that DTO (with the version checks and forward-compatibility
/// contract), and <see cref="CreateLevel"/> materializes the DTO into a live
/// level inside a world.
///
/// Component data is driven by the same reflection contract as the editor
/// inspector: writable public properties marked with
/// <see cref="PropertyAttribute"/> (excluding the lifecycle/transform plumbing
/// declared on the component base types). Reflection only discovers the
/// properties, reads their values and writes them back; the JSON representation
/// of every value is delegated to System.Text.Json through the converters
/// registered in <see cref="Options"/> (vectors as arrays, transforms as
/// canonical strings, materials and models as nested objects, enums as names).
/// This serializer therefore knows nothing about how those types map to JSON.
///
/// Deserialization is deliberately tolerant: unknown component types (renamed
/// or removed) and unknown or unreadable properties are skipped with a warning,
/// so a level saved by a newer or differently-built editor still loads. The
/// file format number is checked first: a file written by a <em>newer</em>
/// format than this build understands fails loudly instead of being silently
/// corrupted.
/// </summary>
public static class LevelSerializer
{
    private const BindingFlags InstancePublic = BindingFlags.Instance | BindingFlags.Public;

    /// <summary>The editor/tool version stamped into the file's metadata.</summary>
    private const string EditorVersion = "0.1";

    /// <summary>
    /// The JSON contract of the <c>.level</c> format: camelCase, pretty-printed,
    /// with every engine type the component properties can carry handled by its
    /// own converter.
    /// </summary>
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new Vector2JsonConverter(),
            new Vector3JsonConverter(),
            new Vector4JsonConverter(),
            new TransformJsonConverter(),
            new MaterialJsonConverter(),
            new ModelJsonConverter(),
            new JsonStringEnumConverter()
        }
    };

    // ------------------------------------------------------------------
    // Serialization: runtime level → JSON
    // ------------------------------------------------------------------

    /// <summary>Serializes a live level into its JSON file form.</summary>
    public static string Serialize(Level level)
    {
        ArgumentNullException.ThrowIfNull(level);

        var data = new LevelFileData
        {
            Format = LevelFile.CurrentFormat,
            Id = level.Id,
            Metadata = new LevelFileMetadata(level.Name, EditorVersion),
            Entities = level.Entities.Select(EntityToData).ToList(),
            Attachments = BuildAttachments(level)
        };

        return JsonSerializer.Serialize(data, Options);
    }

    private static LevelEntityData EntityToData(Entity entity)
    {
        var components = new List<LevelComponentData>(entity.Components.Count);
        foreach (var component in entity.Components)
        {
            if (!component.IsValid)
                continue;

            var componentData = new LevelComponentData { Type = component.GetType().Name };

            // Spatial components carry their local transform (the transform
            // section of the inspector) in the canonical string format; the
            // [Property] walk below deliberately excludes it.
            if (component is TransformComponent transform)
                componentData.Transform = TransformJsonConverter.ToCanonical(transform.Local);

            var properties = DescribeProperties(component);
            if (properties is { Count: > 0 })
                componentData.Properties = properties;

            components.Add(componentData);
        }

        return new LevelEntityData
        {
            Id = entity.Id,
            Name = entity.Name,
            Components = components
        };
    }

    /// <summary>
    /// The writable <see cref="PropertyAttribute"/> values of a component, each
    /// serialized by System.Text.Json into a raw <see cref="JsonElement"/>.
    /// Read-only properties (derived values like a directional light's
    /// <c>Direction</c>) are not persisted: they are recomputed from the stored
    /// state on load. Null and unrepresentable values (a type with no JSON
    /// form, or a model that cannot be reproduced) are omitted.
    /// </summary>
    private static Dictionary<string, JsonElement>? DescribeProperties(Component component)
    {
        Dictionary<string, JsonElement>? properties = null;
        foreach (var property in component.GetType()
                     .GetProperties(InstancePublic)
                     .Where(p => p.GetIndexParameters().Length == 0)
                     .Where(p => p.GetGetMethod() is not null && p.GetSetMethod() is not null)
                     .Where(p => p.IsDefined(typeof(PropertyAttribute), inherit: true))
                     .Where(p => !IsInfrastructure(p)))
        {
            var value = property.GetValue(component);
            if (value is null)
                continue;

            JsonElement element;
            try
            {
                element = JsonSerializer.SerializeToElement(value, property.PropertyType, Options);
            }
            catch (NotSupportedException)
            {
                continue; // property type without a JSON representation: omit it
            }

            if (element.ValueKind == JsonValueKind.Null)
                continue; // unrepresentable value (e.g. the error model): omit it

            properties ??= new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            properties[property.Name] = element;
        }

        return properties;
    }

    private static bool IsInfrastructure(PropertyInfo property) =>
        property.DeclaringType == typeof(Component) ||
        property.DeclaringType == typeof(WorldObject) ||
        property.DeclaringType == typeof(TransformComponent);

    /// <summary>Collects the parent → child transform relationships of the level.</summary>
    private static List<LevelAttachmentData> BuildAttachments(Level level)
    {
        var attachments = new List<LevelAttachmentData>();
        foreach (var entity in level.Entities)
        {
            foreach (var component in entity.Components)
            {
                if (component is TransformComponent { Parent: { } parent } &&
                    parent.Entity is { } parentEntity)
                {
                    attachments.Add(new LevelAttachmentData { Parent = parentEntity.Id, Child = entity.Id });
                }
            }
        }

        return attachments;
    }

    // ------------------------------------------------------------------
    // Deserialization: JSON → DTO → runtime level
    // ------------------------------------------------------------------

    /// <summary>
    /// Parses the JSON text of a <c>.level</c> file into its DTO, checking the
    /// format version first. A file from a newer format throws
    /// <see cref="InvalidDataException"/>; an older format loads with a warning
    /// (the migration point for future formats).
    /// </summary>
    public static LevelFileData Deserialize(string json, Action<string>? warning = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var data = JsonSerializer.Deserialize<LevelFileData>(json, Options)
                   ?? throw new InvalidDataException("The level file is empty.");

        if (data.Format > LevelFile.CurrentFormat)
            throw new InvalidDataException(
                $"The level file uses format {data.Format}, newer than this build's " +
                $"{LevelFile.CurrentFormat}. Update the editor to open it.");

        if (data.Format < LevelFile.CurrentFormat)
            warning?.Invoke(
                $"Level format {data.Format} is older than {LevelFile.CurrentFormat}; " +
                "loading with best-effort compatibility.");

        return data;
    }

    /// <summary>
    /// Materializes a parsed level DTO into a live level inside
    /// <paramref name="world"/>. All entities and their components are created
    /// first (two-phase), then the transform attachments are resolved, so a
    /// child can be attached no matter the order the entities appear in the
    /// file. Warnings are reported through <paramref name="warning"/> (console
    /// when null) and never fail the load — that is the forward-compatibility
    /// contract.
    /// </summary>
    public static Level CreateLevel(World world, LevelFileData data, Action<string>? warning = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(data);
        warning ??= message => Console.WriteLine($"[Level] {message}");

        var level = world.CreateLevel(data.Metadata?.Name);
        level.Id = data.Id;

        // Phase 1: entities and components.
        var byId = new Dictionary<Guid, Entity>(data.Entities.Count);
        foreach (var entityData in data.Entities)
        {
            if (byId.ContainsKey(entityData.Id))
            {
                warning($"Duplicate entity id '{entityData.Id}' ignored.");
                continue;
            }

            var entity = world.SpawnEntity(entityData.Name, level);
            entity.Id = entityData.Id;
            byId[entity.Id] = entity;

            foreach (var componentData in entityData.Components)
                AddComponent(entity, componentData, warning);
        }

        // Phase 2: transform attachments (parents already exist by now).
        foreach (var attachment in data.Attachments)
        {
            if (!byId.TryGetValue(attachment.Parent, out var parent) ||
                !byId.TryGetValue(attachment.Child, out var child))
            {
                warning("Attachment references an unknown entity; skipped.");
                continue;
            }

            try
            {
                // Both local transforms are stored as-is, so attaching with
                // keepWorldTransform: false reproduces the saved hierarchy
                // exactly (world transforms are recomposed from the same data).
                child.AttachTo(parent, keepWorldTransform: false);
            }
            catch (Exception ex)
            {
                warning($"Failed to attach '{child.Name}' to '{parent.Name}': {ex.Message}");
            }
        }

        return level;
    }

    private static void AddComponent(Entity entity, LevelComponentData componentData, Action<string> warning)
    {
        var type = ComponentTypeRegistry.Resolve(componentData.Type);
        if (type is null)
        {
            warning($"Unknown component type '{componentData.Type}' on '{entity.Name}'; skipped (forward compatibility).");
            return;
        }

        Component component;
        try
        {
            component = (Component)Activator.CreateInstance(type)!;
            entity.AddComponent(component);
        }
        catch (Exception ex)
        {
            warning($"Failed to create component '{componentData.Type}' on '{entity.Name}': {ex.Message}");
            return;
        }

        if (component is TransformComponent transform && componentData.Transform is { Length: > 0 } transformText)
        {
            try
            {
                transform.Local = TransformJsonConverter.Parse(transformText);
            }
            catch (JsonException)
            {
                warning($"Invalid transform on '{entity.Name}.{componentData.Type}'; using identity.");
            }
        }

        if (componentData.Properties is null)
            return;

        foreach (var (name, element) in componentData.Properties)
        {
            var property = type.GetProperty(name, InstancePublic);
            if (property?.CanWrite != true || property.GetSetMethod() is null)
                continue; // unknown or read-only property → forward compatibility

            try
            {
                var value = JsonSerializer.Deserialize(element, property.PropertyType, Options);
                property.SetValue(component, value);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                warning($"Property '{name}' on '{entity.Name}.{type.Name}' could not be restored.");
            }
        }
    }
}
