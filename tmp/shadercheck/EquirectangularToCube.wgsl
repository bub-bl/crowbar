struct ConvertUniforms_std140_0
{
    @align(16) face_0 : u32,
    @align(4) size_0 : u32,
    @align(8) padding_0 : vec2<u32>,
};

@binding(3) @group(0) var<uniform> parameters_0 : ConvertUniforms_std140_0;
@binding(0) @group(0) var sourceTexture_0 : texture_2d<f32>;

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

@compute
@workgroup_size(8, 8, 1)
fn cs_main(@builtin(global_invocation_id) id_0 : vec3<u32>)
{
    var _S2 : bool;
    if((id_0.x) >= (parameters_0.size_0))
    {
        _S2 = true;
    }
    else
    {
        _S2 = (id_0.y) >= (parameters_0.size_0);
    }
    if(_S2)
    {
        return;
    }
    var _S3 : vec2<u32> = id_0.xy;
    var _S4 : vec3<f32> = Cubemap_Direction_0(parameters_0.face_0, (vec2<f32>(_S3) + vec2<f32>(0.5f)) / vec2<f32>(f32(parameters_0.size_0)));
    textureStore((outputTexture_0), (_S3), ((textureSampleLevel((sourceTexture_0), (sourceSampler_0), (vec2<f32>(atan2(_S4.z, _S4.x) / 6.28318548202514648f + 0.5f, asin(clamp(_S4.y, -1.0f, 1.0f)) / 3.14159274101257324f + 0.5f)), (0.0f)))));
    return;
}

