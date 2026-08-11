using System.Numerics;

namespace Crowbar.Engine.World.Tests;

internal sealed class TestTransform : TransformComponent
{
}

public class TransformAttachmentTests
{
    private static Transform At(float x, float y = 0f, float z = 0f) => new(new Vector3(x, y, z));

    [Fact]
    public void WorldTransform_IsLocalWhenRoot()
    {
        using var world = new World();
        var component = world.SpawnEntity().AddComponent<TestTransform>();
        component.Local = At(5, 2, 1);

        Assert.Equal(At(5, 2, 1), component.World);
    }

    [Fact]
    public void WorldTransform_ComposesThroughParentChain()
    {
        using var world = new World();
        var root = world.SpawnEntity("root").AddComponent<TestTransform>();
        var mid = world.SpawnEntity("mid").AddComponent<TestTransform>();
        var leaf = world.SpawnEntity("leaf").AddComponent<TestTransform>();

        root.Local = At(10, 0, 0);
        mid.AttachTo(root);
        mid.Local = At(5, 0, 0);
        leaf.AttachTo(mid);
        leaf.Local = At(2, 0, 0);

        Assert.Equal(At(17, 0, 0), leaf.World);
        Assert.Same(mid, leaf.Parent);
        Assert.Same(root, mid.Parent);
        Assert.Equal([mid], root.Children);
        Assert.Equal([leaf], mid.Children);
    }

    [Fact]
    public void AttachKeepWorld_RecomputesLocal()
    {
        using var world = new World();
        var root = world.SpawnEntity().AddComponent<TestTransform>();
        var child = world.SpawnEntity().AddComponent<TestTransform>();

        root.World = At(10, 0, 0);
        child.World = At(15, 0, 0);
        child.AttachTo(root); // keep world: stays at 15

        Assert.Equal(At(15, 0, 0), child.World);
        Assert.Equal(At(5, 0, 0), child.Local);
    }

    [Fact]
    public void AttachKeepLocal_KeepsLocalAndMovesInWorld()
    {
        using var world = new World();
        var root = world.SpawnEntity().AddComponent<TestTransform>();
        var child = world.SpawnEntity().AddComponent<TestTransform>();

        root.World = At(100, 0, 0);
        child.Local = At(5, 0, 0);
        child.AttachTo(root, keepWorldTransform: false);

        Assert.Equal(At(5, 0, 0), child.Local);
        Assert.Equal(At(105, 0, 0), child.World);
    }

    [Fact]
    public void WorldSetter_RecomputesLocalAgainstParent()
    {
        using var world = new World();
        var root = world.SpawnEntity().AddComponent<TestTransform>();
        var child = world.SpawnEntity().AddComponent<TestTransform>();

        root.World = At(100, 0, 0);
        child.AttachTo(root);
        child.World = At(200, 0, 0);

        Assert.Equal(At(100, 0, 0), child.Local);
    }

    [Fact]
    public void DetachKeepWorld_MakesLocalAbsolute()
    {
        using var world = new World();
        var root = world.SpawnEntity().AddComponent<TestTransform>();
        var child = world.SpawnEntity().AddComponent<TestTransform>();

        root.World = At(10, 0, 0);
        child.World = At(10, 0, 0);
        child.AttachTo(root); // keep world: local becomes 0
        Assert.Equal(At(0, 0, 0), child.Local);
        Assert.Equal(At(10, 0, 0), child.World);

        child.Detach();
        Assert.Null(child.Parent);
        Assert.Equal(At(10, 0, 0), child.Local);
        Assert.Equal(At(10, 0, 0), child.World);
        Assert.Empty(root.Children);
    }

    [Fact]
    public void AttachCycle_Throws()
    {
        using var world = new World();
        var a = world.SpawnEntity().AddComponent<TestTransform>();
        var b = world.SpawnEntity().AddComponent<TestTransform>();
        var c = world.SpawnEntity().AddComponent<TestTransform>();

        b.AttachTo(a);
        c.AttachTo(b);

        Assert.Throws<InvalidOperationException>(() => a.AttachTo(c));
        Assert.Throws<InvalidOperationException>(() => a.AttachTo(a));
    }

    [Fact]
    public void DestroyingParent_DetachesChildrenKeepingWorldTransform()
    {
        using var world = new World();
        var parent = world.SpawnEntity("parent");
        var root = parent.AddComponent<TestTransform>();
        var child = world.SpawnEntity("child").AddComponent<TestTransform>();

        root.World = At(10, 0, 0);
        child.World = At(12, 0, 0);
        child.AttachTo(root);

        parent.Destroy();
        Assert.False(root.IsValid);
        Assert.True(child.IsValid);
        Assert.Null(child.Parent);
        Assert.Equal(At(12, 0, 0), child.World);
        Assert.Equal(At(12, 0, 0), child.Local);
    }

    [Fact]
    public void EntityAttachTo_AttachesRootTransforms()
    {
        using var world = new World();
        var parent = world.SpawnEntity("parent");
        parent.AddComponent<TestTransform>();
        var child = world.SpawnEntity("child");
        child.AddComponent<TestTransform>();

        child.AttachTo(parent);

        Assert.Same(
            parent.GetComponent<TestTransform>(),
            child.GetComponent<TestTransform>()!.Parent);
    }

    [Fact]
    public void EntityAttachTo_WithoutTransforms_Throws()
    {
        using var world = new World();
        var parent = world.SpawnEntity("parent");
        var child = world.SpawnEntity("child");

        Assert.Throws<InvalidOperationException>(() => child.AttachTo(parent));
    }

    [Fact]
    public void LocalChanged_RaisesOnChangesButNotOnSameValue()
    {
        using var world = new World();
        var component = world.SpawnEntity().AddComponent<TestTransform>();
        var changes = 0;
        component.LocalChanged += _ => changes++;

        component.Local = At(1, 0, 0);
        component.Local = At(1, 0, 0); // same value: no event
        Assert.Equal(1, changes);

        component.World = At(2, 0, 0);
        Assert.Equal(2, changes);
    }
}
