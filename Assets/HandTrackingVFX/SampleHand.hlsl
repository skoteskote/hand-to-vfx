#ifndef HANDTRACKINGVFX_SAMPLE_HAND_HLSL
#define HANDTRACKINGVFX_SAMPLE_HAND_HLSL

// SampleHand — picks a position on the MediaPipe 21-joint hand skeleton for VFX Graph.
//
// PositionMap is the 32x1 RGBA32F texture written by HandToVFX.cs:
//   .rgb = joint world position
//   .a   = joint speed (world units / second, lightly smoothed)
// Joints 0..20 are filled, 21..31 are zero.
//
// Use as a VFX Graph Custom HLSL operator. Feed Seed from particleId (Random per particle).
// Output Space on consuming contexts should be set to World (positions are world-space).
//
// Bone topology matches HandTransformUpdater.BonePairs from the legacy project: 4 thumb +
// 3 per finger (index/middle/ring/pinky) + a 5-segment palm fan = 21 bones. Picking
// uniformly among bones biases mass toward the palm (5 palm segments vs. 4 thumb / 3
// per finger), which reads as a "filled hand" rather than a sparse skeleton. Adjust the
// bone array if you want different density.

static const uint2 kHandBones[21] = {
    uint2(0, 1),  uint2(1, 2),  uint2(2, 3),  uint2(3, 4),                   // Thumb
    uint2(5, 6),  uint2(6, 7),  uint2(7, 8),                                 // Index
    uint2(9, 10), uint2(10, 11), uint2(11, 12),                              // Middle
    uint2(13, 14), uint2(14, 15), uint2(15, 16),                             // Ring
    uint2(17, 18), uint2(18, 19), uint2(19, 20),                             // Pinky
    uint2(0, 17), uint2(0, 5), uint2(5, 9), uint2(9, 13), uint2(13, 17)      // Palm fan
};

// PCG-style 32-bit hash. Stable across GPUs (frac(sin(...)) drifts on mobile / Metal).
uint _SH_Hash(uint x)
{
    x ^= x >> 17; x *= 0xed5ad4bbu;
    x ^= x >> 11; x *= 0xac4c1b51u;
    x ^= x >> 15; x *= 0x31848babu;
    x ^= x >> 14;
    return x;
}
float _SH_HashF(uint x) { return float(_SH_Hash(x) & 0x00ffffffu) / 16777216.0; }

void SampleHand(
    in Texture2D PositionMap,
    in float Seed,           // any value; particleId or a VFX Random both work
    in float Thickness,      // radial offset around the bone, world units
    in float TipBias,        // 0 = uniform along bone, 1 = strong bias toward the second endpoint
    out float3 Position,     // sampled point in world space
    out float3 Tangent,      // normalised direction along the bone (start -> end)
    out float Speed,         // interpolated joint speed at this point
    out uint BoneIndex)      // which bone was picked (0..20), useful for coloring
{
    // Pull four independent uniforms out of one seed. Offsetting the hash state with
    // golden-ratio-ish constants decorrelates the streams cheaply.
    uint s = asuint(Seed * 4294967.0);
    float h1 = _SH_HashF(s);
    float h2 = _SH_HashF(s + 0x9e3779b9u);
    float h3 = _SH_HashF(s + 0xfacedeadu);
    float h4 = _SH_HashF(s + 0x12345678u);

    BoneIndex = min((uint)floor(h1 * 21.0), 20u);
    uint2 ij = kHandBones[BoneIndex];

    float4 a = PositionMap.Load(int3((int)ij.x, 0, 0));
    float4 b = PositionMap.Load(int3((int)ij.y, 0, 0));

    // pow remap of t. TipBias=0 -> t=h2 (uniform). TipBias=1 -> t ~ h2^20 (clumped near b).
    float t = pow(h2, max(0.05, 1.0 - clamp(TipBias, 0.0, 0.95)));

    float3 along = lerp(a.rgb, b.rgb, t);
    Speed = lerp(a.a, b.a, t);

    float3 bone = b.rgb - a.rgb;
    float bl = max(1e-6, length(bone));
    Tangent = bone / bl;

    // Disc-uniform radial offset perpendicular to the bone. sqrt(h4) for uniform area density.
    float3 up = abs(Tangent.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 bn = normalize(cross(Tangent, up));
    float3 nm = cross(Tangent, bn);
    float ang = h3 * 6.28318530718;
    float rad = sqrt(h4) * Thickness;

    Position = along + (cos(ang) * bn + sin(ang) * nm) * rad;
}

#endif
