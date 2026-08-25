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
- `GetBloomLevelTexture(index)` / `GetBloomLevelWidth/Height(index)` — the
  downsized bloom pyramid (½, ¼, ⅛, 1/16), used by Bloom.
- `Blit(from, to, shaderPath, attributes)` — one fullscreen pass; the shader's
  group-0 uniform buffer is packed from the `RenderAttributes` bag by field
  name.
- `BlitAdditive(from, to, shaderPath, attributes)` — the same pass with a loaded
  target and additive color blending, used to accumulate bloom pyramid levels.
- `Blit(from, to, shaderPath, attributes, secondary)` — the same, additionally
  binding a second texture at slot 3 (and its sampler at slot 4), for effects
  that read two inputs at once (Bloom's final scene + glow combine).

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
- for two-input passes (Bloom's combine): a **secondary texture at slot 3**
  and its **sampler at slot 4**,
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

## The vignette

The engine ships a `Vignette` post-process (corner darkening) as a
first-class component: `src/Engine/World/Vignette.cs` drives
`Shaders/PostProcesses/Vignette.slang`, and the shader hot-reloads like any
other (edit the `.slang` while the editor runs to see it in action). Attach
**Vignette** to any entity from the inspector; it blends across
`PostProcessVolume` instances through `BasePostProcess<T>.GetWeighted`.
The effect is aspect-ratio corrected and defaults to order `100`, so it runs
after HDR bloom and tonemapping.

## Chromatic aberration

`ChromaticAberration` simulates wavelength-dependent lens refraction by
separating red and blue radially toward the frame edges. The displacement is
aspect-ratio corrected and measured in pixels. `Intensity` controls the
separation and `Start` controls the normalized radius where it begins. It
defaults to order `50`, after tonemapping and before the vignette.

## Film grain

`FilmGrain` adds temporally animated monochrome grain after the lens and color
effects. It uses a centered triangular noise distribution at 24 patterns per
second, which avoids changing the average image brightness. `Intensity`
controls the grain strength, while `Response` suppresses grain progressively
in highlights. The effect defaults to order `150` and is disabled by default.

Tonemapping is applied in linear HDR before the display blit. `Reinhard` uses
the standard per-channel curve, `Aces` uses the fitted ACES RRT/ODT transform
with its input/output color matrices, and `Agx` uses the analytic AgX view
transform (including its log encoding and default contrast). Exposure is in
stops and is applied before the selected curve. Tonemapping defaults to order
`0`; Bloom defaults to `-100` so bright extraction sees the original HDR scene.

## The bloom

The engine ships a Gaussian-pyramid `Bloom` post-process in the style of
Unreal and Unity: `src/Engine/World/Bloom.cs` drives
`Shaders/PostProcesses/Bloom{Prefilter,Downsample,Upsample,Combine}.slang`.
It extracts the brights from the linear HDR scene, downsamples them into a
half-res pyramid, blurs them outward through an upsample cascade, and adds
the glow back before tonemapping (so the two compose). The final combine
reads the scene (slot 0) and the glow (slot 3) in one pass through the
secondary-input `Blit` overload.

Attach **Bloom** to any entity from the inspector and tune
`Intensity`, `Threshold`/`ThresholdKnee`, `Scatter`, `DownsampleCount` and
`Clamp`; it blends across `PostProcessVolume` instances like every other
`BasePostProcess<T>`. Because bloom is multi-pass, drive it from the renderer's
bloom pyramid (`GetBloomLevelTexture`) rather than the full-res scratch pool,
so the downsample and upsample passes run at the right resolutions.
