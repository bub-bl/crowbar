#language slang 2026;
import ImportanceSampling;

[[vk::binding(0, 0)]] RWTexture2D<float4> outputTexture;

float GeometrySchlick(float cosine, float roughness)
{
    let k = roughness * roughness / 2.0;
    return cosine / max(cosine * (1.0 - k) + k, 0.0001);
}

[shader("compute")]
[numthreads(8, 8, 1)]
void cs_main(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= 512 || id.y >= 512) return;
    let ndotv = (float(id.x) + 0.5) / 512.0;
    let roughness = (float(id.y) + 0.5) / 512.0;
    let view = float3(sqrt(max(0.0, 1.0 - ndotv * ndotv)), 0.0, ndotv);
    var a = 0.0;
    var b = 0.0;
    for (uint sampleIndex = 0; sampleIndex < 256; sampleIndex++)
    {
        let halfway = ImportanceSampling::Ggx(ImportanceSampling::Hammersley(sampleIndex, 256), float3(0.0, 0.0, 1.0), roughness);
        let light = normalize(2.0 * dot(view, halfway) * halfway - view);
        let ndotl = max(light.z, 0.0);
        let ndoth = max(halfway.z, 0.0);
        let vdoth = max(dot(view, halfway), 0.0);
        if (ndotl > 0.0)
        {
            let geometry = GeometrySchlick(ndotv, roughness) * GeometrySchlick(ndotl, roughness);
            let visibility = geometry * vdoth / max(ndoth * ndotv, 0.0001);
            let fresnel = pow(1.0 - vdoth, 5.0);
            a += (1.0 - fresnel) * visibility;
            b += fresnel * visibility;
        }
    }
    outputTexture[id.xy] = float4(a / 256.0, b / 256.0, 0.0, 1.0);
}
