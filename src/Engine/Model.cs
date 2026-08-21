using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Crowbar.FileSystems;
using Silk.NET.Assimp;
using AssimpMaterial = Silk.NET.Assimp.Material;
using AssimpMesh = Silk.NET.Assimp.Mesh;

namespace Crowbar.Engine;

/// <summary>A single CPU-side vertex: position, normal, tangent and texture coordinate.</summary>
public readonly record struct MeshVertex(Vector3 Position, Vector3 Normal, Vector4 Tangent, Vector2 TexCoord);

/// <summary>
/// One drawable geometry unit: vertices plus triangle indices. Meshes are
/// plain CPU data; the renderer uploads them into GPU buffers and keeps its
/// own representation keyed by this instance.
/// </summary>
public sealed class Mesh
{
    public string Name { get; }
    public MeshVertex[] Vertices { get; }
    public uint[] Indices { get; }

    /// <summary>
    /// The material this mesh is drawn with, or null to fall back to the
    /// renderer's material (then the engine default). Imported models attach
    /// one material per mesh so a single model can carry several PBR surfaces.
    /// </summary>
    public Material? Material { get; }

    public Mesh(string name, MeshVertex[] vertices, uint[] indices, Material? material = null)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Mesh" : name;
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
        Material = material;
    }
}

/// <summary>Coarse-grained stage of a <see cref="Model"/> import.</summary>
public enum ModelLoadStage
{
    /// <summary>Checking the file and its external buffers.</summary>
    Validating,
    /// <summary>Running the Assimp importer.</summary>
    Importing,
    /// <summary>Converting materials and their texture bindings.</summary>
    ConvertingMaterials,
    /// <summary>Converting mesh geometry.</summary>
    ConvertingMeshes,
    /// <summary>The model is ready.</summary>
    Done
}

/// <summary>A progress update emitted by <see cref="Model.LoadAsync"/>.</summary>
public readonly record struct ModelLoadProgress(ModelLoadStage Stage, float Fraction);

/// <summary>
/// A 3D model: a named collection of <see cref="Mesh"/>es plus the
/// <see cref="Material"/>s referenced by them. Procedural primitives
/// (<see cref="CreateCube"/>, <see cref="CreatePlane"/>) are built in code;
/// files (glTF, OBJ, FBX, …) are imported through Assimp
/// (<see cref="Load(string)"/>), which also converts glTF PBR materials and their
/// textures into engine <see cref="Material"/>s using the standard PBR shader.
/// </summary>
[AssetType("gltf", "obj", "fbx")]
public sealed class Model : ResourceFile
{
    private static readonly Assimp Api = Assimp.GetApi();
    private static Model? _error;

    public string Name { get; private set; } = string.Empty;
    public IReadOnlyList<Mesh> Meshes { get; private set; } = [];

    /// <summary>Every node in the model's hierarchy, flattened in pre-order.</summary>
    public IReadOnlyList<ModelNode> Nodes { get; private set; } = [];

    /// <summary>The root node of the hierarchy, or null for a model with no nodes.</summary>
    public ModelNode? Root => Nodes.FirstOrDefault(node => node.Parent is null);

    /// <summary>Every drawable (mesh, node) occurrence; several may share one mesh.</summary>
    public IReadOnlyList<ModelMeshInstance> MeshInstances { get; private set; } = [];

    /// <summary>
    /// The content path this model was loaded from, or null for procedural
    /// models (primitives and the error model). Shared cache identity:
    /// <see cref="Retain"/>/<see cref="Release"/> act on this path. It is a
    /// facade over the inherited <see cref="ResourceFile.Path"/>, which is
    /// empty for procedural models.
    /// </summary>
    public string? ResourcePath => string.IsNullOrEmpty(Path) ? null : Path;

    /// <summary>
    /// Every material referenced by the model's meshes, in the order the
    /// meshes first use them. Unreferenced importer materials are omitted.
    /// </summary>
    public IReadOnlyList<Material> Materials { get; private set; } = [];

    /// <summary>Model-space bounding box of all meshes, used by editor picking.</summary>
    public Bounds Bounds { get; private set; }

