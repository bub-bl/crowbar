struct _MatrixStorage_float4x4_ColMajorstd140_0
{
    @align(16) data_0 : array<vec4<f32>, i32(4)>,
};

@binding(0) @group(1) var<uniform> model_0 : _MatrixStorage_float4x4_ColMajorstd140_0;
struct CameraUniforms_std140_0
{
    @align(16) view_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) proj_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
    @align(16) cameraPosition_0 : vec4<f32>,
    @align(16) time_0 : vec4<f32>,
};

@binding(0) @group(0) var<uniform> camera_0 : CameraUniforms_std140_0;
@binding(3) @group(1) var albedoTexture_0 : texture_2d<f32>;

@binding(2) @group(1) var materialSampler_0 : sampler;

struct MaterialUniforms_std140_0
{
    @align(16) color_0 : vec4<f32>,
    @align(16) metallic_0 : f32,
    @align(4) roughness_0 : f32,
    @align(8) occlusion_0 : f32,
    @align(4) emissive_0 : f32,
};

@binding(1) @group(1) var<uniform> material_0 : MaterialUniforms_std140_0;
@binding(4) @group(1) var normalTexture_0 : texture_2d<f32>;

@binding(5) @group(1) var metallicRoughnessTexture_0 : texture_2d<f32>;

@binding(6) @group(1) var occlusionTexture_0 : texture_2d<f32>;

@binding(7) @group(1) var emissiveTexture_0 : texture_2d<f32>;

struct LightData_std140_0
{
    @align(16) position_type_0 : vec4<f32>,
    @align(16) color_intensity_0 : vec4<f32>,
    @align(16) direction_range_0 : vec4<f32>,
};

struct _Array_std140_LightData8_0
{
    @align(16) data_1 : array<LightData_std140_0, i32(8)>,
};

struct LightsUniform_std140_0
{
    @align(16) count_0 : u32,
    @align(4) pad0_0 : u32,
    @align(8) pad1_0 : u32,
    @align(4) pad2_0 : u32,
    @align(16) lights_0 : _Array_std140_LightData8_0,
};

@binding(1) @group(0) var<uniform> lights_1 : LightsUniform_std140_0;
struct ShadowFace_std140_0
{
    @align(16) uvRect_0 : vec4<f32>,
    @align(16) viewProj_0 : _MatrixStorage_float4x4_ColMajorstd140_0,
};

struct _Array_std140_ShadowFace6_0
{
    @align(16) data_2 : array<ShadowFace_std140_0, i32(6)>,
};

struct ShadowLight_std140_0
{
    @align(16) flags_0 : vec4<f32>,
    @align(16) faces_0 : _Array_std140_ShadowFace6_0,
};

struct _Array_std140_ShadowLight8_0
{
    @align(16) data_3 : array<ShadowLight_std140_0, i32(8)>,
};

struct ShadowUniforms_std140_0
{
    @align(16) atlasSize_0 : vec4<f32>,
    @align(16) lights_2 : _Array_std140_ShadowLight8_0,
};

@binding(2) @group(0) var<uniform> shadows_0 : ShadowUniforms_std140_0;
@binding(3) @group(0) var shadowMap_0 : texture_depth_2d;

@binding(4) @group(0) var shadowSampler_0 : sampler_comparison;

struct EnvironmentUniforms_std140_0
{
    @align(16) parameters_0 : vec4<f32>,
    @align(16) tint_0 : vec4<f32>,
};

@binding(5) @group(2) var<uniform> environment_0 : EnvironmentUniforms_std140_0;
@binding(1) @group(2) var irradianceMap_0 : texture_cube<f32>;

@binding(4) @group(2) var environmentSampler_0 : sampler;

@binding(2) @group(2) var prefilteredSpecularMap_0 : texture_cube<f32>;

@binding(3) @group(2) var brdfLut_0 : texture_2d<f32>;

