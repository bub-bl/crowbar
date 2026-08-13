// Batched SDF shape renderer (Crowbar 2D UI backend).
//
// One instanced unit quad is drawn per shape. The fragment shader evaluates
// the shape as a signed-distance field, paints it with a solid color or an
// inline gradient (up to four stops baked into the instance), and multiplies
// through up to four stacked screen-space clips. A single DrawInstanced call
// therefore covers every rect / rounded-rect / circle / ellipse / line in the
// batch, which is what keeps the draw-call count independent of UI complexity.

struct SdfInstance {
    rect: vec4f,       // local bounding box: xy = top-left, zw = size
    m0: vec4f,         // transform row: screen.x = m0.x*lx + m0.y*ly + m0.z
    m1: vec4f,         // transform row: screen.y = m1.x*lx + m1.y*ly + m1.z
    color: vec4f,      // straight sRGB RGBA (used when the gradient kind is 0)
    params: vec4f,     // x = corner radius, y = stroke width (0 = fill), z = border style, w = dash length
    grad0: vec4f,      // linear: start (local); radial: center (local); line: start
    grad1: vec4f,      // linear: end (local); radial: x = radius, y = focal; line: end
    stop0: vec4f,      // gradient stop colors (straight sRGB)
    stop1: vec4f,
    stop2: vec4f,
    stop3: vec4f,
    offsets: vec4f,    // gradient stop offsets (0..1, ascending)
    clip0: vec4f,      // screen-space clip rects (xy = top-left, zw = size)
    clip1: vec4f,
    clip2: vec4f,
    clip3: vec4f,
    clipRadii: vec4f,  // clip corner radii (px)
    flags: vec4f,      // x = shape kind, y = gradient kind, z = clip count, w = stop count
}

@group(0) @binding(0) var<storage, read> instances: array<SdfInstance>;
@group(0) @binding(1) var<uniform> viewport: vec4f; // xy = size, zw = 1/size

struct VertexOutput {
    @builtin(position) position: vec4f,
    @location(0) local: vec2f,
    // This wgpu build does not expose instance_index in the fragment stage, so
    // it is forwarded from the vertex shader as a flat varying.
    @location(1) @interpolate(flat) instance: u32,
}

@vertex
fn vs_main(@location(0) position: vec2f, @location(1) uv: vec2f,
           @builtin(instance_index) instanceIndex: u32) -> VertexOutput {
    var out: VertexOutput;
    let inst = instances[instanceIndex];
    let local = inst.rect.xy + uv * inst.rect.zw;
    let screen = vec2f(
        inst.m0.x * local.x + inst.m0.y * local.y + inst.m0.z,
        inst.m1.x * local.x + inst.m1.y * local.y + inst.m1.z);
    out.position = vec4f(screen.x * viewport.z * 2.0 - 1.0, 1.0 - screen.y * viewport.w * 2.0, 0.0, 1.0);
    out.local = local;
    out.instance = instanceIndex;
    return out;
}

fn srgbToLinear(c: vec3f) -> vec3f {
    let lo = c / 12.92;
    let hi = pow((c + 0.055) / 1.055, vec3f(2.4));
    return select(hi, lo, c <= vec3f(0.04045));
}

// Signed distance to a rounded rectangle (iquilezles.org/articles/distfunctions2d).
fn roundedRectSDF(p: vec2f, b: vec4f, r: f32) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let q = abs(p - center) - (half - vec2f(rr));
    return length(max(q, vec2f(0.0))) + min(max(q.x, q.y), 0.0) - rr;
}

// Signed distance to an axis-aligned ellipse inscribed in the rect.
fn ellipseSDF(p: vec2f, b: vec4f) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let q = (p - center) / half;
    return (length(q) - 1.0) * min(half.x, half.y);
}

// Distance to a line segment (the clamped projection yields round caps).
fn segmentSDF(p: vec2f, a: vec2f, b: vec2f) -> f32 {
    let pa = p - a;
    let ba = b - a;
    let h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return length(pa - ba * h);
}

