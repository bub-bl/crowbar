// Shared shadow-mapping library. The engine renders shadow-casting lights
// into a single 2D depth atlas (one 512px tile per face) and binds it here to
// group 0 bindings 2-4, alongside the per-light shadow metadata written by the
// renderer (Common/Lighting.wgsl owns the light array itself). Lit shaders
// include this file and multiply their per-light radiance by shadowFactor.

struct ShadowFace {
    uvRect: vec4<f32>,    // atlas UV rect of this face: xy = top-left, zw = bottom-right
    viewProj: mat4x4<f32>, // world -> light clip space
}

struct ShadowLight {
    flags: vec4<f32>,     // x = enabled, y = type (0 directional / 1 point), z = face count, w = bias
    faces: array<ShadowFace, 6>,
}

struct ShadowUniforms {
    atlasSize: vec4<f32>, // xy = atlas dimensions in texels, z = slope bias scale
    lights: array<ShadowLight, 8>,
}

@group(0) @binding(2) var<uniform> shadows: ShadowUniforms;
@group(0) @binding(3) var shadowMap: texture_depth_2d;
@group(0) @binding(4) var shadowSampler: sampler_comparison;

fn shadowFace(data: ShadowLight, index: u32) -> ShadowFace {
    // Select the face explicitly so every read is constant-indexed (some naga
    // builds are stricter about dynamic indexing into struct-member arrays).
    if (index == 0u) { return data.faces[0]; }
    if (index == 1u) { return data.faces[1]; }
    if (index == 2u) { return data.faces[2]; }
    if (index == 3u) { return data.faces[3]; }
    if (index == 4u) { return data.faces[4]; }
    return data.faces[5];
}

// 3x3 percentage-closer filtering of one face. `lightDir` is the normalized
// direction from the surface toward the light (already computed by the BRDF).
fn sampleShadowFace(face: ShadowFace, ndc: vec3<f32>, bias: f32) -> f32 {
    // Outside the face frustum: the light is not occluded here.
    if (ndc.x < -1.0 || ndc.x > 1.0 || ndc.y < -1.0 || ndc.y > 1.0) {
        return 1.0;
    }

    // WebGPU framebuffer origin is top-left, so NDC +y maps to v = 0.
    let uv = vec2<f32>(ndc.x * 0.5 + 0.5, 0.5 - ndc.y * 0.5);
    let uvSize = face.uvRect.zw - face.uvRect.xy;
    let atlasUv = face.uvRect.xy + uv * uvSize;
    let depthRef = clamp(ndc.z - bias, 0.0, 1.0);

    // One atlas texel is constant across all faces because a face's UV span
    // scales exactly with its texel count.
    let texel = vec2<f32>(1.0) / shadows.atlasSize.xy;

    var visibility = 0.0;
    for (var dy = -1; dy <= 1; dy++) {
        for (var dx = -1; dx <= 1; dx++) {
            let offset = vec2<f32>(f32(dx), f32(dy)) * texel;
            visibility += textureSampleCompare(shadowMap, shadowSampler, atlasUv + offset, depthRef);
        }
    }
    return visibility / 9.0;
}

fn shadowFactor(lightIndex: u32, worldPosition: vec3<f32>, lightDir: vec3<f32>, normal: vec3<f32>) -> f32 {
    let data = shadows.lights[lightIndex];
    if (data.flags.x < 0.5) {
        return 1.0;
    }

    // Slope-scaled bias: a surface angled toward the light changes depth fast
    // across the shadow map, so a constant bias aliases into a texel-sized acne
    // lattice (visible as "lines" on floors and walls). Scale the bias by how
    // parallel the surface is to the light (|dot| -> 0) on top of the per-light
    // constant floor, which stays for surfaces facing the light directly.
    let bias = data.flags.w + shadows.atlasSize.z * (1.0 - abs(dot(normal, lightDir)));
    if (data.flags.y < 0.5) {
        // Directional light: one orthographic face.
        let face = data.faces[0];
        let clip = face.viewProj * vec4<f32>(worldPosition, 1.0);
        return sampleShadowFace(face, clip.xyz / clip.w, bias);
    }

    // Point light: pick the cube face the surface lies in. `lightDir` points
    // from the surface toward the light (Lighting.wgsl), so the direction from
    // the light toward the surface is its negation.
    let d = normalize(-lightDir);
    let ax = abs(d.x);
    let ay = abs(d.y);
    let az = abs(d.z);
    var index = 4u;
    if (ax >= ay && ax >= az) {
        index = select(0u, 1u, d.x < 0.0);
    } else if (ay >= az) {
        index = select(2u, 3u, d.y < 0.0);
    } else {
        index = select(4u, 5u, d.z < 0.0);
    }

    let face = shadowFace(data, index);
    let clip = face.viewProj * vec4<f32>(worldPosition, 1.0);
    return sampleShadowFace(face, clip.xyz / clip.w, bias);
}