    /// <summary>
    /// The bounds used for view culling. With no LOD system yet this is the
    /// same as <see cref="Bounds"/>; it exists so LOD-aware render bounds can
    /// be introduced without changing call sites.
    /// </summary>
    public Bounds RenderBounds => Bounds;

    /// <summary>True when the model was built in code (a primitive or the error model).</summary>
    public bool IsProcedural { get; private set; }

    /// <summary>True for the placeholder <see cref="Error"/> model.</summary>
    public bool IsError { get; private set; }

    /// <summary>Total number of meshes in this model.</summary>
    public int MeshCount => Meshes.Count;

    /// <summary>Total number of drawable mesh occurrences (>= <see cref="MeshCount"/>).</summary>
    public int InstanceCount => MeshInstances.Count;

    /// <summary>Total number of materials in this model.</summary>
    public int MaterialCount => Materials.Count;

    private Model(
        string name,
        IReadOnlyList<Mesh> meshes,
        IReadOnlyList<Material>? materials = null,
        bool isProcedural = false,
        bool isError = false,
        string? resourcePath = null,
        IReadOnlyList<ModelNode>? nodes = null,
        IReadOnlyList<ModelMeshInstance>? instances = null)
    {
        Name = name;
        Meshes = meshes;
        Materials = materials ?? [];
        Nodes = nodes ?? [];
        MeshInstances = instances ?? [];
        Bounds = MeshInstances.Count > 0
            ? ComputeInstanceBounds(MeshInstances)
            : Bounds.FromPoints(meshes.SelectMany(mesh => mesh.Vertices).Select(v => v.Position));
        IsProcedural = isProcedural;
        IsError = isError;
        if (resourcePath is not null)
            Path = resourcePath;
    }

    /// <summary>Allocated by the library, then populated through <see cref="Load"/>.</summary>
    private Model()
    {
    }

    /// <summary>
    /// A unit cube (1×1×1, centered on the origin) with per-face normals and
    /// no material (the renderer's default applies).
    /// </summary>
    public static Model CreateCube() =>
        CreateSingleInstanceModel("Cube", CreateCubeMesh("Cube"));

    /// <summary>
    /// A unit plane (1×1) lying in the XZ plane with its normal pointing up
    /// (+Y), centered on the origin. The two triangles share the winding of
    /// the cube's top face, so the geometry stays consistent with
    /// <see cref="CreateCube"/>.
    /// </summary>
    public static Model CreatePlane() =>
        CreateSingleInstanceModel("Plane", CreatePlaneMesh("Plane"));

    /// <summary>
    /// The engine's placeholder model, returned (or substituted) when a model
    /// fails to load. A small red cube with the unlit shader, so it is always
    /// visible without lighting.
    /// </summary>
    public static Model Error => _error ??= CreateErrorModel();

    /// <summary>True when <paramref name="model"/> has at least one drawable mesh instance.</summary>
    public static bool HasRenderMeshes(Model? model) => model is { InstanceCount: > 0 };

    /// <summary>Returns the mesh at <paramref name="index"/>, or null when out of range.</summary>
    public Mesh? GetMesh(int index) => index >= 0 && index < Meshes.Count ? Meshes[index] : null;

    /// <summary>Returns the material at <paramref name="index"/>, or null when out of range.</summary>
    public Material? GetMaterial(int index) => index >= 0 && index < Materials.Count ? Materials[index] : null;

    /// <summary>
    /// Populates this instance from <see cref="ResourceFile.Path"/> through
    /// Assimp (glTF, OBJ, FBX, …). The library allocates the instance, assigns
    /// its path and calls this; loading the same path twice returns the same
    /// instance through the shared cache. See <see cref="Load(string)"/> for
    /// the entry point.
    /// </summary>
    public override void Load() => ImportInto(Path, null, CancellationToken.None);

