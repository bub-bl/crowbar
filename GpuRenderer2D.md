# Crowbar 2D Renderer — WebGPU-first architecture

This document is the design and migration plan for replacing Skia with a
native, GPU-first 2D renderer built exclusively on WebGPU through the
backend-neutral `IGraphicsDevice` abstraction. Skia is not reproduced; this is
a renderer designed for a game engine and a professional editor.

## 1. Goals

- **GPU-first.** Every pixel is produced on the GPU. The CPU only records
  commands and packs instance data — it never rasterizes.
- **Batched.** Thousands of shapes collapse into a handful of draw calls
  (`DrawInstanced` / `Draw`), independent of scene complexity.
- **Zero allocation per frame.** Recording reuses pre-sized buffers; steady
  state allocates nothing.
- **No WebGPU in the public API.** Callers see `Begin` / `DrawRect` /
  `DrawText` / `End`. WebGPU (Silk.NET.WebGPU) is entirely internal.
- **Backend-neutral.** The renderer only talks to `IGraphicsDevice`,
  `ITexture`, `IBuffer`, `IPipeline`, `IRenderPass` — so a Vulkan backend can
  replace WebGPU without touching the drawing API.
- **ECS / Render Graph friendly.** A frame is an ordered command list; the GPU
  resources are transient, cacheable and reusable.

## 2. Layered architecture

```text
UI framework (panels, docking, Razor)         Editor (gizmos, graphs, overlays)
        │                                                 │
        └─────────────── Renderer2D (public drawing API) ─┘
                              │  records
                              ▼
        Command list → Batch builder → instance / vertex buffers (reused)
                              │
                              ▼
                  WebGPU pipelines (SdfShape, TriMesh)   ← WGSL
                              │
                              ▼
                 Offscreen texture (ITexture) → compositor / present
```

`Renderer2D` (namespace `Crowbar.Engine.Rendering2D`, project `Crowbar.Engine`)
lives in the engine, not the UI framework, so the drawing surface is available
to the UI framework, the editor, gizmos, the HUD and debug graphs alike.

## 3. The two GPU pipelines

A single unified vertex format cannot express both a sharp rounded rect and an
arbitrary polygon cheaply, so the renderer splits into two pipelines. Paint
order is preserved because both are issued into the **same render pass** in
submission order; batching merges *consecutive* same-kind commands.

### 3.1 `SdfShape.wgsl` — instanced signed-distance shapes

One unit quad (position + uv, 6 vertices) is instanced once per shape. Each
instance carries the shape's local bounding box, a 2×3 transform, a color, an
inline gradient (up to 4 stops) and up to 4 screen-space clips. The fragment
shader evaluates the shape as an SDF:

- **Rect / RoundedRect** — rounded-rectangle SDF (iQuilez).
- **Circle / Ellipse** — ellipse SDF inscribed in the bounding box.
- **Line / Polyline** — distance to the segment (round caps fall out of the
  clamped projection); a polyline is one instance per segment.
- **Stroke (borders)** — `abs(sdf) - width/2` produces the ring.
- **Gradients** — linear (projection onto the start→end segment) and radial
  (distance from center), interpolated over the inline stops.
- **Clips** — up to 4 stacked rounded-rect masks in screen space, multiplied
  into coverage.

This is the workhorse: an entire UI (rects, panels, buttons, scrollbars,
gizmo lines) is a **single `DrawInstanced` call**.

### 3.2 `TriMesh.wgsl` — tessellated triangles

For geometry the SDF cannot express (concave polygons today; SVG paths and
MSDF glyph quads later), the CPU tessellates into screen-space triangles
carrying a position + straight sRGB color, accumulated into one vertex buffer
and drawn with a single `Draw`. Polygons use ear-clipping; clipping against
the active rect uses Sutherland–Hodgman.

### 3.3 `Textured.wgsl` — image quads

Images are decoded on the CPU (ImageSharp → RGBA8), packed into a **texture
atlas** (shelf packing, 1px gutter, half-texel UV inset) and drawn as
screen-space quads carrying an atlas UV + a straight sRGB tint. One `Draw`
renders every image. The atlas grows (doubles) and re-uploads on overflow,
updating `Image2D` UV rects in place, so all images stay in a single bind
group and a single draw call. Object-fit (stretch/contain/cover/center) is
computed on the CPU before the quad is emitted.

### 3.4 `Glyph.wgsl` — text

Text is laid out and shaped by **SixLabors.Fonts** (`TextRenderer.RenderTo`,
which applies kerning, ligatures and wrapping). Each glyph outline is flattened
(adaptive de Casteljau), rasterized with even-odd winding and converted to a
**single-channel signed distance field** (exact two-pass EDT), then packed into
a glyph atlas. A glyph is one screen-space quad sampling the atlas with the
`Glyph` shader, which smoothsteps the distance for antialiasing — text is crisp
at any scale with a single rasterization per (font, size, glyph), cached in the
atlas. Measurement reuses the same `TextOptions` as rendering, so layout and
pixels stay in lockstep.

## 4. C# structures and buffers

