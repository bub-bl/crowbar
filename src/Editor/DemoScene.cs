using System.Numerics;
using Crowbar.Engine;

namespace Crowbar.Editor;

/// <summary>
/// The demo scene: a level with a directional light (key), a point light
/// (fill), a floor plane and several cubes/models rendered by MeshRenderers
/// through the world system (<see cref="World"/>). Built when the project has no
/// saved level, or when the saved one is unreadable. Models are loaded through
/// <see cref="GlobalNamespaces.ResourceLibrary"/> (an unreadable asset becomes
/// the error model).
/// </summary>
public static class DemoScene
{
    public static Level Build(World world)
    {
        var level = world.CreateLevel("Demo");

        var sun = level.SpawnEntity("Sun");
        var sunLight = sun.AddComponent<DirectionalLight>();
        sunLight.Color = new Vector3(1f, 0.95f, 0.85f);
        sunLight.Intensity = 1.6f;
        sunLight.CastShadows = true;
        sunLight.Local = new Transform(
            Vector3.Zero,
            Rotation.FromYaw(-45f) * Rotation.FromPitch(-35f),
            Vector3.One);

        var fill = level.SpawnEntity("FillLight");
        var fillLight = fill.AddComponent<PointLight>();
        fillLight.Color = new Vector3(0.4f, 0.6f, 1f);
        fillLight.Intensity = 4f;
        fillLight.Range = 8f;
        fillLight.Local = new Transform(
            new Vector3(-2.5f, 2f, -1.5f),
            Rotation.Identity,
            Vector3.One);

        // The floor: a plane primitive lying on the XZ grid plane, at the
        // origin (0, 0, 0). Scaled to 5×5 so it forms a floor under the
        // cubes around it.
        var plane = level.SpawnEntity("Plane");
        var planeMesh = plane.AddComponent<MeshRenderer>();
        planeMesh.Model = Model.CreatePlane();
        planeMesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.3f, 0.33f, 0.3f, 1f))
            .Set("metallic", 0f)
            .Set("roughness", 0.85f)
            .Set("occlusion", 1f)
            .Set("emissive", 0f);
        planeMesh.Local = new Transform(
            Vector3.Zero,
            Rotation.Identity,
            new Vector3(5f, 1f, 5f));

        var cube = level.SpawnEntity("Cube");
        var mesh = cube.AddComponent<MeshRenderer>();
        mesh.Model = Model.CreateCube();
        mesh.Material = Material.FromShader("Surface/StandardPbr")
            .Set("color", new Vector4(0.2f, 0.6f, 1.0f, 1.0f))
            .Set("metallic", 0.15f)
            .Set("roughness", 0.45f)
            .Set("occlusion", 1f)
            .Set("emissive", 0f);
        // Sits on the plane: the unit cube's bottom face is at y = 0.
        mesh.Local = new Transform(
            new Vector3(0f, 0.5f, 0f),
            Rotation.FromYaw(30f) * Rotation.FromPitch(15f),
            Vector3.One);

        // A second, smaller cube with the Unlit shader (3 lines thanks to the
        // includes): proves the shader API is reusable.
        var accent = level.SpawnEntity("Accent");
        var accentMesh = accent.AddComponent<MeshRenderer>();
        accentMesh.Model = Model.CreateCube();
        accentMesh.Material = Material.FromShader("Surface/Unlit")
            .Set("color", new Vector4(1f, 0.72f, 0.08f, 1f));
        // Half the cube's height (0.25) above the floor: it rests on the plane.
        accentMesh.Local = new Transform(
            new Vector3(2.2f, 0.25f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // A last cube without a material: it picks up the default material
        // (Surface/Standard.slang), the renderer's fallback path.
        var fallback = level.SpawnEntity("DefaultCube");
        var fallbackMesh = fallback.AddComponent<MeshRenderer>();
        fallbackMesh.Model = Model.CreateCube();
        fallbackMesh.Local = new Transform(
            new Vector3(-2.2f, 0.25f, 1.4f),
            Rotation.FromYaw(-20f) * Rotation.FromPitch(10f),
            new Vector3(0.5f));

        // A glTF model imported through Model.Load: Assimp converts its
        // geometry and the engine turns the glTF PBR material into a
        // Surface/StandardPbr material with its albedo + metallic-roughness
        // textures bound. The mesh carries its own material, so no renderer
        // override is needed.
        var crate = level.SpawnEntity("Crate");
        var crateMesh = crate.AddComponent<MeshRenderer>();
        crateMesh.Model = ResourceLibrary.LoadModel("Assets/Models/Crate/Crate.gltf");
        crateMesh.Local = new Transform(
            new Vector3(1.6f, 0.5f, -1.6f),
            Rotation.FromYaw(35f),
            Vector3.One);

        // The real-world Sketchfab test: a multi-mesh glTF with a transparent
        // bulb and a tripod. Its geometry lives in scene.bin next to the .gltf;
        // when that buffer is present the loader bakes the node transforms
        // (PreTransformVertices) and converts both PBR materials. Until then it
        // reports the missing file and falls back to the error model.
        var workLight = level.SpawnEntity("IndustrialWorkLight");
        var workLightMesh = workLight.AddComponent<MeshRenderer>();
        var workLightModel = ResourceLibrary.LoadModel("Assets/Models/industrial_work_light/industrial_work_light.gltf");
        workLightMesh.Model = workLightModel;

        // Sketchfab models arrive with arbitrary extents; normalize the bounds
        // to roughly two units so a real asset fits the demo scene.
        var workLightExtent = workLightModel.Bounds.Max - workLightModel.Bounds.Min;
        var workLightMaxExtent = MathF.Max(workLightExtent.X, MathF.Max(workLightExtent.Y, workLightExtent.Z));
        var workLightScale = workLightMaxExtent > 0.001f ? 2f / workLightMaxExtent : 1f;
        workLightMesh.Local = new Transform(
            new Vector3(-1.6f, 0.5f, -1.6f),
            Rotation.FromYaw(-25f),
            new Vector3(workLightScale));

        return level;
    }

    /// <summary>The entity selected at startup: the main cube, else the first mesh, else the first entity.</summary>
    public static Entity? SelectInitial(Level level) =>
        level.Entities.FirstOrDefault(e => e.Name == "Cube")
        ?? level.Entities.FirstOrDefault(e => e.GetComponent<MeshRenderer>() is not null)
        ?? level.Entities.FirstOrDefault();
}
