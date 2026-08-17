#language slang 2026;
// Signed-distance-field primitives and outline arc-length helpers shared by
// the UI shape, box-shadow and backdrop passes. Kept in one place so the
// geometry definitions can never drift between shaders.
module Sdf;

public struct Sdf
{
    // Signed distance to a rounded rectangle (iquilezles.org/articles/distfunctions2d).
    static float RoundedRect(float2 p, float4 b, float r)
    {
        let half = b.zw * 0.5;
        let center = b.xy + half;
        let rr = min(r, min(half.x, half.y));
        let q = abs(p - center) - (half - float2(rr));
        return length(max(q, float2(0.0))) + min(max(q.x, q.y), 0.0) - rr;
    }

    // Signed distance to an axis-aligned ellipse inscribed in the rect.
    static float Ellipse(float2 p, float4 b)
    {
        let half = b.zw * 0.5;
        let center = b.xy + half;
        let q = (p - center) / half;
        return (length(q) - 1.0) * min(half.x, half.y);
    }

    // Distance to a line segment (the clamped projection yields round caps).
    static float Segment(float2 p, float2 a, float2 b)
    {
        let pa = p - a;
        let ba = b - a;
        let h = clamp(dot(pa, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
        return length(pa - ba * h);
    }

    // Arc length (local px) along a (rounded) rectangle outline, clockwise from
    // the top edge's left end. Straight edges use the nearest-edge distance;
    // corners use the quarter-arc angle. Used to phase dashed/dotted borders.
    static float RectOutlineDistance(float2 local, float4 b, float r)
    {
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
        if (qx > ax && qy > ay)
        {
            if (q.x >= 0.0 && q.y <= 0.0) { u = topEnd + (atan2(q.y + ay, q.x - ax) + halfPi) * rr; }
            else if (q.x >= 0.0 && q.y >= 0.0) { u = rightEnd + atan2(q.y - ay, q.x - ax) * rr; }
            else if (q.x <= 0.0 && q.y >= 0.0) { u = bottomEnd + (atan2(q.y - ay, q.x + ax) - halfPi) * rr; }
            else
            {
                // Top-left corner: the angle sweeps pi (left) -> -pi/2 (top),
                // wrapping through the 2pi boundary.
                var theta = atan2(q.y + ay, q.x + ax);
                if (theta < 0.0) { theta = theta + 6.283185307179586; }
                u = leftEnd + (theta - pi) * rr;
            }
        }
        else
        {
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
    static float EllipseOutlineDistance(float2 local, float4 b)
    {
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
    static float LineOutlineDistance(float2 local, float2 a, float2 b)
    {
        let ba = b - a;
        let t = clamp(dot(local - a, ba) / max(dot(ba, ba), 1e-6), 0.0, 1.0);
        return t * length(ba);
    }

    // 1D on/off dash mask (local px): on for `dash` px, off for the gap (equal
    // to `dash` for dashed borders, doubled for dotted so the dots breathe).
    static float Dash(float dist, uint style, float dashLen)
    {
        let dash = max(dashLen, 1.0);
        let gap = select(style == 2u, dash * 2.0, dash);
        let period = dash + gap;
        let x = dist - period * floor(dist / period);
        let d = abs(x - dash * 0.5) - dash * 0.5;
        return 1.0 - smoothstep(-1.0, 1.0, d);
    }
};