// Arc length (local px) along a (rounded) rectangle outline, clockwise from
// the top edge's left end. Straight edges use the nearest-edge distance;
// corners use the quarter-arc angle (the arcs collapse to zero length when
// the corner radius is 0). Used to phase dashed/dotted borders.
fn rectOutlineDist(local: vec2f, b: vec4f, r: f32) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let rr = min(r, min(half.x, half.y));
    let ax = max(half.x - rr, 0.0);
    let ay = max(half.y - rr, 0.0);
    let arcLen = rr * 1.5707963267948966; // quarter arc = r * pi / 2
    let perim = 4.0 * (ax + ay + arcLen);
    if (perim <= 0.0001) { return 0.0; }

    let q = local - center;
    let topEnd = 2.0 * ax;
    let rightArcEnd = topEnd + arcLen;
    let rightEnd = rightArcEnd + 2.0 * ay;
    let bottomArcEnd = rightEnd + arcLen;
    let bottomEnd = bottomArcEnd + 2.0 * ax;
    let leftArcEnd = bottomEnd + arcLen;
    let leftEnd = leftArcEnd + 2.0 * ay;
    let halfPi = 1.5707963267948966;
    let pi = 3.141592653589793;

    let qx = abs(q.x);
    let qy = abs(q.y);
    var u = 0.0;
    if (qx > ax && qy > ay) {
        if (q.x >= 0.0 && q.y <= 0.0) { u = topEnd + (atan2(q.y + ay, q.x - ax) + halfPi) * rr; }
        else if (q.x >= 0.0 && q.y >= 0.0) { u = rightEnd + atan2(q.y - ay, q.x - ax) * rr; }
        else if (q.x <= 0.0 && q.y >= 0.0) { u = bottomEnd + (atan2(q.y - ay, q.x + ax) - halfPi) * rr; }
        else {
            // Top-left corner: the angle sweeps pi (left) -> -pi/2 (top),
            // wrapping through the 2pi boundary.
            var theta = atan2(q.y + ay, q.x + ax);
            if (theta < 0.0) { theta = theta + 6.283185307179586; }
            u = leftEnd + (theta - pi) * rr;
        }
    } else {
        let dTop = abs(q.y + half.y);
        let dBot = abs(half.y - q.y);
        let dLef = abs(q.x + half.x);
        let dRig = abs(half.x - q.x);
        // Select the closest edge without float equality (WGSL forbids == on
        // floats); the four distances are compared directly.
        if (dTop <= dRig && dTop <= dBot && dTop <= dLef) { u = q.x + ax; }
        else if (dRig <= dBot && dRig <= dLef) { u = rightArcEnd + (q.y + ay); }
        else if (dBot <= dLef) { u = bottomArcEnd + (ax - q.x); }
        else { u = leftArcEnd + (ay - q.y); }
    }
    return clamp(u, 0.0, perim);
}

// Approximate arc length along an ellipse outline via the parametric angle.
// Exact for circles; for eccentric ellipses the dash spacing varies slightly.
fn ellipseOutlineDist(local: vec2f, b: vec4f) -> f32 {
    let half = b.zw * 0.5;
    let center = b.xy + half;
    let q = (local - center) / half;
    var t = atan2(q.y, q.x);
    if (t < 0.0) { t = t + 6.283185307179586; } // 0..2pi
    let a = max(half.x, 0.0001);
    let c = max(half.y, 0.0001);
    let h = (a - c) * (a - c) / ((a + c) * (a + c));
    let perim = 3.141592653589793 * (a + c) * (1.0 + 3.0 * h / (10.0 + sqrt(4.0 - 3.0 * h)));
    return t / 6.283185307179586 * perim;
}

// Arc length (local px) along a line segment, clamped to its endpoints.
fn lineOutlineDist(local: vec2f, a: vec2f, b: vec2f) -> f32 {
    let ba = b - a;
    let t = clamp(dot(local - a, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
    return t * length(ba);
}

// 1D on/off dash mask (local px): on for `dash` px, off for the gap (equal to
// `dash` for dashed borders, doubled for dotted so the dots breathe).
fn dashMask(dist: f32, style: u32, dashLen: f32) -> f32 {
    let dash = max(dashLen, 1.0);
    let gap = select(dash, dash * 2.0, style == 2u);
    let period = dash + gap;
    let x = dist - period * floor(dist / period);
    let d = abs(x - dash * 0.5) - dash * 0.5;
    return 1.0 - smoothstep(-1.0, 1.0, d);
}

// The gradient stops are indexed with constant indices only (naga rejects
// dynamic indices into struct-member arrays); the count gates each step.
fn stopColor(inst: SdfInstance, i: u32) -> vec4f {
    var c = inst.stop0;
    if (i == 1u) { c = inst.stop1; }
    else if (i == 2u) { c = inst.stop2; }
    else if (i == 3u) { c = inst.stop3; }
    return c;
}

fn stopOffset(inst: SdfInstance, i: u32) -> f32 {
    var o = inst.offsets.x;
    if (i == 1u) { o = inst.offsets.y; }
    else if (i == 2u) { o = inst.offsets.z; }
    else if (i == 3u) { o = inst.offsets.w; }
    return o;
}

fn evalGradient(inst: SdfInstance, local: vec2f) -> vec4f {
    let kind = u32(inst.flags.y);
    var t = 0.0;
    if (kind == 1u) {
        let d = inst.grad1.xy - inst.grad0.xy;
        let denom = dot(d, d);
        t = select(0.0, clamp(dot(local - inst.grad0.xy, d) / denom, 0.0, 1.0), denom > 0.0);
    } else if (kind == 2u) {
        let r = max(inst.grad1.x, 0.0001);
        t = clamp(length(local - inst.grad0.xy) / r, 0.0, 1.0);
    }
    let count = clamp(u32(inst.flags.w), 1u, 4u);
    var color = stopColor(inst, 0u);
    var prevOff = stopOffset(inst, 0u);
    var prevCol = color;
    for (var i = 1u; i < 4u; i++) {
        if (i < count) {
            let off = stopOffset(inst, i);
            let col = stopColor(inst, i);
            if (t >= prevOff && t <= off) {
                let span = max(off - prevOff, 0.0001);
                color = mix(prevCol, col, clamp((t - prevOff) / span, 0.0, 1.0));
            }
            prevOff = off;
            prevCol = col;
        }
    }
    return color;
}

fn clipRect(inst: SdfInstance, i: u32) -> vec4f {
    var c = inst.clip0;
    if (i == 1u) { c = inst.clip1; }
    else if (i == 2u) { c = inst.clip2; }
    else if (i == 3u) { c = inst.clip3; }
    return c;
}

fn clipRadius(inst: SdfInstance, i: u32) -> f32 {
    var r = inst.clipRadii.x;
    if (i == 1u) { r = inst.clipRadii.y; }
    else if (i == 2u) { r = inst.clipRadii.z; }
    else if (i == 3u) { r = inst.clipRadii.w; }
    return r;
}

// Product of the (up to four) screen-space clip masks, each a rounded rect.
fn clipMask(inst: SdfInstance, frag: vec2f) -> f32 {
    var mask = 1.0;
    let count = u32(inst.flags.z);
    if (count > 0u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 0u), clipRadius(inst, 0u))); }
    if (count > 1u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 1u), clipRadius(inst, 1u))); }
    if (count > 2u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 2u), clipRadius(inst, 2u))); }
    if (count > 3u) { mask *= 1.0 - smoothstep(-1.0, 0.0, roundedRectSDF(frag, clipRect(inst, 3u), clipRadius(inst, 3u))); }
    return mask;
}

