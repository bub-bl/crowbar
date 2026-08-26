#language slang 2026;
// Shared volumetric-fog helpers, mirroring s&box / Source 2's dynamic
// volumetric fog. The fog volume is a coarse stack of 2D slices through the
// camera frustum (a froxel grid stored as a 2D texture array); these helpers
// are the phase function and the slice <-> view-depth mapping shared by the
// accumulate / integrate / apply passes.
module Fog;

public struct Fog
{
    // Maps a distance along the camera's forward axis to a slice layer index.
    static float SliceCoordinate(float depth, float nearPlane, float farPlane, int sliceCount)
    {
        let f = clamp((depth - nearPlane) / max(farPlane - nearPlane, 0.0001), 0.0, 1.0);
        return f * float(sliceCount - 1);
    }

    // Maps a slice index back to a view-space depth (center of the slice).
    static float SliceDepth(int slice, float nearPlane, float farPlane, int sliceCount)
    {
        let f = (float(slice) + 0.5) / float(max(sliceCount, 1));
        return lerp(nearPlane, farPlane, f);
    }

    // Henyey-Greenstein single-scattering phase function. cosTheta is the cosine
    // of the angle between the incident (light-to-point) direction and the
    // scattered (point-to-eye) direction. g = anisotropy: 0 isotropic, +1 forward.
    static float PhaseHenveyGreenstein(float cosTheta, float g)
    {
        let g2 = g * g;
        return (1.0 - g2) / (4.0 * 3.14159265 * pow(1.0 + g2 - 2.0 * g * cosTheta, 1.5));
    }
}