| Resource | Contents | Usage |
|---|---|---|
| `SdfInstance` (288 B) | 18 `Vector4` — rect, matrix rows, color, params, gradient, 4 clips | `Storage | CopyDst`, one per shape |
| `TriVertex` (24 B) | `Vector2` + `Vector4` | `Vertex | CopyDst`, one per triangle vertex |
| `TexturedVertex` (32 B) | `Vector2` + `Vector2` uv + `Vector4` tint | `Vertex | CopyDst`, one per image/glyph quad vertex |
| image atlas | `RGBA8` square texture (256→4096, shelf-packed) | `Sampled | CopyDst` |
| glyph atlas | `RGBA8` square texture of SDF glyphs (256→4096, shelf-packed) | `Sampled | CopyDst` |
| unit quad | 6 × (position + uv) | `Vertex | CopyDst`, shared by all SDF instances |
| viewport | `Vector4` (size, 1/size) | `Uniform | CopyDst` |
| target / depth | RGBA8 + D24 | render targets, resized with the viewport |

Buffers are created lazily and only **grown**, never reallocated, once they
reach steady-state size. Instance/vertex data is written zero-copy via
`CollectionsMarshal.AsSpan` → `MemoryMarshal.AsBytes`.

## 5. Public API (no WebGPU anywhere)

```csharp
var renderer = new Renderer2D(device);   // device == null → headless record-only

renderer.Begin();                        // or Begin(width, height)

renderer.DrawRect(rect, color);
renderer.DrawRoundedRect(rect, radius, color);          // + stroke overloads
renderer.DrawCircle(center, radius, color);
renderer.DrawEllipse(bounds, color);
renderer.DrawLine(a, b, width, color);
renderer.DrawPolyline(points, width, color);
renderer.DrawPolygon(points, color);
renderer.DrawRect(rect, linearGradient);                // + radial gradients

var image = renderer.LoadImage("ui/icon.png");          // PNG/JPEG/WebP
renderer.DrawImage(rect, image, tint, ImageFit.Contain); // stretch/contain/cover/center

renderer.DrawText("Hello", pos, 24f, color);             // kerning, wrapping, alignment via TextStyle

renderer.PushClip(rect, radius); renderer.PopClip();
renderer.PushTransform(matrix); renderer.PopTransform();
renderer.PushTranslate(t); renderer.PushScale(s); renderer.PushRotate(r);

var texture = renderer.End();           // offscreen texture, sampled by the compositor
```

Transform composition is pre-multiplied (`current = pushed * current`), so the
most recently pushed transform applies first — the canvas/Skia convention.

## 6. Migration from Skia (progressive)

The existing `SkiaUiRenderer` rasterizes the panel tree to a CPU bitmap and
delegates fills/shadows/borders/backdrops to the GPU compositor. Each feature
below replaces one Skia surface with a `Renderer2D` equivalent:

| Feature | Architecture | Shader | Status |
|---|---|---|---|
| Rect / RoundedRect / Circle / Ellipse / Line / Polygon | SDF instance + ear-clip | `SdfShape` / `TriMesh` | ✅ done |
| Solid + gradient fills | inline gradient in the instance | `SdfShape` | ✅ done |
| Rect / rounded clips, transforms | per-instance clip list + matrix | `SdfShape` | ✅ done |
| Solid borders | SDF stroke | `SdfShape` | ✅ done |
| Dashed / dotted / double borders | arc-length along the outline `mod`-ed by the dash pattern (round dots via 2D dot mask) | `SdfShape` | ✅ done |
| **Text** | SixLabors.Fonts shaping + single-channel SDF glyph atlas + per-glyph quads; kerning, alignment, wrapping | `Glyph` | ✅ done |
| **SVG** | CPU parse → flatten paths → tessellate | `TriMesh` | planned |
| **Images (PNG/JPEG/WebP)** | decode (ImageSharp) → texture atlas → image quads with object-fit | `Textured` | ✅ done |
| Box / inner / drop shadow | blurred SDF (mirror `Decorations.wgsl`) | `SdfShape` | planned |
| CSS filters | compose onto an offscreen layer, then filter | new `Filter.wgsl` | planned |
| Backdrop filter | sample the 3D scene texture (already in `Backdrop.wgsl`) | `Backdrop` | exists |

Text is the only CPU-side dependency that remains: shaping, measurement,
kerning and wrapping run on the CPU (SixLabors.Fonts, replacing the Skia-based
`TextLayout`) and are emitted as quads referencing the GPU glyph atlas — the
same split Chromium uses.

## 7. Performance characteristics

- Draw calls: **2 per frame** in the common case (one `DrawInstanced` for
  shapes, one `Draw` for triangles), regardless of how many thousands of
  elements are on screen.
- Pipeline changes: only when SDF and triangle commands interleave.
- Bind groups: one per pipeline, reused every frame.
- Allocation: none in steady state (all buffers pre-sized and `Clear`-ed).
- Transient resources: the offscreen target is owned by the renderer and
  resized only on viewport change, so it plugs directly into a render-graph
  transient-texture pool.

## 8. Limitations (current)

- Gradient stops are capped at 4 per shape (more will sample a gradient atlas).
- Clips under rotation/skew collapse to their axis-aligned bounding box; the
  polygon path approximates rounded clips by their rect. Exact transformed
  rounded clips land with the scissor/SDF-clip-in-local-space work.
- Lines use round caps; butt/bevel caps are a follow-up.
- Triangle edges are hard (no antialiasing) until a coverage attribute is added.
- Text renders monochrome outlines (`ColorFontSupport.None`); color/emoji fonts,
  bidi and complex-script shaping are not yet wired.
- SVG, dashed borders, shadows and filters are specified above and not yet
  implemented — the existing Skia renderer still serves them until each is
  migrated.

## 9. Verification

`tests/Crowbar.UI.Tests/Rendering/Renderer2DTests.cs` covers recording,
batching, paint order, transforms, clips, gradients, polygon tessellation,
steady-state zero-allocation and the WGSL entry-point/binding contract, all
headless (no GPU required).
