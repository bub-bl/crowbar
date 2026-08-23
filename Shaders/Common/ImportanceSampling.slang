#language slang 2026;
module ImportanceSampling;

public struct ImportanceSampling
{
    static float RadicalInverse(uint bits)
    {
        bits = (bits << 16u) | (bits >> 16u);
        bits = ((bits & 0x55555555u) << 1u) | ((bits & 0xAAAAAAAAu) >> 1u);
        bits = ((bits & 0x33333333u) << 2u) | ((bits & 0xCCCCCCCCu) >> 2u);
        bits = ((bits & 0x0F0F0F0Fu) << 4u) | ((bits & 0xF0F0F0F0u) >> 4u);
        bits = ((bits & 0x00FF00FFu) << 8u) | ((bits & 0xFF00FF00u) >> 8u);
        return float(bits) * 2.3283064365386963e-10;
    }

    static float2 Hammersley(uint index, uint count)
    {
        return float2(float(index) / float(count), RadicalInverse(index));
    }

    static float3 Ggx(float2 xi, float3 normal, float roughness)
    {
        let a = roughness * roughness;
        let phi = 6.28318530718 * xi.x;
        let cosTheta = sqrt((1.0 - xi.y) / (1.0 + (a * a - 1.0) * xi.y));
        let sinTheta = sqrt(max(0.0, 1.0 - cosTheta * cosTheta));
        let halfway = float3(cos(phi) * sinTheta, sin(phi) * sinTheta, cosTheta);
        let up = abs(normal.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
        let tangent = normalize(cross(up, normal));
        let bitangent = cross(normal, tangent);
        return normalize(tangent * halfway.x + bitangent * halfway.y + normal * halfway.z);
    }
};