struct VertexOutput_0
{
    @builtin(position) clip_position_0 : vec4<f32>,
    @location(0) world_position_0 : vec3<f32>,
    @location(1) normal_0 : vec3<f32>,
    @location(2) tangent_0 : vec4<f32>,
    @location(3) uv_0 : vec2<f32>,
};

struct VertexInput_0
{
     position_0 : vec3<f32>,
     normal_1 : vec3<f32>,
     tangent_1 : vec4<f32>,
     uv_1 : vec2<f32>,
};

fn VertexTransform_ToClip_0( input_0 : VertexInput_0,  model_1 : mat4x4<f32>) -> VertexOutput_0
{
    var _S1 : vec4<f32> = (((vec4<f32>(input_0.position_0, 1.0f)) * (model_1)));
    var output_0 : VertexOutput_0;
    output_0.world_position_0 = _S1.xyz;
    var _S2 : mat3x3<f32> = mat3x3<f32>(model_1[i32(0)].xyz, model_1[i32(1)].xyz, model_1[i32(2)].xyz);
    output_0.normal_0 = normalize((((input_0.normal_1) * (_S2))));
    output_0.tangent_0 = vec4<f32>(normalize((((input_0.tangent_1.xyz) * (_S2)))), input_0.tangent_1.w);
    output_0.uv_0 = input_0.uv_1;
    output_0.clip_position_0 = (((_S1) * ((((mat4x4<f32>(camera_0.view_0.data_0[i32(0)][i32(0)], camera_0.view_0.data_0[i32(1)][i32(0)], camera_0.view_0.data_0[i32(2)][i32(0)], camera_0.view_0.data_0[i32(3)][i32(0)], camera_0.view_0.data_0[i32(0)][i32(1)], camera_0.view_0.data_0[i32(1)][i32(1)], camera_0.view_0.data_0[i32(2)][i32(1)], camera_0.view_0.data_0[i32(3)][i32(1)], camera_0.view_0.data_0[i32(0)][i32(2)], camera_0.view_0.data_0[i32(1)][i32(2)], camera_0.view_0.data_0[i32(2)][i32(2)], camera_0.view_0.data_0[i32(3)][i32(2)], camera_0.view_0.data_0[i32(0)][i32(3)], camera_0.view_0.data_0[i32(1)][i32(3)], camera_0.view_0.data_0[i32(2)][i32(3)], camera_0.view_0.data_0[i32(3)][i32(3)])) * (mat4x4<f32>(camera_0.proj_0.data_0[i32(0)][i32(0)], camera_0.proj_0.data_0[i32(1)][i32(0)], camera_0.proj_0.data_0[i32(2)][i32(0)], camera_0.proj_0.data_0[i32(3)][i32(0)], camera_0.proj_0.data_0[i32(0)][i32(1)], camera_0.proj_0.data_0[i32(1)][i32(1)], camera_0.proj_0.data_0[i32(2)][i32(1)], camera_0.proj_0.data_0[i32(3)][i32(1)], camera_0.proj_0.data_0[i32(0)][i32(2)], camera_0.proj_0.data_0[i32(1)][i32(2)], camera_0.proj_0.data_0[i32(2)][i32(2)], camera_0.proj_0.data_0[i32(3)][i32(2)], camera_0.proj_0.data_0[i32(0)][i32(3)], camera_0.proj_0.data_0[i32(1)][i32(3)], camera_0.proj_0.data_0[i32(2)][i32(3)], camera_0.proj_0.data_0[i32(3)][i32(3)])))))));
    return output_0;
}

struct vertexInput_0
{
    @location(0) position_1 : vec3<f32>,
    @location(1) normal_2 : vec3<f32>,
    @location(2) tangent_2 : vec4<f32>,
    @location(3) uv_2 : vec2<f32>,
};