@fragment
fn fs_main(@builtin(position) frag: vec4f, @location(0) local: vec2f,
           @location(1) @interpolate(flat) instance: u32) -> @location(0) vec4f {
    let inst = instances[instance];
    let kind = u32(inst.flags.x);

    var sd = 0.0;
    if (kind == 3u) {
        // Line segment (round caps fall out of the clamped projection).
        sd = segmentSDF(local, inst.grad0.xy, inst.grad1.xy) - max(inst.params.y * 0.5, 0.0);
    } else if (kind == 2u) {
        sd = ellipseSDF(local, inst.rect);
    } else {
        sd = roundedRectSDF(local, inst.rect, inst.params.x);
    }

    let strokeWidth = inst.params.y;
    let style = u32(inst.params.z);
    let dashLen = inst.params.w;

    // A stroke is the ring between |sd| <= width/2; a fill is the interior.
    // Dashed/dotted borders modulate the ring along the outline; double draws
    // two concentric rings.
    var coverage = 0.0;
    if (strokeWidth > 0.0 && kind != 3u) {
        if (style == 3u) {
            // Double: two rings at +/- width/3, each width/3 thick.
            let third = strokeWidth / 3.0;
            let outer = 1.0 - smoothstep(-1.0, 0.0, abs(sd - third) - third * 0.5);
            let inner = 1.0 - smoothstep(-1.0, 0.0, abs(sd + third) - third * 0.5);
            coverage = max(outer, inner);
        } else if (style == 1u || style == 2u) {
            let dist = select(
                rectOutlineDist(local, inst.rect, inst.params.x),
                ellipseOutlineDist(local, inst.rect),
                kind == 2u);
            if (style == 1u) {
                coverage = (1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5)) * dashMask(dist, style, dashLen);
            } else {
                // Dotted: round dots centred on the boundary.
                let period = max(dashLen * 3.0, 1.0);
                let x = dist - period * floor(dist / period);
                let along = min(x, period - x);
                let rdot = max(dashLen * 0.5, 0.5);
                let d2 = sqrt(sd * sd + along * along);
                coverage = 1.0 - smoothstep(-1.0, 1.0, d2 - rdot);
            }
        } else {
            coverage = 1.0 - smoothstep(-1.0, 0.0, abs(sd) - strokeWidth * 0.5);
        }
    } else if (strokeWidth > 0.0) {
        // Line stroke (round caps).
        if (style == 1u || style == 2u) {
            let dist = lineOutlineDist(local, inst.grad0.xy, inst.grad1.xy);
            if (style == 1u) {
                coverage = (1.0 - smoothstep(-1.0, 0.0, sd)) * dashMask(dist, style, dashLen);
            } else {
                let period = max(dashLen * 3.0, 1.0);
                let x = dist - period * floor(dist / period);
                let along = min(x, period - x);
                let rdot = max(dashLen * 0.5, 0.5);
                let d2 = sqrt(sd * sd + along * along);
                coverage = 1.0 - smoothstep(-1.0, 1.0, d2 - rdot);
            }
        } else {
            coverage = 1.0 - smoothstep(-1.0, 0.0, sd);
        }
    } else {
        // Fill.
        coverage = 1.0 - smoothstep(-1.0, 0.0, sd);
    }

    var color = inst.color;
    if (inst.flags.y > 0.0) {
        color = evalGradient(inst, local);
    }

    let alpha = clamp(color.a * coverage * clipMask(inst, frag.xy), 0.0, 1.0);
    return vec4f(srgbToLinear(color.rgb), alpha);
}
