@binding(0) @group(0) var outputTexture_0 : texture_storage_2d<rgba32float, read_write>;

fn ImportanceSampling_RadicalInverse_0( bits_0 : u32) -> f32
{
    var _S1 : u32 = (((bits_0 << (u32(16)))) | (((bits_0 >> (u32(16))))));
    var _S2 : u32 = (((((_S1 & (u32(1431655765)))) << (u32(1)))) | (((((_S1 & (u32(2863311530)))) >> (u32(1))))));
    var _S3 : u32 = (((((_S2 & (u32(858993459)))) << (u32(2)))) | (((((_S2 & (u32(3435973836)))) >> (u32(2))))));
    var _S4 : u32 = (((((_S3 & (u32(252645135)))) << (u32(4)))) | (((((_S3 & (u32(4042322160)))) >> (u32(4))))));
    return f32((((((_S4 & (u32(16711935)))) << (u32(8)))) | (((((_S4 & (u32(4278255360)))) >> (u32(8))))))) * 2.32830643653869629e-10f;
}

fn ImportanceSampling_Hammersley_0( index_0 : u32,  count_0 : u32) -> vec2<f32>
{
    return vec2<f32>(f32(index_0) / f32(count_0), ImportanceSampling_RadicalInverse_0(index_0));
}

fn ImportanceSampling_Ggx_0( xi_0 : vec2<f32>,  normal_0 : vec3<f32>,  roughness_0 : f32) -> vec3<f32>
{
    var _S5 : f32 = roughness_0 * roughness_0;
    var _S6 : f32 = 6.28318548202514648f * xi_0.x;
    var _S7 : f32 = xi_0.y;
    var _S8 : f32 = sqrt((1.0f - _S7) / (1.0f + (_S5 * _S5 - 1.0f) * _S7));
    var _S9 : f32 = sqrt(max(0.0f, 1.0f - _S8 * _S8));
    var _S10 : f32 = cos(_S6) * _S9;
    var _S11 : f32 = sin(_S6) * _S9;
    var _S12 : vec3<f32>;
    if((abs(normal_0.z)) < 0.99900001287460327f)
    {
        _S12 = vec3<f32>(0.0f, 0.0f, 1.0f);
    }
    else
    {
        _S12 = vec3<f32>(1.0f, 0.0f, 0.0f);
    }
    var _S13 : vec3<f32> = normalize(cross(_S12, normal_0));
    return normalize(_S13 * vec3<f32>(_S10) + cross(normal_0, _S13) * vec3<f32>(_S11) + normal_0 * vec3<f32>(_S8));
}

fn GeometrySchlick_0( cosine_0 : f32,  roughness_1 : f32) -> f32
{
    var _S14 : f32 = roughness_1 * roughness_1 / 2.0f;
    return cosine_0 / max(cosine_0 * (1.0f - _S14) + _S14, 0.00009999999747379f);
}

@compute
@workgroup_size(8, 8, 1)
fn cs_main(@builtin(global_invocation_id) id_0 : vec3<u32>)
{
    var _S15 : u32 = id_0.x;
    var _S16 : bool;
    if(_S15 >= u32(512))
    {
        _S16 = true;
    }
    else
    {
        _S16 = (id_0.y) >= u32(512);
    }
    if(_S16)
    {
        return;
    }
    var _S17 : f32 = (f32(_S15) + 0.5f) / 512.0f;
    var _S18 : f32 = (f32(id_0.y) + 0.5f) / 512.0f;
    var _S19 : vec3<f32> = vec3<f32>(sqrt(max(0.0f, 1.0f - _S17 * _S17)), 0.0f, _S17);
    var sampleIndex_0 : u32 = u32(0);
    var a_0 : f32 = 0.0f;
    var b_0 : f32 = 0.0f;
    for(;;)
    {
        if(sampleIndex_0 < u32(256))
        {
        }
        else
        {
            break;
        }
        var _S20 : vec3<f32> = ImportanceSampling_Ggx_0(ImportanceSampling_Hammersley_0(sampleIndex_0, u32(256)), vec3<f32>(0.0f, 0.0f, 1.0f), _S18);
        var _S21 : f32 = dot(_S19, _S20);
        var _S22 : f32 = max(normalize(vec3<f32>((2.0f * _S21)) * _S20 - _S19).z, 0.0f);
        var _S23 : f32 = max(_S20.z, 0.0f);
        var _S24 : f32 = max(_S21, 0.0f);
        if(_S22 > 0.0f)
        {
            var _S25 : f32 = GeometrySchlick_0(_S17, _S18) * GeometrySchlick_0(_S22, _S18) * _S24 / max(_S23 * _S17, 0.00009999999747379f);
            var _S26 : f32 = pow(1.0f - _S24, 5.0f);
            var b_1 : f32 = b_0 + _S26 * _S25;
            a_0 = a_0 + (1.0f - _S26) * _S25;
            b_0 = b_1;
        }
        sampleIndex_0 = sampleIndex_0 + u32(1);
    }
    textureStore((outputTexture_0), (id_0.xy), (vec4<f32>(a_0 / 256.0f, b_0 / 256.0f, 0.0f, 1.0f)));
    return;
}