@vertex
fn vs_main( _S3 : vertexInput_0) -> VertexOutput_0
{
    var _S4 : VertexInput_0 = VertexInput_0( _S3.position_1, _S3.normal_2, _S3.tangent_2, _S3.uv_2 );
    return VertexTransform_ToClip_0(_S4, mat4x4<f32>(model_0.data_0[i32(0)][i32(0)], model_0.data_0[i32(1)][i32(0)], model_0.data_0[i32(2)][i32(0)], model_0.data_0[i32(3)][i32(0)], model_0.data_0[i32(0)][i32(1)], model_0.data_0[i32(1)][i32(1)], model_0.data_0[i32(2)][i32(1)], model_0.data_0[i32(3)][i32(1)], model_0.data_0[i32(0)][i32(2)], model_0.data_0[i32(1)][i32(2)], model_0.data_0[i32(2)][i32(2)], model_0.data_0[i32(3)][i32(2)], model_0.data_0[i32(0)][i32(3)], model_0.data_0[i32(1)][i32(3)], model_0.data_0[i32(2)][i32(3)], model_0.data_0[i32(3)][i32(3)]));
}

struct Material_0
{
     WorldPosition_0 : vec3<f32>,
     BaseColor_0 : vec3<f32>,
     Normal_0 : vec3<f32>,
     Metallic_0 : f32,
     Roughness_0 : f32,
     Occlusion_0 : f32,
     Emissive_0 : vec3<f32>,
     Opacity_0 : f32,
};

fn Material_Init_0( input_1 : VertexOutput_0) -> Material_0
{
    var m_0 : Material_0;
    m_0.WorldPosition_0 = input_1.world_position_0;
    m_0.BaseColor_0 = vec3<f32>(1.0f);
    m_0.Normal_0 = normalize(input_1.normal_0);
    m_0.Metallic_0 = 0.0f;
    m_0.Roughness_0 = 0.5f;
    m_0.Occlusion_0 = 1.0f;
    m_0.Emissive_0 = vec3<f32>(0.0f);
    m_0.Opacity_0 = 1.0f;
    return m_0;
}

fn Material_NormalFromMap_0( input_2 : VertexOutput_0,  normalMap_0 : vec3<f32>) -> vec3<f32>
{
    var _S5 : vec3<f32> = normalize(input_2.normal_0);
    var _S6 : vec3<f32> = input_2.tangent_0.xyz;
    var _S7 : vec3<f32> = normalize(_S6 - _S5 * vec3<f32>(dot(_S5, _S6)));
    var _S8 : vec3<f32> = normalMap_0 * vec3<f32>(2.0f) - vec3<f32>(1.0f);
    return normalize(_S7 * vec3<f32>(_S8.x) + normalize(cross(_S5, _S7) * vec3<f32>(input_2.tangent_0.w)) * vec3<f32>(_S8.y) + _S5 * vec3<f32>(_S8.z));
}

fn Lighting_Direction_0( light_0 : ptr<function, LightData_std140_0>,  surfacePosition_0 : vec3<f32>) -> vec3<f32>
{
    var _S9 : vec4<f32> = (*light_0).position_type_0;
    if(((*light_0).position_type_0.w) > 0.5f)
    {
        return normalize(_S9.xyz - surfacePosition_0);
    }
    return normalize((vec3<f32>(0) - (*light_0).direction_range_0.xyz));
}

fn Lighting_Direction_1( light_1 : ptr<function, LightData_std140_0>,  surfacePosition_1 : vec3<f32>) -> vec3<f32>
{
    var _S10 : vec4<f32> = (*light_1).position_type_0;
    if(((*light_1).position_type_0.w) > 0.5f)
    {
        return normalize(_S10.xyz - surfacePosition_1);
    }
    return normalize((vec3<f32>(0) - (*light_1).direction_range_0.xyz));
}

fn Lighting_FresnelSchlick_0( cosine_0 : f32,  f0_0 : vec3<f32>) -> vec3<f32>
{
    return f0_0 + (vec3<f32>(1.0f) - f0_0) * vec3<f32>(pow(1.0f - cosine_0, 5.0f));
}

