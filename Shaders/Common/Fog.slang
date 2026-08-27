#language slang 2026;
// Shared volumetric-fog helpers, mirroring s&box / Source 2's dynamic
// volumetric fog. The fog volume is a coarse stack of 2D slices through the
// camera frustum (a froxel grid stored as a 2D texture array); these helpers
// are the phase function and the slice <-> view-depth mapping shared by the
// accumulate / integrate / apply passes.
module Fog;

public struct Fog
{
    // Maps a distance along the camera's forward axis to a logarithmic slice layer index.
    // Froxel depth is logarithmic. Linear slices over a 3000-unit draw range
    // place the first sample dozens of units from the camera and completely
    // miss ordinary local fog volumes.
    static float SliceCoordinate(float depth, float nearPlane, float farPlane, int sliceCount)
    {
        let n = max(nearPlane, 0.0001);
        let f = max(farPlane, n + 0.0001);
        let logarithmicDepth = log(max(depth, n) / n) / log(f / n);
        return clamp(logarithmicDepth, 0.0, 1.0) * float(max(sliceCount - 1, 1));
    }

    static float SliceBoundary(int boundary, float nearPlane, float farPlane, int sliceCount)
    {
        let n = max(nearPlane, 0.0001);
        let f = max(farPlane, n + 0.0001);
        let t = float(boundary) / float(max(sliceCount, 1));
        return n * pow(f / n, t);
    }

    // Maps a slice index back to a view-space depth (center of the logarithmic slice).
    static float SliceDepth(int slice, float nearPlane, float farPlane, int sliceCount)
    {
        return sqrt(SliceBoundary(slice, nearPlane, farPlane, sliceCount)
                  * SliceBoundary(slice + 1, nearPlane, farPlane, sliceCount));
    }

    static float SliceThickness(int slice, float nearPlane, float farPlane, int sliceCount)
    {
        return SliceBoundary(slice + 1, nearPlane, farPlane, sliceCount)
             - SliceBoundary(slice, nearPlane, farPlane, sliceCount);
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