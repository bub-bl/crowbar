struct VertexOutput { @builtin(position) position: vec4f, @location(0) uv: vec2f }
@group(0) @binding(0) var ui_texture: texture_2d<f32>;
@group(0) @binding(1) var ui_sampler: sampler;
@vertex fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f) -> VertexOutput {
  var o: VertexOutput;
  o.position = vec4f(position, 0.0, 1.0); o.uv = uv; return o;
}

fn srgbToLinear(c: vec3f) -> vec3f {
  let lo = c / 12.92;
  let hi = pow((c + 0.055) / 1.055, vec3f(2.4));
  return select(hi, lo, c <= vec3f(0.04045));
}

@fragment fn fs_main(input: VertexOutput) -> @location(0) vec4f {
  // The UI texture stores Skia's premultiplied sRGB-encoded RGBA (uploaded raw
  // from the raster bitmap, no CPU conversion). Linear filtering on
  // premultiplied values is correct (no fringe on soft edges); un-premultiply
  // and decode sRGB here so the SrcAlpha blend below reproduces browser-style
  // straight-alpha compositing, while opaque pixels round-trip exactly.
  let tex = textureSample(ui_texture, ui_sampler, input.uv);
  let a = tex.a;
  var rgb = vec3f(0.0);
  if (a > 0.0) {
    rgb = srgbToLinear(clamp(tex.rgb / a, vec3f(0.0), vec3f(1.0)));
  }
  return vec4f(rgb, a);
}