fn Lighting_DistributionGgx_0( n_0 : vec3<f32>,  h_0 : vec3<f32>,  roughness_1 : f32) -> f32
{
    var _S11 : f32 = roughness_1 * roughness_1;
    var _S12 : f32 = _S11 * _S11;
    var _S13 : f32 = max(dot(n_0, h_0), 0.0f);
    var _S14 : f32 = _S13 * _S13 * (_S12 - 1.0f) + 1.0f;
    return _S12 / max(3.14159274101257324f * _S14 * _S14, 0.00009999999747379f);
}

fn Lighting_GeometrySchlick_0( cosine_1 : f32,  roughness_2 : f32) -> f32
{
    var _S15 : f32 = roughness_2 + 1.0f;
    var _S16 : f32 = _S15 * _S15 / 8.0f;
    return cosine_1 / max(cosine_1 * (1.0f - _S16) + _S16, 0.00009999999747379f);
}

fn Lighting_Evaluate_0( light_2 : ptr<function, LightData_std140_0>,  normal_3 : vec3<f32>,  viewDir_0 : vec3<f32>,  surfacePosition_2 : vec3<f32>,  baseColor_0 : vec3<f32>,  metallic_1 : f32,  roughness_3 : f32) -> vec3<f32>
{
    var _S17 : vec3<f32> = Lighting_Direction_1(&((*light_2)), surfacePosition_2);
    var _S18 : f32 = max(dot(normal_3, _S17), 0.0f);
    if(_S18 <= 0.0f)
    {
        return vec3<f32>(0.0f);
    }
    var _S19 : vec3<f32> = normalize(_S17 + viewDir_0);
    var _S20 : f32 = max(dot(normal_3, viewDir_0), 0.0f);
    var _S21 : vec3<f32> = Lighting_FresnelSchlick_0(max(dot(_S19, viewDir_0), 0.0f), mix(vec3<f32>(0.03999999910593033f), baseColor_0, vec3<f32>(metallic_1)));
    var _S22 : vec3<f32> = vec3<f32>((Lighting_DistributionGgx_0(normal_3, _S19, roughness_3) * Lighting_GeometrySchlick_0(_S18, roughness_3) * Lighting_GeometrySchlick_0(_S20, roughness_3))) * _S21 / vec3<f32>(max(4.0f * _S18 * _S20, 0.00100000004749745f));
    var _S23 : vec3<f32> = (vec3<f32>(1.0f) - _S21) * vec3<f32>((1.0f - metallic_1)) * baseColor_0 / vec3<f32>(3.14159274101257324f);
    var _S24 : vec4<f32> = (*light_2).position_type_0;
    var attenuation_0 : f32;
    if(((*light_2).position_type_0.w) > 0.5f)
    {
        var attenuation_1 : f32 = clamp(1.0f - length(_S24.xyz - surfacePosition_2) / max((*light_2).direction_range_0.w, 0.00009999999747379f), 0.0f, 1.0f);
        attenuation_0 = attenuation_1 * attenuation_1;
    }
    else
    {
        attenuation_0 = 1.0f;
    }
    return (_S23 + _S22) * vec3<f32>(_S18) * (*light_2).color_intensity_0.xyz * vec3<f32>((*light_2).color_intensity_0.w) * vec3<f32>(attenuation_0);
}

