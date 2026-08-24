#language slang 2026;
// Analytic single-scattering atmosphere (Nishita-style ray marching).
//
// The sky is evaluated per pixel by marching the view ray through the
// atmosphere, accumulating Rayleigh and Mie in-scattering. Each sample
// contributes its phase-function-weighted scattering attenuated by the
// transmittance along the camera ray and along the sun ray. A diffuse ground
// bounce term approximates sunlight reflected upward by the ground, and the
// sun disc is added with the full atmosphere transmittance when the view ray
// points within its angular radius. Everything is computed analytically on
// the GPU — no precomputed LUTs, no CPU work.
//
// Radiance units are display-space: the coefficients are tuned so a clear
// sky lands in a pleasant range for the engine's un-tonemapped output, with
// the sun disc naturally blown out to white.
module Atmosphere;

public struct AtmosphereParams
{
    float3 sunDirection;     // normalized, in the same space as the sampled direction
    float sunAngularRadius;  // radians
    float turbidity;         // multiplies Mie (haze); 1 = clear air
    float groundAlbedo;      // 0..1 diffuse ground reflectance
    float sunIntensity;      // multiplier on the sun's radiance
};

public struct Atmosphere
{
    static const float PlanetRadius = 6360.0e3;
    static const float AtmosphereHeight = 60.0e3;
    static const float RayleighScaleHeight = 8.0e3;
    static const float MieScaleHeight = 1.2e3;

    // Sea-level scattering coefficients (per meter). Rayleigh is wavelength
    // dependent (blue scatters most), Mie is wavelength neutral.
    static const float3 RayleighScattering = float3(5.802e-6, 13.558e-6, 33.1e-6);
    static const float MieScattering = 3.996e-6;
    static const float MieAsymmetry = 0.8;

    static const float3 SunColor = float3(1.0, 0.72, 0.42);
    static const float3 Up = float3(0.0, 1.0, 0.0);

    // Display-space sun radiance: chosen so clear-sky radiance sits around
    // 0.15-0.5 before the sky pass's tonemap, and the sun disc is a bright
    // (not blown out) white spot after it.
    static const float SunRadiance = 40.0;

    struct SphereHit
    {
        float near;
        float far;
    };

    static SphereHit IntersectSphere(float3 origin, float3 direction, float radius)
    {
        SphereHit hit;
        let b = dot(origin, direction);
        let c = dot(origin, origin) - radius * radius;
        let disc = b * b - c;
        if (disc < 0.0)
        {
            hit.near = 1.0;
            hit.far = -1.0;
        }
        else
        {
            let s = sqrt(disc);
            hit.near = -b - s;
            hit.far = -b + s;
        }
        return hit;
    }

    static float RayleighPhase(float cosine)
    {
        return 3.0 / (16.0 * 3.14159265359) * (1.0 + cosine * cosine);
    }

    static float MiePhase(float cosine)
    {
        let g = MieAsymmetry;
        let g2 = g * g;
        return 3.0 / (8.0 * 3.14159265359) * (1.0 - g2) / (2.0 + g2)
            * (1.0 + cosine * cosine) / pow(1.0 + g2 - 2.0 * g * cosine, 1.5);
    }

    // Optical depth (Rayleigh + Mie) from a point toward the sun, integrated
    // along the sun ray until it exits the atmosphere.
    static float3 SunOpticalDepth(float3 position, float3 sunDirection)
    {
        let hit = IntersectSphere(position, sunDirection, PlanetRadius + AtmosphereHeight);
        let far = max(hit.far, 0.0); // the point is inside the atmosphere, so far > 0
        const uint steps = 6;
        let dt = far / float(steps);
        var depth = float3(0.0);
        for (uint i = 0; i < steps; i++)
        {
            let point = position + sunDirection * (float(i) + 0.5) * dt;
            let height = max(length(point) - PlanetRadius, 0.0);
            depth += (RayleighScattering * exp(-height / RayleighScaleHeight)
                    + MieScattering * exp(-height / MieScaleHeight)) * dt;
        }
        return depth;
    }

