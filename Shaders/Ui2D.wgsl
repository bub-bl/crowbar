// Blit pass for the Renderer2D offscreen target (the GPU-rendered UI layer).
//
// The Renderer2D draws into an Rgba8Unorm target whose shaders already write
// linear color + straight (non-premultiplied) alpha. Unlike Ui.wgsl (which
// un-premultiplies and decodes Skia's sRGB raster), this shader is a plain
// passthrough: it forwards the linear straight-alpha texel so the SrcAlpha
// blend below reproduces browser-style compositing on the surface.

struct VertexOutput { @builtin(position) position: vec4f, @location(0) uv: vec2f }

@group(0) @binding(0) var ui_texture: texture_2d<f32>;
@group(0) @binding(1) var ui_sampler: sampler;

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f) -> VertexOutput {
  var o: VertexOutput;
  o.position = vec4f(position, 0.0, 1.0);
  o.uv = uv;
  return o;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4f {
  return textureSample(ui_texture, ui_sampler, input.uv);
}
