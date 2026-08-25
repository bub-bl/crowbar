#language slang 2026;
// FXAA 3.11 consumer spatial anti-aliasing.
//
// Faithful port of NVIDIA's FXAA_DefaultES.frag as distributed with three.js
// (Archilogic/Agost Biro's WebGL port of the FXAA 3.11 "quality - PC" shader,
// from NVIDIA's "Fast Approximate Anti-Aliasing" white paper by Timothy
// Lottes). It is an edge-blending algorithm: it taps the four mid-edge
// neighbors (N/S/E/W) and, if no neighbor differs enough from the center,
// returns the pixel unmodified (the cheap, common path). Otherwise it ranks
// vertical vs horizontal contrast to pick the edge span, walks across it until
// the local contrast flips, and mixes the center toward the higher-contrast
// neighbor (up to ~50%) to feather the stair-step. A 45-degree fallback blurs
// diagonal edges with a 3x3 average.
//
// Purely spatial: no history buffer, no temporal accumulation, so there is no
// ghosting on camera motion. Applied last in the post-process chain, on the
// display-referred value (the chain stays linear Rgba16Float until the
// Ui/BlitScene.slang display encode). Convention: scene texture slot 0,
// sampler slot 1, one uniform buffer in group 0.

struct FxaaUniforms
{
    float edgeThreshold;   // early-exit contrast gate (0.166 default)
    float subpixel;        // mix strength toward the edge neighbor (0 = off, 1 = full)
    float quality;         // number of search samples along the edge (>=2)
    float _pad0;
    float2 resolution;     // (1/width, 1/height) texel step
    float2 _pad1;
};

[[vk::binding(0, 0)]] Texture2D sceneTexture;
[[vk::binding(1, 0)]] SamplerState sceneSampler;
[[vk::binding(2, 0)]] ConstantBuffer<FxaaUniforms> postProcess;

struct VertexOutput
{
    float4 position : SV_Position;
    [[vk::location(0)]] float2 uv : TEXCOORD0;
};

[shader("vertex")]
VertexOutput vs_main(uint vertexId : SV_VertexID)
{
    let clip = float2(float((vertexId << 1u) & 2u), float(vertexId & 2u)) * 2.0 - 1.0;
    VertexOutput output;
    output.position = float4(clip, 0.0, 1.0);
    output.uv = float2(clip.x * 0.5 + 0.5, 0.5 - clip.y * 0.5);
    return output;
}

struct Fxaa
{
    static float4 Sample(float2 uv)
    {
        return sceneTexture.Sample(sceneSampler, uv);
    }

    // Largest per-channel (plus alpha) absolute difference between two colors:
    // the contrast metric FXAA ranks neighbors by.
    static float Contrast(float4 a, float4 b)
    {
        let d = abs(a - b);
        return max(d.r, max(d.g, max(d.b, d.a)));
    }
};