    // Sky radiance along a view direction, for a camera at the given altitude
    // (meters above the ground; clamped to the ground plane).
    static float3 EvaluateSky(float3 viewDirection, AtmosphereParams p, float cameraHeight)
    {
        let cameraPosition = float3(0.0, PlanetRadius + max(cameraHeight, 0.0), 0.0);

        let atmosphere = IntersectSphere(cameraPosition, viewDirection, PlanetRadius + AtmosphereHeight);
        let ground = IntersectSphere(cameraPosition, viewDirection, PlanetRadius);

        let near = max(atmosphere.near, 0.0);
        let hitsGround = ground.far > 0.0;
        let far = hitsGround ? min(atmosphere.far, ground.far) : atmosphere.far;
        if (far <= near)
            return float3(0.0);

        let mieScattering = MieScattering * max(p.turbidity, 0.1);
        let sunDirection = normalize(p.sunDirection);
        let sunRadiance = p.sunIntensity * SunRadiance * SunColor;

        // Diffuse ground reflection: sunlight reaching the ground is reflected
        // upward and scattered toward the camera. The ground irradiance uses
        // the sun transmittance at the ground point below the camera.
        let groundPosition = float3(cameraPosition.x, PlanetRadius, cameraPosition.z);
        let sunTransmittanceAtGround = exp(-SunOpticalDepth(groundPosition, sunDirection));
        let groundIrradiance = sunRadiance * sunTransmittanceAtGround
            * max(dot(Up, sunDirection), 0.0) * p.groundAlbedo / 3.14159265359;

        let sunPhaseAngle = dot(viewDirection, sunDirection);
        let groundPhaseAngle = dot(viewDirection, Up);
        let sunCosine = cos(p.sunAngularRadius);

        const uint steps = 24;
        let dt = (far - near) / float(steps);

        var opticalDepth = float3(0.0); // camera to current sample
        var radiance = float3(0.0);

        for (uint i = 0; i < steps; i++)
        {
            let t = near + (float(i) + 0.5) * dt;
            let sample = cameraPosition + viewDirection * t;
            let height = max(length(sample) - PlanetRadius, 0.0);
            let rayleighDensity = exp(-height / RayleighScaleHeight);
            let mieDensity = exp(-height / MieScaleHeight);

            opticalDepth += (RayleighScattering * rayleighDensity + mieScattering * mieDensity) * dt;
            // Note: `-(a + b)` is emitted as `-a + b` by this slangc version, so
            // the sum is negated term by term.
            let transmittance = exp(-opticalDepth - SunOpticalDepth(sample, sunDirection));

            let scattering = RayleighScattering * rayleighDensity * RayleighPhase(sunPhaseAngle)
                           + mieScattering * mieDensity * MiePhase(sunPhaseAngle);
            radiance += scattering * transmittance * sunRadiance * dt;

            // Ground bounce (approximate): ground-reflected sunlight scattered
            // toward the camera, attenuated by the analytic vertical optical
            // depth between the ground and the sample.
            let groundTransmittance = exp(
                -RayleighScattering * RayleighScaleHeight * (1.0 - rayleighDensity)
                - mieScattering * MieScaleHeight * (1.0 - mieDensity));
            let groundPhase = (RayleighPhase(groundPhaseAngle) + MiePhase(groundPhaseAngle)) * 0.5;
            radiance += (RayleighScattering * rayleighDensity + mieScattering * mieDensity)
                      * groundPhase * groundIrradiance * groundTransmittance * dt;
        }

        // Sun disc: only when the view ray reaches the atmosphere boundary
        // (the sun is above the horizon) and points within the sun's angular
        // radius.
        if (!hitsGround && sunPhaseAngle > sunCosine)
        {
            let exit = cameraPosition + viewDirection * far;
            radiance += sunRadiance * exp(-opticalDepth - SunOpticalDepth(exit, sunDirection));
        }

        return radiance;
    }
};