fn Shadow_SampleFace_0( face_0 : ptr<function, ShadowFace_std140_0>,  ndc_0 : vec3<f32>,  bias_0 : f32) -> f32
{
    var _S25 : f32 = ndc_0.x;
    var _S26 : bool;
    if(_S25 < -1.0f)
    {
        _S26 = true;
    }
    else
    {
        _S26 = _S25 > 1.0f;
    }
    if(_S26)
    {
        _S26 = true;
    }
    else
    {
        _S26 = (ndc_0.y) < -1.0f;
    }
    if(_S26)
    {
        _S26 = true;
    }
    else
    {
        _S26 = (ndc_0.y) > 1.0f;
    }
    if(_S26)
    {
        return 1.0f;
    }
    var _S27 : vec2<f32> = (*face_0).uvRect_0.xy;
    var _S28 : vec2<f32> = _S27 + vec2<f32>(_S25 * 0.5f + 0.5f, 0.5f - ndc_0.y * 0.5f) * ((*face_0).uvRect_0.zw - _S27);
    var _S29 : f32 = clamp(ndc_0.z - bias_0, 0.0f, 1.0f);
    var _S30 : vec2<f32> = vec2<f32>(1.0f) / shadows_0.atlasSize_0.xy;
    var dy_0 : i32 = i32(-1);
    var visibility_0 : f32 = 0.0f;
    for(;;)
    {
        if(dy_0 <= i32(1))
        {
        }
        else
        {
            break;
        }
        var dx_0 : i32 = i32(-1);
        for(;;)
        {
            if(dx_0 <= i32(1))
            {
            }
            else
            {
                break;
            }
            var visibility_1 : f32 = visibility_0 + (textureSampleCompare((shadowMap_0), (shadowSampler_0), (_S28 + vec2<f32>(f32(dx_0), f32(dy_0)) * _S30), (_S29)));
            dx_0 = dx_0 + i32(1);
            visibility_0 = visibility_1;
        }
        dy_0 = dy_0 + i32(1);
    }
    return visibility_0 / 9.0f;
}

struct ShadowFace_0
{
     uvRect_0 : vec4<f32>,
     viewProj_0 : mat4x4<f32>,
};

fn Shadow_SampleFace_1( face_1 : ShadowFace_0,  ndc_1 : vec3<f32>,  bias_1 : f32) -> f32
{
    var _S31 : f32 = ndc_1.x;
    var _S32 : bool;
    if(_S31 < -1.0f)
    {
        _S32 = true;
    }
    else
    {
        _S32 = _S31 > 1.0f;
    }
    if(_S32)
    {
        _S32 = true;
    }
    else
    {
        _S32 = (ndc_1.y) < -1.0f;
    }
    if(_S32)
    {
        _S32 = true;
    }
    else
    {
        _S32 = (ndc_1.y) > 1.0f;
    }
    if(_S32)
    {
        return 1.0f;
    }
    var _S33 : vec2<f32> = face_1.uvRect_0.xy;
    var _S34 : vec2<f32> = _S33 + vec2<f32>(_S31 * 0.5f + 0.5f, 0.5f - ndc_1.y * 0.5f) * (face_1.uvRect_0.zw - _S33);
    var _S35 : f32 = clamp(ndc_1.z - bias_1, 0.0f, 1.0f);
    var _S36 : vec2<f32> = vec2<f32>(1.0f) / shadows_0.atlasSize_0.xy;
    var dy_1 : i32 = i32(-1);
    var visibility_2 : f32 = 0.0f;
    for(;;)
    {
        if(dy_1 <= i32(1))
        {
        }
        else
        {
            break;
        }
        var dx_1 : i32 = i32(-1);
        for(;;)
        {
            if(dx_1 <= i32(1))
            {
            }
            else
            {
                break;
            }
            var visibility_3 : f32 = visibility_2 + (textureSampleCompare((shadowMap_0), (shadowSampler_0), (_S34 + vec2<f32>(f32(dx_1), f32(dy_1)) * _S36), (_S35)));
            dx_1 = dx_1 + i32(1);
            visibility_2 = visibility_3;
        }
        dy_1 = dy_1 + i32(1);
    }
    return visibility_2 / 9.0f;
}

