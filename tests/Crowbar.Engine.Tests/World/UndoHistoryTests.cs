using System.Numerics;
using System.Text;
using Crowbar.Engine.Undo;

namespace Crowbar.Engine.Tests;

/// <summary>
/// The undo/redo core (<see cref="UndoHistory"/>) is document-agnostic: these
/// tests drive it through a tiny string document (a single StringBuilder), so
/// the tape/window/label/dirty semantics are verified without any engine
/// coupling — any future tool document (animation, material library, …) gets
/// the exact same guarantees.
/// </summary>
public class UndoHistoryTests
{
    private sealed class Document
    {
        private readonly StringBuilder _text = new("start");

        public UndoHistory History { get; }

        public Document()
        {
            History = new UndoHistory(() => _text.ToString(), state =>
            {
                _text.Clear();
                _text.Append(state);
            });
        }

        public string Text => _text.ToString();

        // Mirrors the Level contract: a mutation notifies the history (the
        // level does this through MarkDirty → History.NotifyMutation).
        public void Write(string text)
        {
            _text.Clear().Append(text);
            History.NotifyMutation();
        }
    }

    [Fact]
    public void Baseline_IsTheStateAtCreation_AndNothingCanBeUndone()
    {
        var document = new Document();

        Assert.Equal("start", document.Text);
        Assert.False(document.History.CanUndo);
        Assert.False(document.History.CanRedo);
        Assert.False(document.History.IsDirty);
    }

    [Fact]
    public void Step_CommitsTheChange_AndUndoRedoRestoreExactStates()
    {
        var document = new Document();

        using (document.History.Step("two"))
            document.Write("middle");
        using (document.History.Step("three"))
            document.Write("end");

        Assert.True(document.History.CanUndo);
        Assert.Equal("three", document.History.UndoLabel);
        Assert.Equal("end", document.Text);

        document.History.Undo();
        Assert.Equal("middle", document.Text);
        Assert.True(document.History.CanRedo);
        Assert.Equal("three", document.History.RedoLabel);

        document.History.Undo();
        Assert.Equal("start", document.Text);
        Assert.False(document.History.CanUndo);

        document.History.Redo();
        Assert.Equal("middle", document.Text);
        document.History.Redo();
        Assert.Equal("end", document.Text);
        Assert.False(document.History.CanRedo);
    }

    [Fact]
    public void NoOpWindow_CommitsNothing()
    {
        var document = new Document();

        using (document.History.Step("nothing"))
        {
            // No mutation inside the window.
        }

        Assert.False(document.History.CanUndo);
        Assert.False(document.History.CanRedo);
        Assert.False(document.History.IsDirty);
    }

    [Fact]
    public void NewStepAfterUndo_TruncatesTheRedoBranch()
    {
        var document = new Document();

        using (document.History.Step("two"))
            document.Write("middle");
        using (document.History.Step("three"))
            document.Write("end");

        document.History.Undo();
        Assert.Equal("middle", document.Text);
        Assert.True(document.History.CanRedo);

        // A new edit discards the redone branch: the old "end" is gone forever.
        using (document.History.Step("replaced"))
            document.Write("replacement");

        Assert.Equal("replacement", document.Text);
        Assert.False(document.History.CanRedo);

        document.History.Undo();
        Assert.Equal("middle", document.Text);
        document.History.Undo();
        Assert.Equal("start", document.Text);
    }

    [Fact]
    public void UndoMidWindow_CommitsTheWindowFirst_ThenUndoesIt()
    {
        var document = new Document();

        using (document.History.Step("first"))
            document.Write("one");

        // A drag in progress when undo fires: the in-flight window is committed
        // as a step, then that very step is undone.
        using (document.History.Step("drag"))
        {
            document.Write("dragged");
            document.History.Undo();
        }

        Assert.Equal("one", document.Text);
        // The committed drag step is redoable (standard editor behavior: undo
        // mid-drag then redo replays the drag-so-far); disposing the window
        // again is a no-op.
        Assert.True(document.History.CanUndo);
        Assert.True(document.History.CanRedo);

        document.History.Redo();
        Assert.Equal("dragged", document.Text);
    }

