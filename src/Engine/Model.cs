using System.Numerics;
using System.Runtime.InteropServices;
using Crowbar.FileSystems;
using Silk.NET.Assimp;
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

    public Mesh(string name, MeshVertex[] vertices, uint[] indices)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Mesh" : name;
        Vertices = vertices ?? throw new ArgumentNullException(nameof(vertices));
        Indices = indices ?? throw new ArgumentNullException(nameof(indices));
    }
}

/// <summary>
/// A 3D model: a named collection of <see cref="Mesh"/>es. Procedural
/// primitives (<see cref="CreateCube"/>) are built in code; files (OBJ, glTF,
/// FBX, …) are imported through Assimp (<see cref="Load"/>).
/// </summary>
public sealed class Model
{
    private static readonly Assimp Api = Assimp.GetApi();

    public string Name { get; }
    public IReadOnlyList<Mesh> Meshes { get; }

    /// <summary>Model-space bounding box of all meshes, used by editor picking.</summary>
    public Bounds Bounds { get; }

    private Model(string name, IReadOnlyList<Mesh> meshes)
    {
        Name = name;
        Meshes = meshes;
        Bounds = Bounds.FromPoints(meshes.SelectMany(mesh => mesh.Vertices).Select(v => v.Position));
    }

    /// <summary>A unit cube (1×1×1, centered on the origin) with per-face normals.</summary>
    public static Model CreateCube()
    {
        var vertices = new List<MeshVertex>(24);
        var indices = new List<uint>(36);

        // Each face is defined by its normal and two in-plane axes (u, v).
        var faces = new (Vector3 Normal, Vector3 U, Vector3 V)[]
        {
            (new Vector3(1, 0, 0), new Vector3(0, 0, 1), new Vector3(0, 1, 0)),
            (new Vector3(-1, 0, 0), new Vector3(0, 0, -1), new Vector3(0, 1, 0)),
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

        return new Model("Cube", [new Mesh("Cube", [.. vertices], [.. indices])]);
    }

    /// <summary>
    /// Imports a 3D model file through Assimp (OBJ, glTF, FBX, …). The scene
    /// is triangulated, smoothed and optimized at load time; the Assimp scene
    /// is released before returning, so the returned model owns only managed
    /// data.
    /// </summary>
    public static unsafe Model Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fs = FileSystem.Content;
        if (!fs.FileExists(path))
            throw new FileNotFoundException("Model file not found.", path);

        const uint postProcess =
            (uint)(PostProcessSteps.Triangulate
                   | PostProcessSteps.GenerateSmoothNormals
                   | PostProcessSteps.CalculateTangentSpace
                   | PostProcessSteps.FlipUVs
                   | PostProcessSteps.JoinIdenticalVertices
                   | PostProcessSteps.OptimizeMeshes);

        var systemPath = fs.ToSystemPath(path);
        Scene* scene = Api.ImportFile(systemPath, postProcess);
        if (scene == null)
        {
            nint error = (nint)Api.GetErrorString();
            var message = error == 0 ? null : Marshal.PtrToStringUTF8(error);
            throw new InvalidOperationException(
                $"Assimp could not import '{path}': {message ?? "unknown error"}");
        }

        try
        {
            var meshes = new List<Mesh>((int)scene->MNumMeshes);
            for (var i = 0; i < scene->MNumMeshes; i++)
            {
                AssimpMesh* source = scene->MMeshes[i];
                meshes.Add(ConvertMesh(source));
            }

            return new Model(PathUtil.GetFileNameWithoutExtension(path), meshes);
        }
        finally
        {
            Api.ReleaseImport(scene);
        }
    }

    private static unsafe Mesh ConvertMesh(AssimpMesh* source)
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
        return new Mesh(string.IsNullOrWhiteSpace(name) ? "Mesh" : name, vertices, [.. indices]);
    }
}