fn Shadow_Face_0( _S37 : u32,  _S38 : u32) -> ShadowFace_0
{
    if(_S38 == u32(0))
    {
        var _S39 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(0)].viewProj_0.data_0[i32(3)][i32(3)]) );
        return _S39;
    }
    if(_S38 == u32(1))
    {
        var _S40 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(1)].viewProj_0.data_0[i32(3)][i32(3)]) );
        return _S40;
    }
    if(_S38 == u32(2))
    {
        var _S41 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(2)].viewProj_0.data_0[i32(3)][i32(3)]) );
        return _S41;
    }
    if(_S38 == u32(3))
    {
        var _S42 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(3)].viewProj_0.data_0[i32(3)][i32(3)]) );
        return _S42;
    }
    if(_S38 == u32(4))
    {
        var _S43 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(4)].viewProj_0.data_0[i32(3)][i32(3)]) );
        return _S43;
    }
    var _S44 : ShadowFace_0 = ShadowFace_0( shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].uvRect_0, mat4x4<f32>(shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(0)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(1)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(2)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(3)][i32(0)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(0)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(1)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(2)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(3)][i32(1)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(0)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(1)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(2)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(3)][i32(2)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(0)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(1)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(2)][i32(3)], shadows_0.lights_2.data_3[_S37].faces_0.data_2[i32(5)].viewProj_0.data_0[i32(3)][i32(3)]) );
    return _S44;
}

fn Shadow_Factor_0( lightIndex_0 : u32,  worldPosition_0 : vec3<f32>,  lightDir_0 : vec3<f32>,  normal_4 : vec3<f32>) -> f32
{
    var _S45 : vec4<f32> = shadows_0.lights_2.data_3[lightIndex_0].flags_0;
    if((shadows_0.lights_2.data_3[lightIndex_0].flags_0.x) < 0.5f)
    {
        return 1.0f;
    }
    var _S46 : f32 = _S45.w + shadows_0.atlasSize_0.z * (1.0f - abs(dot(normal_4, lightDir_0)));
    if((_S45.y) < 0.5f)
    {
        var _S47 : ShadowFace_std140_0 = shadows_0.lights_2.data_3[lightIndex_0].faces_0.data_2[i32(0)];
        var _S48 : vec4<f32> = (((vec4<f32>(worldPosition_0, 1.0f)) * (mat4x4<f32>(_S47.viewProj_0.data_0[i32(0)][i32(0)], _S47.viewProj_0.data_0[i32(1)][i32(0)], _S47.viewProj_0.data_0[i32(2)][i32(0)], _S47.viewProj_0.data_0[i32(3)][i32(0)], _S47.viewProj_0.data_0[i32(0)][i32(1)], _S47.viewProj_0.data_0[i32(1)][i32(1)], _S47.viewProj_0.data_0[i32(2)][i32(1)], _S47.viewProj_0.data_0[i32(3)][i32(1)], _S47.viewProj_0.data_0[i32(0)][i32(2)], _S47.viewProj_0.data_0[i32(1)][i32(2)], _S47.viewProj_0.data_0[i32(2)][i32(2)], _S47.viewProj_0.data_0[i32(3)][i32(2)], _S47.viewProj_0.data_0[i32(0)][i32(3)], _S47.viewProj_0.data_0[i32(1)][i32(3)], _S47.viewProj_0.data_0[i32(2)][i32(3)], _S47.viewProj_0.data_0[i32(3)][i32(3)]))));
        var _S49 : f32 = Shadow_SampleFace_0(&(_S47), _S48.xyz / vec3<f32>(_S48.w), _S46);
        return _S49;
    }
    var _S50 : vec3<f32> = normalize((vec3<f32>(0) - lightDir_0));
    var _S51 : f32 = _S50.x;
    var _S52 : f32 = abs(_S51);
    var _S53 : f32 = _S50.y;
    var _S54 : f32 = abs(_S53);
    var _S55 : f32 = _S50.z;
    var _S56 : f32 = abs(_S55);
    var _S57 : bool;
    if(_S52 >= _S54)
    {
        _S57 = _S52 >= _S56;
    }
    else
    {
        _S57 = false;
    }
    var index_0 : u32;
    if(_S57)
    {
        if(_S51 < 0.0f)
        {
            index_0 = u32(0);
        }
        else
        {
            index_0 = u32(1);
        }
    }
    else
    {
        if(_S54 >= _S56)
        {
            if(_S53 < 0.0f)
            {
                index_0 = u32(2);
            }
            else
            {
                index_0 = u32(3);
            }
        }
        else
        {
            if(_S55 < 0.0f)
            {
                index_0 = u32(4);
            }
            else
            {
                index_0 = u32(5);
            }
        }
    }
    var _S58 : ShadowFace_0 = Shadow_Face_0(lightIndex_0, index_0);
    var _S59 : vec4<f32> = (((vec4<f32>(worldPosition_0, 1.0f)) * (_S58.viewProj_0)));
    return Shadow_SampleFace_1(_S58, _S59.xyz / vec3<f32>(_S59.w), _S46);
}

