struct VignetteUniforms_std140_0
{
    @align(16) intensity_0 : f32,
    @align(4) radius_0 : f32,
};

@binding(2) @group(0) var<uniform> postProcess_0 : VignetteUniforms_std140_0;
@binding(0) @group(0) var sceneTexture_0 : texture_2d<f32>;

@binding(1) @group(0) var sceneSampler_0 : sampler;

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) uv_0 : vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexId_0 : u32) -> VertexOutput_0
{
    var _S1 : vec2<f32> = vec2<f32>(f32((((vertexId_0 << (u32(1)))) & (u32(2)))), f32((vertexId_0 & (u32(2))))) * vec2<f32>(2.0f) - vec2<f32>(1.0f);
    var output_0 : VertexOutput_0;
    output_0.position_0 = vec4<f32>(_S1, 0.0f, 1.0f);
    output_0.uv_0 = vec2<f32>(_S1.x * 0.5f + 0.5f, 0.5f - _S1.y * 0.5f);
    return output_0;
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) uv_1 : vec2<f32>,
};

@fragment
fn fs_main( _S2 : pixelInput_0, @builtin(position) position_1 : vec4<f32>) -> pixelOutput_0
{
    var _S3 : pixelOutput_0 = pixelOutput_0( vec4<f32>((textureSample((sceneTexture_0), (sceneSampler_0), (_S2.uv_1))).xyz * vec3<f32>((1.0f - smoothstep(clamp(1.0f - postProcess_0.radius_0, 0.0f, 0.99900001287460327f), 1.0f, length(_S2.uv_1 - vec2<f32>(0.5f)) / 0.70710676908493042f) * postProcess_0.intensity_0)), 1.0f) );
    return _S3;
}