    [Fact]
    public void NestedWindows_CommitTheirOwnSlices()
    {
        var document = new Document();

        using (document.History.Step("outer"))
        {
            document.Write("a");
            using (document.History.Step("inner"))
                document.Write("ab");
            document.Write("abc");
        }

        // Two independent steps: the inner one (a → ab), then the outer one
        // (start → abc), each undoable separately.
        Assert.Equal("abc", document.Text);
        document.History.Undo();
        Assert.Equal("ab", document.Text);
        document.History.Undo();
        Assert.Equal("start", document.Text);
    }

    [Fact]
    public void Labels_TrackTheStepTheyLeadTo()
    {
        var document = new Document();

        using (document.History.Step("first"))
            document.Write("one");
        using (document.History.Step("second"))
            document.Write("two");

        Assert.Equal("second", document.History.UndoLabel);
        document.History.Undo();
        Assert.Equal("second", document.History.RedoLabel);
        Assert.Equal("first", document.History.UndoLabel);
    }

    [Fact]
    public void Dirty_FollowsTheSavedPosition()
    {
        var document = new Document();

        Assert.False(document.History.IsDirty);

        using (document.History.Step("edit"))
            document.Write("edited");
        Assert.True(document.History.IsDirty);

        document.History.MarkSaved();
        Assert.False(document.History.IsDirty);

        // Undoing past the save point re-dirties; redoing back to it cleans.
        document.History.Undo();
        Assert.True(document.History.IsDirty);
        document.History.Redo();
        Assert.False(document.History.IsDirty);
    }

    [Fact]
    public void MutationOutsideAWindow_IsForcedDirty_AndCounted()
    {
        var document = new Document();

        // A mutation nobody bracketed (e.g. a runtime script): still dirty,
        // and recorded as an unbracketed mutation for the host's detector.
        document.Write("scripted");

        Assert.True(document.History.IsDirty);
        Assert.Equal(1, document.History.UnbracketedMutationCount);

        document.History.MarkSaved();
        Assert.False(document.History.IsDirty);
    }

    [Fact]
    public void MutationInsideAWindow_IsNotUnbracketed()
    {
        var document = new Document();

        using (document.History.Step("edit"))
            document.Write("edited");

        Assert.Equal(0, document.History.UnbracketedMutationCount);
        Assert.True(document.History.IsDirty);
    }

    [Fact]
    public void UndoRedoRestores_AreNotNewMutations()
    {
        var document = new Document();

        using (document.History.Step("edit"))
            document.Write("edited");
        document.History.MarkSaved();

        document.History.Undo();
        document.History.Redo();

        // The restores themselves bumped neither the forced-dirty fallback nor
        // the unbracketed counter.
        Assert.False(document.History.IsDirty);
        Assert.Equal(0, document.History.UnbracketedMutationCount);
    }

    [Fact]
    public void Depth_IsBounded_AndTheBaselineSurvives()
    {
        var text = "start";
        var history = new UndoHistory(() => text, state => text = state, maxDepth: 3);

        using (history.Step("s1")) text = "1";
        using (history.Step("s2")) text = "2";
        using (history.Step("s3")) text = "3";
        using (history.Step("s4")) text = "4";

        // The fourth commit drops the oldest step (s1), never the baseline:
        // three undoable steps remain, and the baseline is the undo floor.
        Assert.Equal("4", text);
        history.Undo();
        Assert.Equal("3", text);
        history.Undo();
        Assert.Equal("2", text);
        history.Undo();
        Assert.Equal("start", text);
        Assert.False(history.CanUndo);
    }

    [Fact]
    public void Reset_RebaselinesTheDocument_AndForgetsEveryStep()
    {
        var document = new Document();

        using (document.History.Step("edit"))
            document.Write("edited");
        document.Write("reloaded");
        Assert.True(document.History.IsDirty);

        document.History.Reset();

        Assert.Equal("reloaded", document.Text);
        Assert.False(document.History.CanUndo);
        Assert.False(document.History.CanRedo);
        Assert.False(document.History.IsDirty);
        Assert.Equal(0, document.History.UnbracketedMutationCount);
    }
}