fn Lighting_EvaluateAll_0( worldPosition_1 : vec3<f32>,  normal_5 : vec3<f32>,  viewDir_1 : vec3<f32>,  baseColor_1 : vec3<f32>,  metallic_2 : f32,  roughness_4 : f32) -> vec3<f32>
{
    var _S60 : vec3<f32> = vec3<f32>(0.0f);
    var i_0 : u32 = u32(0);
    var color_1 : vec3<f32> = _S60;
    for(;;)
    {
        if(i_0 < (lights_1.count_0))
        {
        }
        else
        {
            break;
        }
        var _S61 : LightData_std140_0 = lights_1.lights_0.data_1[i_0];
        var _S62 : vec3<f32> = Lighting_Direction_0(&(_S61), worldPosition_1);
        var _S63 : vec3<f32> = Lighting_Evaluate_0(&(_S61), normal_5, viewDir_1, worldPosition_1, baseColor_1, metallic_2, roughness_4);
        var color_2 : vec3<f32> = color_1 + _S63 * vec3<f32>(Shadow_Factor_0(i_0, worldPosition_1, _S62, normal_5));
        i_0 = i_0 + u32(1);
        color_1 = color_2;
    }
    return color_1;
}

fn EnvironmentLighting_Rotate_0( direction_0 : vec3<f32>) -> vec3<f32>
{
    var _S64 : f32 = environment_0.parameters_0.x;
    var _S65 : f32 = sin(_S64);
    var _S66 : f32 = cos(_S64);
    var _S67 : f32 = direction_0.x;
    var _S68 : f32 = direction_0.z;
    return vec3<f32>(_S66 * _S67 - _S65 * _S68, direction_0.y, _S65 * _S67 + _S66 * _S68);
}

fn EnvironmentLighting_ApplySettings_0( color_3 : vec3<f32>) -> vec3<f32>
{
    return color_3 * environment_0.tint_0.xyz * vec3<f32>(environment_0.parameters_0.y) * vec3<f32>(exp2(environment_0.parameters_0.z));
}

