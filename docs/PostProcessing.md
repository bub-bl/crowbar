# Post-processing

Post-process effects run after the 3D scene renders in linear HDR, before the
frame is shown: the enabled `PostProcess` components chain in `Order` through
fullscreen passes (ping-ponging between HDR intermediates), and the last pass
writes the display texture the surface blit shows. Everything is a plain
component, so effects are inspectable, undoable, serialized in the `.level`,
and attachable from the editor's **+ Add component** list — including effects
written in a game project. The engine is neutral: with no component the scene
is presented as-is, so the default look is level content — the demo level
owns its camera (a `Camera` entity) and ships a `Tonemapping` component on
it.

## The two ways to write an effect

### Declarative: `SinglePassPostProcess`

The shader reads the previous chain texture, writes the chain output once,
and its uniform fields are packed automatically from the component's
`[Property]` values by name:

```csharp
using Crowbar.Engine;

public sealed class Brightness : SinglePassPostProcess
{
    [Property] public float Boost { get; set; } = 1.2f;

    public override string ShaderPath => "Content/Shaders/Brightness.wgsl";
}
```

```slang
#language slang 2026;
struct BrightnessUniforms
{
    float boost; // packed from the Boost property (trailing underscores/case ignored)
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<BrightnessUniforms> postProcess;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output;
    output.position = float4(clip, 0.0, 1.0);
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    return output;
}

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    var color = sceneTexture.Sample(sceneSampler, input.uv).rgb * postProcess.boost;
    return float4(color, 1.0);
}
```

### Imperative: derive from `PostProcess` (or `BasePostProcess<T>`)

For multi-pass effects (blur, bloom), conditional work, or anything that needs
code at render time, override `Render(PostProcessContext)` and issue the
passes yourself:

```csharp
public sealed class MyBlur : BasePostProcess<MyBlur>
{
    [Property] public float Strength { get; set; } = 1f;

    public override void Render(PostProcessContext context)
    {
        var strength = GetWeighted(effect => effect.Strength);
        var scratch = context.GetScratchTexture(0);
        context.Blit(context.Input, scratch, "Content/Shaders/BlurX.wgsl",
            new RenderAttributes().Set("strength", strength));
        context.Blit(scratch, context.Output, "Content/Shaders/BlurY.wgsl",
            new RenderAttributes().Set("strength", strength));
    }
}
```

The context gives you:

- `Input` / `Output` — the chain texture coming in and the target to write.
- `Depth` — the scene's depth texture (depth of field, fog).
- `GetScratchTexture(index)` — four extra `Rgba16Float` targets for internal
  ping-pong.
- `Blit(from, to, shaderPath, attributes)` — one fullscreen pass; the shader's
  group-0 uniform buffer is packed from the `RenderAttributes` bag by field
  name.

## Shader convention

Every post-process shader (engine: `Shaders/PostProcesses/`, game:
`Content/Shaders/`) follows the same convention, and the renderer derives
everything else from reflection:

- the **previous chain texture** at binding slot 0,
- a **sampler** at slot 1 (the component's `Sampler` property selects
  linear/point filtering),
- exactly **one uniform buffer in group 0** (any slots) — its fields are
  packed by name from the attributes/`[Property]` values, with trailing
  underscores and case ignored (`operator_` ↔ `Operator`),
- optional `texture_depth_2d` binding — bound to the scene depth,
- `vs_main` / `fs_main` entry points.

## Spatial volumes and blending

Attach effect components to a `PostProcessVolume` entity (a box from the
entity's transform: position = center, scale = full extents) to make them
apply only while the camera is inside, with a `Softness`-controlled falloff.
Effects derived from `BasePostProcess<T>` blend their settings across all
active instances through `GetWeighted` — every volume the camera is in
(weighted by position) plus the global instance at full weight:

```csharp
var exposure = GetWeighted(effect => effect.Exposure);      // blended
var mode = GetWeighted(effect => effect.Operator);          // enum: strongest wins
```

## Hot reload

The editor watches `Shaders/PostProcesses/` (dev tree) and the project's
`Content/Shaders/`, recompiles edited `.slang` files with the vendored slangc,
and swaps the shader and pipeline on the next frame — no rebuild, no relaunch.
Compile errors appear as notifications. `Content/Shaders/` shaders are also
compiled on editor start and on project switch (they are not part of the
engine build).

## The demo

`Game/Code/Vignette.cs` + `Game/Content/Shaders/Vignette.slang` ship a
complete game-authored effect (corner darkening). Attach **Vignette** to any
entity from the inspector, then edit the `.slang` while the editor runs to see
the hot reload in action.
