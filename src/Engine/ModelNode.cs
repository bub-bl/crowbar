using System.Numerics;

namespace Crowbar.Engine;

/// <summary>
/// A node in a model's hierarchy: a named transform with children and an
/// optional mesh. Node transforms are kept separate from the mesh geometry
/// (rather than baked into the vertices), so the same mesh can be referenced
/// by several nodes and nodes can be animated by mutating
/// <see cref="LocalTransform"/>.
/// </summary>
public sealed class ModelNode
{
    private readonly List<ModelNode> _children = [];

    public string Name { get; }

    /// <summary>The node's transform relative to <see cref="Parent"/>, mutable for animation.</summary>
    public Matrix4x4 LocalTransform { get; set; }

    public ModelNode? Parent { get; internal set; }

    public IReadOnlyList<ModelNode> Children => _children;

    /// <summary>The mesh drawn at this node, or null for a group/transform node.</summary>
    public Mesh? Mesh { get; internal set; }

    public ModelNode(string name, Matrix4x4 localTransform)
    {
        Name = string.IsNullOrWhiteSpace(name) ? "Node" : name;
        LocalTransform = localTransform;
    }

    internal void AddChild(ModelNode child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    /// <summary>
    /// The world transform: this node's local transform composed over its
    /// ancestors. Recomputed on every read, so mutating a node's
    /// <see cref="LocalTransform"/> is picked up immediately.
    /// </summary>
    public Matrix4x4 WorldTransform => Parent is null ? LocalTransform : LocalTransform * Parent.WorldTransform;
}

/// <summary>
/// One drawable occurrence of a <see cref="Mesh"/> at a <see cref="ModelNode"/>'s
/// transform. Several instances may share the same mesh (instancing) and a node
/// may contribute one instance per mesh it references.
/// </summary>
public sealed class ModelMeshInstance
{
    public Mesh Mesh { get; }
    public ModelNode Node { get; }

    public ModelMeshInstance(Mesh mesh, ModelNode node)
    {
        Mesh = mesh ?? throw new ArgumentNullException(nameof(mesh));
        Node = node ?? throw new ArgumentNullException(nameof(node));
    }
}