    /// <summary>
    /// Imports a 3D model file, sharing the instance across every load of the
    /// same path through the global <see cref="Global.ResourceLibrary"/> cache.
    /// Throws when the file is missing or unreadable.
    /// </summary>
    public static Model Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.Load<Model>(path);
    }

    /// <summary>
    /// The actual Assimp import: the scene is triangulated, smoothed and
    /// optimized at load time and released before returning, so the model owns
    /// only managed data. glTF materials are converted to the standard PBR
    /// shader with their texture set (albedo, normal, metallic-roughness,
    /// occlusion, emissive) resolved relative to the model file.
    /// </summary>
    private unsafe void ImportInto(string path, IProgress<ModelLoadProgress>? progress, CancellationToken cancellationToken)
    {
        Path = path;
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ModelLoadProgress(ModelLoadStage.Validating, 0f));

        var fs = FileSystem.Content;
        if (!fs.FileExists(path))
            throw new FileNotFoundException("Model file not found.", path);

        ValidateExternalBuffers(path);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ModelLoadProgress(ModelLoadStage.Validating, 0.05f));

        // Assimp imports right-handed content (glTF spec), but the engine
        // renders left-handed (Unity/DirectX): front faces wind the opposite
        // way. FlipWindingOrder converts imported meshes to the engine's
        // convention so their front faces are not culled as back faces.
        const uint postProcess =
            (uint)(PostProcessSteps.Triangulate
                   | PostProcessSteps.GenerateSmoothNormals
                   | PostProcessSteps.CalculateTangentSpace
                   | PostProcessSteps.FlipUVs
                   | PostProcessSteps.FlipWindingOrder
                   | PostProcessSteps.JoinIdenticalVertices
                   | PostProcessSteps.OptimizeMeshes);

        progress?.Report(new ModelLoadProgress(ModelLoadStage.Importing, 0.1f));
        var systemPath = fs.ToSystemPath(path);
        Scene* scene = Api.ImportFile(systemPath, postProcess);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new ModelLoadProgress(ModelLoadStage.Importing, 0.5f));
        if (scene == null)
        {
            nint error = (nint)Api.GetErrorString();
            var message = error == 0 ? null : Marshal.PtrToStringUTF8(error);
            throw new InvalidOperationException(
                $"Assimp could not import '{path}': {message ?? "unknown error"}");
        }

        try
        {
            var allMaterials = new List<Material>((int)scene->MNumMaterials);
            for (var i = 0; i < scene->MNumMaterials; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                allMaterials.Add(ConvertMaterial(scene->MMaterials[i], path));
                progress?.Report(new ModelLoadProgress(
                    ModelLoadStage.ConvertingMaterials,
                    0.5f + 0.2f * (i + 1) / Math.Max(1, (int)scene->MNumMaterials)));
            }

            var meshes = new List<Mesh>((int)scene->MNumMeshes);
            for (var i = 0; i < scene->MNumMeshes; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssimpMesh* source = scene->MMeshes[i];
                var materialIndex = (int)source->MMaterialIndex;
                var material = materialIndex >= 0 && materialIndex < allMaterials.Count
                    ? allMaterials[materialIndex]
                    : null;
                meshes.Add(ConvertMesh(source, material));
                progress?.Report(new ModelLoadProgress(
                    ModelLoadStage.ConvertingMeshes,
                    0.7f + 0.28f * (i + 1) / Math.Max(1, (int)scene->MNumMeshes)));
            }

            // Assimp can append a default material that no mesh references.
            // Expose only the materials the meshes actually use (in mesh
            // order), like s&box's Model.Materials.
            var materials = meshes
                .Select(mesh => mesh.Material)
                .Where(material => material is not null)
                .Distinct()
                .Select(material => material!)
                .ToList();

            // Preserve the node hierarchy instead of baking node transforms
            // into the vertices: each node keeps its local transform and
            // references its meshes, so one mesh can be instanced at several
            // nodes and nodes can be animated later.
            var nodes = new List<ModelNode>();
            var instances = new List<ModelMeshInstance>();
            if (scene->MRootNode != null)
                ConvertNode(scene->MRootNode, meshes, nodes, instances);

            cancellationToken.ThrowIfCancellationRequested();
            Name = PathUtil.GetFileNameWithoutExtension(path);
            Meshes = meshes;
            Materials = materials;
            Nodes = nodes;
            MeshInstances = instances;
            Bounds = MeshInstances.Count > 0
                ? ComputeInstanceBounds(MeshInstances)
                : Bounds.FromPoints(meshes.SelectMany(mesh => mesh.Vertices).Select(v => v.Position));
            IsProcedural = false;
            IsError = false;
            IsValid = true;
            RetainTextures();
            progress?.Report(new ModelLoadProgress(ModelLoadStage.Done, 1f));
        }
        finally
        {
            Api.ReleaseImport(scene);
        }
    }

    /// <summary>
    /// Loads a model off the calling thread (see <see cref="Load(string)"/>).
    /// Reports <see cref="ModelLoadProgress"/> as the import advances and
    /// checks <paramref name="cancellationToken"/> between stages. The result
    /// is shared with <see cref="Load(string)"/> through the same cache; a
    /// cancelled token skips the import and the returned task faults with
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    public static Task<Model> LoadAsync(
        string path,
        IProgress<ModelLoadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return ResourceLibrary.LoadAsync(path, (path, token) => CreateFromFile(path, progress, token), cancellationToken);
    }

    /// <summary>Allocates a model and imports it into the instance, for the off-thread async path.</summary>
    private static Model CreateFromFile(string path, IProgress<ModelLoadProgress>? progress, CancellationToken token)
    {
        var model = new Model();
        model.ImportInto(path, progress, token);
        return model;
    }

    /// <summary>Records a holder reference for a file-loaded model (no-op for procedural models).</summary>
    public void Retain()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Retain<Model>(ResourcePath);
    }

    /// <summary>Drops a holder reference; the cache entry is discarded when the last holder releases.</summary>
    public void Release()
    {
        if (ResourcePath is not null)
            ResourceLibrary.Release<Model>(ResourcePath);
    }

    /// <summary>Discards the cached model at <paramref name="path"/> so the next load re-imports it.</summary>
    public static void Invalidate(string path) => ResourceLibrary.Invalidate<Model>(path);

    /// <summary>Discards every cached model.</summary>
    public static void ClearCache() => ResourceLibrary.Clear<Model>();

    internal static int CachedCount => ResourceLibrary.CachedCount<Model>();

    internal static int GetReferenceCount(string path) => ResourceLibrary.GetReferenceCount<Model>(path);

    private IEnumerable<Texture2D> DistinctTextures() =>
        Materials.SelectMany(material => material.Textures.Values).Distinct();

    /// <summary>
    /// Releases the model's retained textures when the cache discards the
    /// entry (the last holder released, or an invalidate/clear): each texture
    /// drops the holder recorded at import, so its own cache entry is free to
    /// go when nothing else references it.
    /// </summary>
    public override void Unload()
    {
        base.Unload();
        foreach (var texture in DistinctTextures())
            texture.Release();
    }

    private void RetainTextures()
    {
        foreach (var texture in DistinctTextures())
            texture.Retain();
    }

    private static Model CreateErrorModel()
    {
        var material = Material.FromShader("Surface/Unlit", "Main", "Error")
            .Set("color", new Vector4(1f, 0.1f, 0.25f, 1f));
        var mesh = CreateCubeMesh("Error", material);
        return CreateSingleInstanceModel("Error", mesh, [material], isError: true);
    }

    /// <summary>Builds a procedural model around one mesh: a single identity node plus one instance.</summary>
    private static Model CreateSingleInstanceModel(
        string name,
        Mesh mesh,
        IReadOnlyList<Material>? materials = null,
        bool isError = false)
    {
        var node = new ModelNode(name, Matrix4x4.Identity) { Mesh = mesh };
        return new Model(
            name,
            [mesh],
            materials,
            isProcedural: true,
            isError: isError,
            nodes: [node],
            instances: [new ModelMeshInstance(mesh, node)]);
    }

    private static Mesh CreateCubeMesh(string name, Material? material = null)
    {
        var vertices = new List<MeshVertex>(24);
        var indices = new List<uint>(36);

        // Each face is defined by its normal and two in-plane axes (u, v).
        var faces = new (Vector3 Normal, Vector3 U, Vector3 V)[]
        {
            (new Vector3(1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, 1)),
            (new Vector3(-1, 0, 0), new Vector3(0, 1, 0), new Vector3(0, 0, -1)),
            (new Vector3(0, 1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, -1)),
            (new Vector3(0, -1, 0), new Vector3(1, 0, 0), new Vector3(0, 0, 1)),
            (new Vector3(0, 0, 1), new Vector3(1, 0, 0), new Vector3(0, 1, 0)),
            (new Vector3(0, 0, -1), new Vector3(-1, 0, 0), new Vector3(0, 1, 0))
        };

        foreach (var face in faces)
        {
            // The face is offset by half a unit along its normal, so its square
            // sits at ±0.5 instead of passing through the origin (which would
            // collapse the cube into three coplanar square pairs). The tangent
            // is the face's in-plane u axis with a positive handedness.
            var center = face.Normal * 0.5f;
            var corner = face.U * 0.5f + face.V * 0.5f;
            var tangent = new Vector4(face.U, 1f);
            var baseIndex = (uint)vertices.Count;
            vertices.Add(new MeshVertex(center - corner, face.Normal, tangent, new Vector2(0, 1)));
            vertices.Add(new MeshVertex(center - face.U * 0.5f + face.V * 0.5f, face.Normal, tangent, new Vector2(1, 1)));
            vertices.Add(new MeshVertex(center + corner, face.Normal, tangent, new Vector2(1, 0)));
            vertices.Add(new MeshVertex(center + face.U * 0.5f - face.V * 0.5f, face.Normal, tangent, new Vector2(0, 0)));
            indices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
        }

        return new Mesh(name, [.. vertices], [.. indices], material);
    }

    private static Mesh CreatePlaneMesh(string name, Material? material = null)
    {
        MeshVertex[] vertices =
        [
            new(new Vector3(-0.5f, 0f, 0.5f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), new Vector2(0, 1)),
            new(new Vector3(-0.5f, 0f, -0.5f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), new Vector2(1, 1)),
            new(new Vector3(0.5f, 0f, -0.5f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), new Vector2(1, 0)),
            new(new Vector3(0.5f, 0f, 0.5f), Vector3.UnitY, new Vector4(1f, 0f, 0f, 1f), new Vector2(0, 0))
        ];
        uint[] indices = [0, 1, 2, 0, 2, 3];

        return new Mesh(name, vertices, indices, material);
    }

    /// <summary>
    /// Converts an Assimp material into an engine <see cref="Material"/> using
    /// the standard PBR shader. glTF factors (base color, metallic, roughness)
    /// become shader parameters and the glTF texture set becomes the shader's
    /// texture slots. Textures that are missing or unreadable are skipped; the
    /// renderer substitutes its neutral defaults.
    /// </summary>
    private static unsafe Material ConvertMaterial(AssimpMaterial* source, string modelPath)
    {
        var name = GetMaterialName(source, "Material");
        var material = Material.FromShader("Surface/StandardPbr", "Main", name);

        // glTF stores the base color under $clr.base; classic formats (OBJ,
        // FBX) use $clr.diffuse. The alpha channel carries the material's
        // opacity, which the PBR shader writes through as color.a.
        var baseColor = new Vector4(1f);
        if (!TryGetColor(source, "$clr.base", out baseColor))
            TryGetColor(source, "$clr.diffuse", out baseColor);

        material.Set("color", baseColor);
        material.Set("metallic", GetMaterialFloat(source, "$mat.metallicFactor", 0f));
        material.Set("roughness", GetMaterialFloat(source, "$mat.roughnessFactor", 0.5f));
        material.Set("occlusion", 1f);
        material.Set("emissive", 0f);

        BindTexture(material, source, "albedoTexture", [TextureType.BaseColor, TextureType.Diffuse], modelPath);
        BindTexture(material, source, "normalTexture", [TextureType.Normals, TextureType.NormalCamera], modelPath);
        BindTexture(material, source, "metallicRoughnessTexture", [TextureType.GltfMetallicRoughness, TextureType.Metalness], modelPath);
        BindTexture(material, source, "occlusionTexture", [TextureType.AmbientOcclusion], modelPath);
        BindTexture(material, source, "emissiveTexture", [TextureType.Emissive, TextureType.EmissionColor], modelPath);

        // The shader treats emissive as an intensity multiplier for the
        // emissive map; without a map the black fallback keeps emissive at 0.
        if (material.Textures.ContainsKey("emissiveTexture"))
            material.Set("emissive", 1f);

        // glTF render state: alphaMode BLEND composites src-over without
        // writing depth; alphaMode MASK is alpha-tested, which the engine does
        // not support yet, so it stays opaque. doubleSided disables culling.
        material.DoubleSided = GetMaterialInt(source, "$mat.twosided", 0) != 0;
        var alphaMode = GetMaterialStringValue(source, "$mat.gltf.alphaMode");
        material.BlendMode = string.Equals(alphaMode, "BLEND", StringComparison.OrdinalIgnoreCase)
            ? MaterialBlendMode.Blend
            : MaterialBlendMode.Opaque;

        return material;
    }

    private static unsafe void BindTexture(
        Material material,
        AssimpMaterial* source,
        string slotName,
        TextureType[] types,
        string modelPath)
    {
        string? relativePath = null;
        foreach (var type in types)
        {
            relativePath = GetMaterialTexturePath(source, type);
            if (relativePath is not null)
                break;
        }

        if (relativePath is null)
            return;

        try
        {
            material.SetTexture(slotName, Texture2D.Load(ResolveRelativePath(modelPath, relativePath)));
        }
        catch (Exception)
        {
            // Missing or corrupt texture: leave the slot unbound. The renderer
            // binds a neutral 1x1 fallback, so the model still renders.
        }
    }

    private static unsafe string? GetMaterialTexturePath(AssimpMaterial* material, TextureType type)
    {
        AssimpString path = default;
        if (Api.GetMaterialString(material, "$tex.file", (uint)type, 0, ref path) != Return.Success)
            return null;

        var value = path.AsString;
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static unsafe bool TryGetColor(AssimpMaterial* material, string key, out Vector4 color)
    {
        color = Vector4.One;
        return Api.GetMaterialColor(material, key, 0, 0, ref color) == Return.Success;
    }

    private static unsafe float GetMaterialFloat(AssimpMaterial* material, string key, float fallback)
    {
        float value = fallback;
        uint count = 1;
        return Api.GetMaterialFloatArray(material, key, 0, 0, ref value, ref count) == Return.Success
            ? value
            : fallback;
    }

    private static unsafe int GetMaterialInt(AssimpMaterial* material, string key, int fallback)
    {
        int value = fallback;
        uint count = 1;
        return Api.GetMaterialIntegerArray(material, key, 0, 0, ref value, ref count) == Return.Success
            ? value
            : fallback;
    }

    private static unsafe string? GetMaterialStringValue(AssimpMaterial* material, string key)
    {
        AssimpString value = default;
        return Api.GetMaterialString(material, key, 0, 0, ref value) == Return.Success
            ? value.AsString
            : null;
    }

    private static unsafe string GetMaterialName(AssimpMaterial* material, string fallback)
    {
        AssimpString name = default;
        if (Api.GetMaterialString(material, "?mat.name", 0, 0, ref name) != Return.Success)
            return fallback;

        var value = name.AsString;
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>
    /// Resolves a path relative to a model file (a texture or an external
    /// buffer) into a path the filesystem service can open. OS paths pass
    /// through unchanged.
    /// </summary>
    private static string ResolveRelativePath(string modelPath, string relativePath)
    {
        if (FileSystemService.IsRooted(relativePath))
            return relativePath;

        var directory = new FilePath(modelPath).GetDirectory();
        return (directory / new FilePath(relativePath)).FullName;
    }

    /// <summary>
    /// glTF references geometry through external buffers (a <c>.bin</c> next to
    /// the <c>.gltf</c>). Check those up front so a missing buffer fails with an
    /// actionable message instead of Assimp's generic import error.
    /// </summary>
    private static void ValidateExternalBuffers(string path)
    {
        if (!path.EndsWith(".gltf", StringComparison.OrdinalIgnoreCase))
            return;

        var fs = FileSystem.Content;
        using var json = JsonDocument.Parse(fs.ReadAllText(path));
        if (!json.RootElement.TryGetProperty("buffers", out var buffers) ||
            buffers.ValueKind != JsonValueKind.Array)
            return;

        foreach (var buffer in buffers.EnumerateArray())
        {
            if (!buffer.TryGetProperty("uri", out var uri) || uri.ValueKind != JsonValueKind.String)
                continue;

            var bufferUri = uri.GetString();
            if (string.IsNullOrWhiteSpace(bufferUri) || bufferUri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            var bufferPath = ResolveRelativePath(path, bufferUri);
            if (!fs.FileExists(bufferPath))
            {
                throw new FileNotFoundException(
                    $"The glTF buffer '{bufferUri}' referenced by '{path}' was not found. " +
                    "The complete model package (the .bin next to the .gltf) is required.",
                    bufferPath);
            }
        }
    }

    private static unsafe Mesh ConvertMesh(AssimpMesh* source, Material? material)
    {
        var vertexCount = (int)source->MNumVertices;
        var vertices = new MeshVertex[vertexCount];
        var hasNormals = source->MNormals != null;
        var hasUvs = source->MTextureCoords.Element0 != null;
        var hasTangents = source->MTangents != null && source->MBitangents != null;

        for (var i = 0; i < vertexCount; i++)
        {
            var position = source->MVertices[i];
            var normal = hasNormals ? source->MNormals[i] : default;
            var uv = hasUvs
                ? new Vector2(source->MTextureCoords.Element0[i].X, source->MTextureCoords.Element0[i].Y)
                : Vector2.Zero;

            var tangent = new Vector4(1f, 0f, 0f, 1f);
            if (hasTangents)
            {
                var t = source->MTangents[i];
                var b = source->MBitangents[i];
                var tangent3 = new Vector3(t.X, t.Y, t.Z);
                var normal3 = new Vector3(normal.X, normal.Y, normal.Z);
                var bitangent3 = new Vector3(b.X, b.Y, b.Z);
                var sign = Vector3.Dot(Vector3.Cross(normal3, tangent3), bitangent3);
                tangent = new Vector4(tangent3, sign >= 0f ? 1f : -1f);
            }

            vertices[i] = new MeshVertex(
                new Vector3(position.X, position.Y, position.Z),
                new Vector3(normal.X, normal.Y, normal.Z),
                tangent,
                uv);
        }

        var indices = new List<uint>((int)source->MNumFaces * 3);
        for (var f = 0; f < source->MNumFaces; f++)
        {
            var face = source->MFaces[f];
            for (var i = 0; i < face.MNumIndices; i++)
                indices.Add(face.MIndices[i]);
        }

        var name = source->MName.AsString;
        return new Mesh(string.IsNullOrWhiteSpace(name) ? "Mesh" : name, vertices, [.. indices], material);
    }

    /// <summary>
    /// Converts an Assimp node (and its descendants) into a <see cref="ModelNode"/>
    /// tree, recording one <see cref="ModelMeshInstance"/> per mesh the node
    /// references. The same mesh referenced by several nodes yields several
    /// instances sharing the mesh.
    /// </summary>
    private static unsafe ModelNode ConvertNode(
        Node* source,
        IReadOnlyList<Mesh> meshes,
        List<ModelNode> nodes,
        List<ModelMeshInstance> instances)
    {
        // Silk.NET binds Assimp's aiMatrix4x4 (column-vector convention,
        // translation in the last column) straight into System.Numerics'
        // field order (row-vector convention, translation in the last row),
        // so the matrix must be transposed to read a correct transform.
        var node = new ModelNode(source->MName.AsString, Matrix4x4.Transpose(source->MTransformation));
        nodes.Add(node);

        for (var i = 0; i < source->MNumMeshes; i++)
        {
            var meshIndex = (int)source->MMeshes[i];
            if (meshIndex < 0 || meshIndex >= meshes.Count)
                continue;

            var mesh = meshes[meshIndex];
            node.Mesh ??= mesh;
            instances.Add(new ModelMeshInstance(mesh, node));
        }

        for (var i = 0; i < source->MNumChildren; i++)
            node.AddChild(ConvertNode(source->MChildren[i], meshes, nodes, instances));

        return node;
    }

    private static Bounds MeshBounds(Mesh mesh) =>
        Bounds.FromPoints(mesh.Vertices.Select(vertex => vertex.Position));

    private static Bounds ComputeInstanceBounds(IReadOnlyList<ModelMeshInstance> instances)
    {
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        foreach (var instance in instances)
        {
            var bounds = MeshBounds(instance.Mesh).TransformBy(instance.Node.WorldTransform);
            min = Vector3.Min(min, bounds.Min);
            max = Vector3.Max(max, bounds.Max);
        }
        return new Bounds(min, max);
    }
}