[shader("fragment")]
float4 fs_main(VertexOutput input) : SV_Target
{
    let texel = postProcess.resolution;
    let invEdgeThreshold = 1.0 / max(postProcess.edgeThreshold, 1e-4);
    let numSamples = max(2.0, round(postProcess.quality));
    let maxSearch = numSamples - 1.0;

    // The center and its four mid-edge neighbors (one full texel away).
    let rgbaM = Fxaa::Sample(input.uv);
    let rgbaS = Fxaa::Sample(input.uv + float2(0.0, texel.y));
    let rgbaE = Fxaa::Sample(input.uv + float2(texel.x, 0.0));
    let rgbaN = Fxaa::Sample(input.uv + float2(0.0, -texel.y));
    let rgbaW = Fxaa::Sample(input.uv + float2(-texel.x, 0.0));

    // Early exit: no neighbor contrasts enough with the center, so this pixel
    // lies in a smooth region — the common, cheapest path.
    let maxContrast = max(Fxaa::Contrast(rgbaM, rgbaN), max(Fxaa::Contrast(rgbaM, rgbaS),
        max(Fxaa::Contrast(rgbaM, rgbaE), Fxaa::Contrast(rgbaM, rgbaW))));
    if (maxContrast < postProcess.edgeThreshold)
        return float4(rgbaM.rgb, 1.0);

    let contrastN = Fxaa::Contrast(rgbaM, rgbaN);
    let contrastS = Fxaa::Contrast(rgbaM, rgbaS);
    let contrastE = Fxaa::Contrast(rgbaM, rgbaE);
    let contrastW = Fxaa::Contrast(rgbaM, rgbaW);

    // Vertical vs horizontal contrast. A positive relativeVContrast means the
    // north+south contrast dominates, i.e. the edge runs horizontally.
    var relativeVContrast = ((contrastN + contrastS) - (contrastE + contrastW)) * invEdgeThreshold;
    var horzSpan = relativeVContrast > 0.0;

    // 45-degree edge detection: when V/H contrast is nearly equal the edge is
    // diagonal; tap perpendicular to the implied direction to pick the span.
    if (abs(relativeVContrast) < 0.3)
    {
        let dirToEdgeX = contrastE > contrastW ? 1.0 : -1.0;
        let dirToEdgeY = contrastS > contrastN ? 1.0 : -1.0;

        let matchAlongH = Fxaa::Contrast(rgbaM, Fxaa::Sample(input.uv + float2(dirToEdgeX, -dirToEdgeY) * texel));
        let matchAlongV = Fxaa::Contrast(rgbaM, Fxaa::Sample(input.uv + float2(-dirToEdgeX, dirToEdgeY) * texel));

        let rel45 = (matchAlongV - matchAlongH) * invEdgeThreshold;
        if (abs(rel45) < 0.3)
        {
            // A genuine 45-degree edge: fall back to a 3x3 average blur.
            let colorAvg = (rgbaN + rgbaS + rgbaE + rgbaW) * 0.25;
            return float4(lerp(rgbaM.rgb, colorAvg.rgb, 0.4), 1.0);
        }
        horzSpan = rel45 > 0.0;
    }

    // Normalize the pair so highN is the higher-contrast neighbor, and choose
    // the walk axis (perpendicular to the edge span).
    var highN = horzSpan ? rgbaN : rgbaW;
    var highS = horzSpan ? rgbaS : rgbaE;
    if (Fxaa::Contrast(rgbaM, highN) <= Fxaa::Contrast(rgbaM, highS))
        highN = highS;

    let offNp = horzSpan ? float2(texel.x, 0.0) : float2(0.0, texel.y);

    // March both directions from the pixel until the local contrast flips
    // (leaving the edge interior), tracking how far each side travelled.
    var doneN = false;
    var doneP = false;
    var iterationsN = 0.0;
    var iterationsP = 0.0;

    for (var i = 0.0; i < numSamples; i += 1.0)
    {
        let increment = i + 1.0;
        if (!doneN)
        {
            let rgbaEndN = Fxaa::Sample(input.uv + offNp * increment);
            doneN = Fxaa::Contrast(rgbaEndN, rgbaM) > Fxaa::Contrast(rgbaEndN, highN);
            iterationsN = i;
        }
        if (!doneP)
        {
            let rgbaEndP = Fxaa::Sample(input.uv - offNp * increment);
            doneP = Fxaa::Contrast(rgbaEndP, rgbaM) > Fxaa::Contrast(rgbaEndP, highN);
            iterationsP = i;
        }
        if (doneN || doneP)
            break;
    }

    if (!doneN && !doneP)
        return float4(rgbaM.rgb, 1.0); // could not find the edge ends.

    // Edge extent as a fraction of the search window. The nearer completed side
    // wins; squaring the inverse softens mostly-diagonal edges a little.
    var edgeFraction = min(
        doneN ? iterationsN / maxSearch : 1.0,
        doneP ? iterationsP / maxSearch : 1.0);
    edgeFraction = sqrt(edgeFraction);
    let blend = (1.0 - edgeFraction) * 0.5 * clamp(postProcess.subpixel, 0.0, 1.0);

    let finalColor = lerp(rgbaM.rgb, highN.rgb, blend);
    return float4(finalColor, 1.0);
}