fn EnvironmentLighting_EvaluateIbl_0( normal_6 : vec3<f32>,  viewDirection_0 : vec3<f32>,  baseColor_2 : vec3<f32>,  metallic_3 : f32,  roughness_5 : f32,  ao_0 : f32) -> vec3<f32>
{
    var _S69 : vec3<f32> = normalize(normal_6);
    var _S70 : vec3<f32> = normalize(viewDirection_0);
    var _S71 : f32 = max(dot(_S69, _S70), 0.0f);
    var _S72 : vec3<f32> = mix(vec3<f32>(0.03999999910593033f), baseColor_2, vec3<f32>(metallic_3));
    var _S73 : vec2<f32> = (textureSampleLevel((brdfLut_0), (environmentSampler_0), (vec2<f32>(_S71, roughness_5)), (0.0f))).xy;
    return EnvironmentLighting_ApplySettings_0(((textureSampleLevel((irradianceMap_0), (environmentSampler_0), (EnvironmentLighting_Rotate_0(_S69)), (0.0f))).xyz * baseColor_2 * vec3<f32>((1.0f - metallic_3)) + (textureSampleLevel((prefilteredSpecularMap_0), (environmentSampler_0), (EnvironmentLighting_Rotate_0(reflect((vec3<f32>(0) - _S70), _S69))), (roughness_5 * environment_0.parameters_0.w))).xyz * ((_S72 + (max(vec3<f32>((1.0f - roughness_5)), _S72) - _S72) * vec3<f32>(pow(1.0f - _S71, 5.0f))) * vec3<f32>(_S73.x) + vec3<f32>(_S73.y))) * vec3<f32>(ao_0));
}

fn Color_Tonemap_0( c_0 : vec3<f32>) -> vec3<f32>
{
    return pow(c_0 / (c_0 + vec3<f32>(1.0f)), vec3<f32>(0.45454543828964233f));
}

fn ShadingModelStandard_Shade_0( m_1 : Material_0) -> vec4<f32>
{
    var _S74 : vec3<f32> = normalize(camera_0.cameraPosition_0.xyz - m_1.WorldPosition_0);
    return vec4<f32>(Color_Tonemap_0(Lighting_EvaluateAll_0(m_1.WorldPosition_0, m_1.Normal_0, _S74, m_1.BaseColor_0, m_1.Metallic_0, m_1.Roughness_0) * vec3<f32>(m_1.Occlusion_0) + EnvironmentLighting_EvaluateIbl_0(m_1.Normal_0, _S74, m_1.BaseColor_0, m_1.Metallic_0, m_1.Roughness_0, m_1.Occlusion_0) + m_1.Emissive_0), m_1.Opacity_0);
}

struct pixelOutput_0
{
    @location(0) output_1 : vec4<f32>,
};

struct pixelInput_0
{
    @location(0) world_position_1 : vec3<f32>,
    @location(1) normal_7 : vec3<f32>,
    @location(2) tangent_3 : vec4<f32>,
    @location(3) uv_3 : vec2<f32>,
};

@fragment
fn fs_main( _S75 : pixelInput_0, @builtin(position) clip_position_1 : vec4<f32>) -> pixelOutput_0
{
    var _S76 : VertexOutput_0 = VertexOutput_0( clip_position_1, _S75.world_position_1, _S75.normal_7, _S75.tangent_3, _S75.uv_3 );
    var m_2 : Material_0 = Material_Init_0(_S76);
    var _S77 : vec4<f32> = (textureSample((albedoTexture_0), (materialSampler_0), (_S75.uv_3)));
    m_2.BaseColor_0 = _S77.xyz * material_0.color_0.xyz;
    m_2.Normal_0 = Material_NormalFromMap_0(_S76, (textureSample((normalTexture_0), (materialSampler_0), (_S75.uv_3))).xyz);
    var _S78 : vec4<f32> = (textureSample((metallicRoughnessTexture_0), (materialSampler_0), (_S75.uv_3)));
    m_2.Metallic_0 = material_0.metallic_0 * _S78.z;
    m_2.Roughness_0 = material_0.roughness_0 * _S78.y;
    m_2.Occlusion_0 = (textureSample((occlusionTexture_0), (materialSampler_0), (_S75.uv_3))).x * material_0.occlusion_0;
    m_2.Emissive_0 = (textureSample((emissiveTexture_0), (materialSampler_0), (_S75.uv_3))).xyz * vec3<f32>(material_0.emissive_0);
    m_2.Opacity_0 = material_0.color_0.w * _S77.w;
    var _S79 : pixelOutput_0 = pixelOutput_0( ShadingModelStandard_Shade_0(m_2) );
    return _S79;
}

