// Selection outline pass: replaces the plain scene blit when an entity is
// selected. Samples the finished scene texture and the selection mask, then
// dilates the mask with a tent-weighted disk to tint a soft band just outside
// the silhouette — a real contour, independent of the mesh's own geometry
// (no inverted-hull scaling, which breaks at silhouettes and thin features).

struct OutlineParams {
    color: vec4<f32>,       // rgb = outline color, a = opacity
    texel_size: vec2<f32>,  // 1 / viewport width, 1 / viewport height
    thickness: f32,         // outline radius in pixels
    pad: f32,
};

@group(0) @binding(0) var scene_tex: texture_2d<f32>;
@group(0) @binding(1) var mask_tex: texture_2d<f32>;
@group(0) @binding(2) var samp: sampler;
@group(0) @binding(3) var<uniform> params: OutlineParams;

// The scene texture is sRGB: sampling it with texture_2d<f32> yields linear
// values, and the plain blit (Ui.wgsl) decodes them once more (srgbToLinear)
// before the sRGB target re-encodes on store — the gamma-correct round trip
// of the whole pipeline. This pass must reproduce that exact handling, or
// the scene comes out brighter while an entity is selected.
fn srgbToLinear(c: vec3<f32>) -> vec3<f32> {
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3<f32>(2.4));
    return select(hi, lo, c <= vec3<f32>(0.04045));
}

struct VertexOutput {
    @builtin(position) position: vec4<f32>,
    @location(0) uv: vec2<f32>,
};

@vertex
fn vs_main(@location(0) position: vec2<f32>, @location(1) uv: vec2<f32>) -> VertexOutput {
    var output: VertexOutput;
    output.position = vec4<f32>(position, 0.0, 1.0);
    output.uv = uv;
    return output;
}

@fragment
fn fs_main(input: VertexOutput) -> @location(0) vec4<f32> {
    // Un-premultiply and decode sRGB exactly like the scene blit (Ui.wgsl), so
    // pixels outside the outline band are bit-identical to the blit.
    let tex = textureSample(scene_tex, samp, input.uv);
    let alpha = tex.a;
    var rgb = vec3<f32>(0.0);
    if (alpha > 0.0) {
        rgb = srgbToLinear(clamp(tex.rgb / alpha, vec3<f32>(0.0), vec3<f32>(1.0)));
    }

    let center = textureSample(mask_tex, samp, input.uv).r;

    // Inside the selected object's silhouette: keep the scene exactly as-is.
    if (center > 0.5) {
        return vec4<f32>(rgb, alpha);
    }

    // Outside: weight the mask over a disk of radius `thickness`. Right at
    // the silhouette roughly half the disk overlaps the object, so the sum
    // peaks there and falls off with distance — a band with soft inner and
    // outer edges instead of a hard 1px fringe.
    let radius = i32(clamp(round(params.thickness), 1.0, 12.0));
    var outside = 0.0;
    var total = 0.0;
    for (var dy = -radius; dy <= radius; dy++) {
        for (var dx = -radius; dx <= radius; dx++) {
            let distance = sqrt(f32(dx * dx + dy * dy));
            if (distance > f32(radius)) {
                continue;
            }
            let weight = 1.0 - distance / (f32(radius) + 1.0);
            total += weight;
            outside += weight * textureSample(mask_tex, samp,
                input.uv + vec2<f32>(f32(dx), f32(dy)) * params.texel_size).r;
        }
    }

    let edge = clamp(outside * (2.0 / total), 0.0, 1.0);
    let blend = smoothstep(0.2, 0.8, edge) * params.color.a;
    // Mix in linear space; the sRGB target re-encodes on store. The alpha is
    // the scene's own, so translucent pixels (grid lines) composite over the
    // cleared surface exactly like the blit.
    return vec4<f32>(mix(rgb, params.color.rgb, blend), alpha);
}
