struct FilterUniforms_std140_0
{
    @align(16) face_0 : u32,
    @align(4) size_0 : u32,
    @align(8) roughness_0 : f32,
    @align(4) sampleCount_0 : u32,
};

@binding(3) @group(0) var<uniform> parameters_0 : FilterUniforms_std140_0;
@binding(0) @group(0) var sourceTexture_0 : texture_cube<f32>;

@binding(1) @group(0) var sourceSampler_0 : sampler;

@binding(2) @group(0) var outputTexture_0 : texture_storage_2d<rgba32float, read_write>;

fn Cubemap_Direction_0( face_1 : u32,  uv_0 : vec2<f32>) -> vec3<f32>
{
    var _S1 : vec2<f32> = uv_0 * vec2<f32>(2.0f) - vec2<f32>(1.0f);
    switch(face_1)
    {
    case u32(0):
        {
            return normalize(vec3<f32>(1.0f, - _S1.y, - _S1.x));
        }
    case u32(1):
        {
            return normalize(vec3<f32>(-1.0f, - _S1.y, _S1.x));
        }
    case u32(2):
        {
            return normalize(vec3<f32>(_S1.x, 1.0f, _S1.y));
        }
    case u32(3):
        {
            return normalize(vec3<f32>(_S1.x, -1.0f, - _S1.y));
        }
    case u32(4):
        {
            return normalize(vec3<f32>(_S1.x, - _S1.y, 1.0f));
        }
    default :
        {
            return normalize(vec3<f32>(- _S1.x, - _S1.y, -1.0f));
        }
    }
}

fn ImportanceSampling_RadicalInverse_0( bits_0 : u32) -> f32
{
    var _S2 : u32 = (((bits_0 << (u32(16)))) | (((bits_0 >> (u32(16))))));
    var _S3 : u32 = (((((_S2 & (u32(1431655765)))) << (u32(1)))) | (((((_S2 & (u32(2863311530)))) >> (u32(1))))));
    var _S4 : u32 = (((((_S3 & (u32(858993459)))) << (u32(2)))) | (((((_S3 & (u32(3435973836)))) >> (u32(2))))));
    var _S5 : u32 = (((((_S4 & (u32(252645135)))) << (u32(4)))) | (((((_S4 & (u32(4042322160)))) >> (u32(4))))));
    return f32((((((_S5 & (u32(16711935)))) << (u32(8)))) | (((((_S5 & (u32(4278255360)))) >> (u32(8))))))) * 2.32830643653869629e-10f;
}

fn ImportanceSampling_Hammersley_0( index_0 : u32,  count_0 : u32) -> vec2<f32>
{
    return vec2<f32>(f32(index_0) / f32(count_0), ImportanceSampling_RadicalInverse_0(index_0));
}

fn ImportanceSampling_Ggx_0( xi_0 : vec2<f32>,  normal_0 : vec3<f32>,  roughness_1 : f32) -> vec3<f32>
{
    var _S6 : f32 = roughness_1 * roughness_1;
    var _S7 : f32 = 6.28318548202514648f * xi_0.x;
    var _S8 : f32 = xi_0.y;
    var _S9 : f32 = sqrt((1.0f - _S8) / (1.0f + (_S6 * _S6 - 1.0f) * _S8));
    var _S10 : f32 = sqrt(max(0.0f, 1.0f - _S9 * _S9));
    var _S11 : f32 = cos(_S7) * _S10;
    var _S12 : f32 = sin(_S7) * _S10;
    var _S13 : vec3<f32>;
    if((abs(normal_0.z)) < 0.99900001287460327f)
    {
        _S13 = vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    else
    {
        _S13 = vec3<f32>(1.0f, 0.0f, 0.0f);
    }
    var _S14 : vec3<f32> = normalize(cross(_S13, normal_0));
    return normalize(_S14 * vec3<f32>(_S11) + cross(normal_0, _S14) * vec3<f32>(_S12) + normal_0 * vec3<f32>(_S9));
}

@compute
@workgroup_size(8, 8, 1)
fn cs_main(@builtin(global_invocation_id) id_0 : vec3<u32>)
{
    var _S15 : bool;
    if((id_0.x) >= (parameters_0.size_0))
    {
        _S15 = true;
    }
    else
    {
        _S15 = (id_0.y) >= (parameters_0.size_0);
    }
    if(_S15)
    {
        return;
    }
    var _S16 : vec2<u32> = id_0.xy;
    var _S17 : vec3<f32> = Cubemap_Direction_0(parameters_0.face_0, (vec2<f32>(_S16) + vec2<f32>(0.5f)) / vec2<f32>(f32(parameters_0.size_0)));
    var _S18 : vec3<f32> = vec3<f32>(0.0f);
    var sampleIndex_0 : u32 = u32(0);
    var sum_0 : vec3<f32> = _S18;
    var weight_0 : f32 = 0.0f;
    for(;;)
    {
        if(sampleIndex_0 < (parameters_0.sampleCount_0))
        {
        }
        else
        {
            break;
        }
        var _S19 : vec3<f32> = ImportanceSampling_Ggx_0(ImportanceSampling_Hammersley_0(sampleIndex_0, parameters_0.sampleCount_0), _S17, parameters_0.roughness_0);
        var _S20 : vec3<f32> = normalize(vec3<f32>((2.0f * dot(_S17, _S19))) * _S19 - _S17);
        var _S21 : f32 = max(dot(_S17, _S20), 0.0f);
        if(_S21 > 0.0f)
        {
            var weight_1 : f32 = weight_0 + _S21;
            sum_0 = sum_0 + (textureSampleLevel((sourceTexture_0), (sourceSampler_0), (_S20), (0.0f))).xyz * vec3<f32>(_S21);
            weight_0 = weight_1;
        }
        sampleIndex_0 = sampleIndex_0 + u32(1);
    }
    textureStore((outputTexture_0), (_S16), (vec4<f32>(sum_0 / vec3<f32>(max(weight_0, 0.00009999999747379f)), 1.0f)));
    return;
}

