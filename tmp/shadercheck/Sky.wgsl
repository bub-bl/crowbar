struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

struct CameraUniforms_std140_0
{
    @align(16) view_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) proj_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) cameraPosition_0 : vec4<f32>,
    @align(16) time_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> camera_0 : CameraUniforms_std140_0;
struct EnvironmentUniforms_std140_0
{
    @align(16) parameters_0 : vec4<f32>,
    @align(16) tint_0 : vec4<f32>,
};

@binding(5) @group(2) var<uniform> environment_0 : EnvironmentUniforms_std140_0;
@binding(0) @group(2) var environmentMap_0 : texture_cube<f32>;

@binding(4) @group(2) var environmentSampler_0 : sampler;

struct VertexOutput_0
{
    @builtin(position) position_0 : vec4<f32>,
    @location(0) clip_0 : vec2<f32>,
};

@vertex
fn vs_main(@builtin(vertex_index) vertexId_0 : u32) -> VertexOutput_0
{
    var _S1 : vec2<f32> = vec2<f32>(f32((((vertexId_0 << (u32(1)))) & (u32(2)))), f32((vertexId_0 & (u32(2))))) * vec2<f32>(2.0f) - vec2<f32>(1.0f);
    var output_0 : VertexOutput_0;
    output_0.position_0 = vec4<f32>(_S1, 1.0f, 1.0f);
    output_0.clip_0 = _S1;
    return output_0;
}

fn EnvironmentLighting_Rotate_0( direction_0 : vec3<f32>) -> vec3<f32>
{
    var _S2 : f32 = environment_0.parameters_0.x;
    var _S3 : f32 = sin(_S2);
    var _S4 : f32 = cos(_S2);
    var _S5 : f32 = direction_0.x;
    var _S6 : f32 = direction_0.z;
    return vec3<f32>(_S4 * _S5 - _S3 * _S6, direction_0.y, _S3 * _S5 + _S4 * _S6);
}

fn EnvironmentLighting_ApplySettings_0( color_0 : vec3<f32>) -> vec3<f32>
{
    return color_0 * environment_0.tint_0.xyz * vec3<f32>(environment_0.parameters_0.y) * vec3<f32>(exp2(environment_0.parameters_0.z));
}

fn EnvironmentLighting_SampleSky_0( direction_1 : vec3<f32>) -> vec3<f32>
{
    return EnvironmentLighting_ApplySettings_0((textureSampleLevel((environmentMap_0), (environmentSampler_0), (EnvironmentLighting_Rotate_0(direction_1)), (0.0f))).xyz);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) clip_1 : vec2<f32>,
};

@fragment
fn fs_main( _S7 : pixelInput_0, @builtin(position) position_1 : vec4<f32>) -> pixelOutput_0
{
    var _S8 : mat4x4<f32> = mat4x4<f32>(camera_0.view_0.data_0[i32(0)][i32(0)], camera_0.view_0.data_0[i32(1)][i32(0)], camera_0.view_0.data_0[i32(2)][i32(0)], camera_0.view_0.data_0[i32(3)][i32(0)], camera_0.view_0.data_0[i32(0)][i32(1)], camera_0.view_0.data_0[i32(1)][i32(1)], camera_0.view_0.data_0[i32(2)][i32(1)], camera_0.view_0.data_0[i32(3)][i32(1)], camera_0.view_0.data_0[i32(0)][i32(2)], camera_0.view_0.data_0[i32(1)][i32(2)], camera_0.view_0.data_0[i32(2)][i32(2)], camera_0.view_0.data_0[i32(3)][i32(2)], camera_0.view_0.data_0[i32(0)][i32(3)], camera_0.view_0.data_0[i32(1)][i32(3)], camera_0.view_0.data_0[i32(2)][i32(3)], camera_0.view_0.data_0[i32(3)][i32(3)]);
    var _S9 : pixelOutput_0 = pixelOutput_0( vec4<f32>(EnvironmentLighting_SampleSky_0(normalize((((normalize(vec3<f32>(_S7.clip_1.x / camera_0.proj_0.data_0[i32(0)][i32(0)], _S7.clip_1.y / camera_0.proj_0.data_0[i32(1)][i32(1)], 1.0f))) * (transpose(mat3x3<f32>(_S8[i32(0)].xyz, _S8[i32(1)].xyz, _S8[i32(2)].xyz))))))), 1.0f) );
    return _S9;
}