/// <summary>
/// The undo/redo system wired to a real level: Step windows around inspector
/// edits and gizmo-style transform writes, undo/redo restoring the document
/// through <see cref="LevelSerializer.ApplyTo"/> with stable entity ids, and
/// the dirty flag derived from the history position. Entities are rebuilt with
/// their ids preserved on every restore, so tests re-resolve components by id
/// after undo/redo (exactly like the editor's selection does).
/// </summary>
public class LevelUndoTests
{
    /// <summary>Builds a small editable level (one meshed cube) and returns it with its history created.</summary>
    private static (Level Level, Guid CubeId) Scene(World world)
    {
        var level = world.CreateLevel("Demo");
        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Local = new Transform(new Vector3(0f, 0.5f, 0f), Rotation.Identity, Vector3.One);
        _ = level.History; // establish the baseline
        return (level, cube.Id);
    }

    private static MeshRenderer Mesh(World world, Guid id) =>
        world.FindEntity(id)!.GetComponent<MeshRenderer>()!;

    [Fact]
    public void InspectorEdit_UndoRestoresTheProperty_AndRedoReappliesIt()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        using (level.History.Step("Edit a property"))
            InspectorStateBuilder.ApplyEdit(world.FindEntity(cubeId)!, "transform.position", "1, 2, 3");

        Assert.Equal(new Vector3(1, 2, 3), Mesh(world, cubeId).Local.Position);
        Assert.True(level.IsDirty);

        level.History.Undo();
        Assert.Equal(new Vector3(0f, 0.5f, 0f), Mesh(world, cubeId).Local.Position);
        Assert.True(level.IsDirty); // undoing away from the saved state keeps it dirty

