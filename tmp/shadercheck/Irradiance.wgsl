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

@compute
@workgroup_size(8, 8, 1)
fn cs_main(@builtin(global_invocation_id) id_0 : vec3<u32>)
{
    var _S6 : bool;
    if((id_0.x) >= (parameters_0.size_0))
    {
        _S6 = true;
    }
    else
    {
        _S6 = (id_0.y) >= (parameters_0.size_0);
    }
    if(_S6)
    {
        return;
    }
    var _S7 : vec2<u32> = id_0.xy;
    var _S8 : vec3<f32> = Cubemap_Direction_0(parameters_0.face_0, (vec2<f32>(_S7) + vec2<f32>(0.5f)) / vec2<f32>(f32(parameters_0.size_0)));
    var sum_0 : vec3<f32>;
    if((abs(_S8.z)) < 0.99900001287460327f)
    {
        sum_0 = vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    else
    {
        sum_0 = vec3<f32>(1.0f, 0.0f, 0.0f);
    }
    var _S9 : vec3<f32> = normalize(cross(sum_0, _S8));
    var _S10 : vec3<f32> = cross(_S8, _S9);
    var _S11 : vec3<f32> = vec3<f32>(0.0f);
    var sampleIndex_0 : u32 = u32(0);
    sum_0 = _S11;
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
        var _S12 : vec2<f32> = ImportanceSampling_Hammersley_0(sampleIndex_0, parameters_0.sampleCount_0);
        var _S13 : f32 = 6.28318548202514648f * _S12.x;
        var _S14 : f32 = _S12.y;
        var _S15 : f32 = sqrt(1.0f - _S14);
        var _S16 : f32 = sqrt(_S14);
        var _S17 : vec3<f32> = vec3<f32>(_S15);
        var sum_1 : vec3<f32> = sum_0 + (textureSampleLevel((sourceTexture_0), (sourceSampler_0), (_S9 * vec3<f32>((cos(_S13) * _S16)) + _S10 * vec3<f32>((sin(_S13) * _S16)) + _S8 * _S17), (0.0f))).xyz * _S17;
        var weight_1 : f32 = weight_0 + _S15;
        sampleIndex_0 = sampleIndex_0 + u32(1);
        sum_0 = sum_1;
        weight_0 = weight_1;
    }
    textureStore((outputTexture_0), (_S7), (vec4<f32>(vec3<f32>(3.14159274101257324f) * sum_0 / vec3<f32>(max(weight_0, 0.00009999999747379f)), 1.0f)));
    return;
}

