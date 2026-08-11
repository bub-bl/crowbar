// Shared lighting library: the light buffer the engine binds to group 0
// binding 1, plus the BRDF functions every lit shader reuses. This is the
// "class library" of the shader API — Mesh, Pbr and any future shadow pass
// include it instead of re-declaring lights or re-writing fresnel/GGX.

struct LightData {
    position_type: vec4<f32>,    // xyz = world position, w = 0 directional / 1 point
    color_intensity: vec4<f32>,  // rgb = color, w = intensity
    direction_range: vec4<f32>,  // xyz = direction (directional), w = range (point)
};

struct LightsUniform {
    // Explicit u32 padding (a vec3 pad would 16-align past offset 16 and
    // shift the array to 32); the engine packs count + 3 padders then the
    // array at offset 16.
    count: u32,
    pad0: u32,
    pad1: u32,
    pad2: u32,
    lights: array<LightData, 8>,
};

@group(0) @binding(1) var<uniform> lights: LightsUniform;

fn fresnelSchlick(cosine: f32, f0: vec3<f32>) -> vec3<f32> {
    return f0 + (vec3<f32>(1.0) - f0) * pow(1.0 - cosine, 5.0);
}

fn distributionGgx(n: vec3<f32>, h: vec3<f32>, roughness: f32) -> f32 {
    let a = roughness * roughness;
    let a2 = a * a;
    let ndoth = max(dot(n, h), 0.0);
    let denominator = ndoth * ndoth * (a2 - 1.0) + 1.0;
    return a2 / max(3.14159265 * denominator * denominator, 0.0001);
}

fn geometrySchlick(cosine: f32, roughness: f32) -> f32 {
    let k = (roughness + 1.0) * (roughness + 1.0) / 8.0;
    return cosine / max(cosine * (1.0 - k) + k, 0.0001);
}

fn lightDirection(light: LightData, surfacePosition: vec3<f32>) -> vec3<f32> {
    if (light.position_type.w > 0.5) {
        return normalize(light.position_type.xyz - surfacePosition);
    }
    return normalize(light.direction_range.xyz);
}

fn evaluateLight(light: LightData, normal: vec3<f32>, viewDir: vec3<f32>, surfacePosition: vec3<f32>, baseColor: vec3<f32>, metallic: f32, roughness: f32) -> vec3<f32> {
    let lightDir = lightDirection(light, surfacePosition);
    let ndotl = max(dot(normal, lightDir), 0.0);
    if (ndotl <= 0.0) {
        return vec3<f32>(0.0);
    }

    let halfVector = normalize(lightDir + viewDir);
    let ndotv = max(dot(normal, viewDir), 0.0);
    let hdotv = max(dot(halfVector, viewDir), 0.0);
    let f0 = mix(vec3<f32>(0.04), baseColor, metallic);
    let fresnel = fresnelSchlick(hdotv, f0);
    let specular = distributionGgx(normal, halfVector, roughness)
        * geometrySchlick(ndotl, roughness) * geometrySchlick(ndotv, roughness)
        * fresnel / max(4.0 * ndotl * ndotv, 0.001);
    let diffuse = (vec3<f32>(1.0) - fresnel) * (1.0 - metallic) * baseColor / 3.14159265;

    var attenuation = 1.0;
    if (light.position_type.w > 0.5) {
        let dist = length(light.position_type.xyz - surfacePosition);
        let range = light.direction_range.w;
        attenuation = clamp(1.0 - dist / max(range, 0.0001), 0.0, 1.0);
        attenuation = attenuation * attenuation;
    }

    return (diffuse + specular) * ndotl * light.color_intensity.rgb * light.color_intensity.w * attenuation;
}

fn tonemap(color: vec3<f32>) -> vec3<f32> {
    let mapped = color / (color + vec3<f32>(1.0));
    return pow(mapped, vec3<f32>(1.0 / 2.2));
}