        level.History.Redo();
        Assert.Equal(new Vector3(1, 2, 3), Mesh(world, cubeId).Local.Position);
    }

    [Fact]
    public void SaveThenUndoPastSave_ReDirties_AndRedoBackToSaveCleans()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        using (level.History.Step("move"))
            InspectorStateBuilder.ApplyEdit(world.FindEntity(cubeId)!, "transform.position", "5, 0, 0");
        level.ClearDirty(); // Ctrl+S
        Assert.False(level.IsDirty);

        level.History.Undo();
        Assert.True(level.IsDirty); // we left the saved state

        level.History.Redo();
        Assert.False(level.IsDirty); // back exactly at the saved state
    }

    [Fact]
    public void StructuralMutation_SpawnAndDelete_AreUndoable()
    {
        using var world = new World();
        var (level, _) = Scene(world);

        // Spawn inside a window; the spawned id survives the restore.
        Guid spawnedId;
        using (level.History.Step("create an entity"))
            spawnedId = level.SpawnEntity("Extra").Id;

        Assert.Contains(level.Entities, e => e.Id == spawnedId);

        level.History.Undo();
        Assert.Null(world.FindEntity(spawnedId));
        Assert.DoesNotContain(level.Entities, e => e.Id == spawnedId);

        level.History.Redo();
        var restored = world.FindEntity(spawnedId);
        Assert.NotNull(restored);
        Assert.True(restored!.IsValid);
        Assert.Contains(level.Entities, e => e.Id == spawnedId);
    }

    [Fact]
    public void UndoRestores_WithStableEntityIds_SoSelectionSurvives()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        // A gizmo-style drag: many transform writes, one window.
        using (level.History.Step("Move"))
        {
            for (var i = 1; i <= 10; i++)
                Mesh(world, cubeId).Local = new Transform(
                    new Vector3(i, 0.5f, 0f), Rotation.Identity, Vector3.One);
        }

        level.History.Undo();
        var restored = world.FindEntity(cubeId);
        Assert.NotNull(restored);
        Assert.Equal(cubeId, restored!.Id);
        Assert.Equal(new Vector3(0f, 0.5f, 0f),
            restored.GetComponent<MeshRenderer>()!.Local.Position);
    }

    [Fact]
    public void GizmoDrag_ManyWrites_CommitOneSingleStep()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        using (level.History.Step("Move"))
        {
            for (var i = 1; i <= 50; i++)
                Mesh(world, cubeId).Local = Mesh(world, cubeId).Local
                    .WithPosition(new Vector3(i, 0.5f, 0f));
        }

        // One undo returns straight to the pre-drag position (a single step,
        // not fifty).
        level.History.Undo();
        Assert.Equal(new Vector3(0f, 0.5f, 0f), Mesh(world, cubeId).Local.Position);
    }

    [Fact]
    public void UndoDepth_IsBounded_AndBaselineSurvives()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        for (var i = 1; i <= 105; i++)
        {
            using (level.History.Step($"edit {i}"))
                InspectorStateBuilder.ApplyEdit(world.FindEntity(cubeId)!, "transform.position", $"{i}, 0, 0");
        }

        // Only the last 100 steps are kept; the baseline still undoes to.
        var undos = 0;
        while (level.History.CanUndo)
        {
            level.History.Undo();
            undos++;
        }

        Assert.Equal(100, undos);
        Assert.NotNull(world.FindEntity(cubeId));
    }

    [Fact]
    public void ChangeCount_TracksEveryMutation_AndIgnoresRestores()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);
        var before = level.ChangeCount;

        using (level.History.Step("edit"))
            InspectorStateBuilder.ApplyEdit(world.FindEntity(cubeId)!, "transform.position", "2, 0, 0");
        // A transform edit fires MarkDirty twice (the Local setter and ApplyEdit's
        // own commit) — what matters is that the counter moved, and that the
        // edit was bracketed (no unbracketed mutation).
        Assert.True(level.ChangeCount > before);
        Assert.Equal(0, level.History.UnbracketedMutationCount);

        var countAtEdit = level.ChangeCount;
        level.History.Undo();
        level.History.Redo();

        // Undo/redo restores are suppressed: they look like nothing happened.
        Assert.Equal(countAtEdit, level.ChangeCount);
    }

    [Fact]
    public void UnbracketedLevelMutation_IsDetected_AndDirty()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);

        // A transform write with no Step window around it (e.g. a runtime
        // script): the detector's counter moves and the level stays dirty.
        Mesh(world, cubeId).Local = Mesh(world, cubeId).Local.WithPosition(new Vector3(9f, 0, 0));

        Assert.Equal(1, level.History.UnbracketedMutationCount);
        Assert.True(level.IsDirty);

        // A later save clears it.
        level.ClearDirty();
        Assert.False(level.IsDirty);
    }

    [Fact]
    public void WorldTeardown_DoesNotCountAsAnUnbracketedMutation()
    {
        using var world = new World();
        var (level, cubeId) = Scene(world);
        var changeBefore = level.ChangeCount;

        // Disposing the world destroys every entity: teardown is not an edit
        // and must not look like an unbracketed mutation (it would trip the
        // editor's undo detector on the closing frame).
        world.Dispose();

        Assert.Equal(0, level.History.UnbracketedMutationCount);
        Assert.Equal(changeBefore, level.ChangeCount);
    }

    [Fact]
    public void MaterialEdit_IsUndoable()
    {
        using var world = new World();
        var level = world.CreateLevel("Demo");
        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        // The material is part of the document before the history baseline is
        // taken (in the editor it would be set by an earlier, already-undoable
        // action) — an edit made before any step is not undoable by definition.
        mesh.Material = Material.FromShader("Surface/StandardPbr").Set("metallic", 0.15f);
        _ = level.History;

        using (level.History.Step("Edit a property"))
            InspectorStateBuilder.ApplyEdit(cube, "MeshRenderer.Material.metallic", "0.9");
        Assert.Equal(0.9f, mesh.Material!.Get<float>("metallic"), 3);

        level.History.Undo();
        var restored = world.FindEntity(cube.Id)!.GetComponent<MeshRenderer>()!;
        Assert.Equal(0.15f, restored.Material!.Get<float>("metallic"), 3);
    }
}
