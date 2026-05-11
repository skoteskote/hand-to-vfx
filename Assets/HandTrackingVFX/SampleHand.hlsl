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

// Parameters (kept comment-free in the signature so VFX Graph's reflection parses cleanly):
//   PositionMap : 32x1 RGBA32F from HandToVFX.cs
//   Seed        : any value; particleId or a VFX Random both work
//   Thickness   : radial offset around the bone, world units
//   TipBias     : 0 = uniform along bone, 1 = strong bias toward the second endpoint
//   PosSpeed    : xyz = sampled world position, w = interpolated joint speed
//   DirBone     : xyz = bone tangent (start -> end), w = picked bone index (0..20)
//
// Outputs are packed into two float4s on purpose: VFX Graph's Custom HLSL codegen has
// shown character-eating bugs when mixing scalar/vector `out` types or using names that
// start with `Out` (e.g. `OutBoneIndex` -> `utBoneIndex`, `float` -> `oat` in the call
// site). Two uniform float4 outs sidestep both.
void SampleBoneHand(
    Texture2D PositionMap,
    float Seed,
    float Thickness,
    float TipBias,
    out float4 PosSpeed,
    out float4 DirBone)
{
    // Defined inside the function: VFX Graph inlines this .hlsl, and file-scope
    // `static const uintN[]` arrays don't survive that context reliably.
    const int2 kHandBones[21] = {
        int2(0, 1),  int2(1, 2),  int2(2, 3),  int2(3, 4),                   // Thumb
        int2(5, 6),  int2(6, 7),  int2(7, 8),                                // Index
        int2(9, 10), int2(10, 11), int2(11, 12),                             // Middle
        int2(13, 14), int2(14, 15), int2(15, 16),                            // Ring
        int2(17, 18), int2(18, 19), int2(19, 20),                            // Pinky
        int2(0, 17), int2(0, 5), int2(5, 9), int2(9, 13), int2(13, 17)       // Palm fan
    };

    // Pull four independent uniforms out of one seed. Offsetting the hash state with
    // golden-ratio-ish constants decorrelates the streams cheaply.
    uint s = asuint(Seed * 4294967.0);
    float h1 = _SH_HashF(s);
    float h2 = _SH_HashF(s + 0x9e3779b9u);
    float h3 = _SH_HashF(s + 0xfacedeadu);
    float h4 = _SH_HashF(s + 0x12345678u);

    int picked = min((int)floor(h1 * 21.0), 20);
    int2 ij = kHandBones[picked];

    float4 a = PositionMap.Load(int3(ij.x, 0, 0));
    float4 b = PositionMap.Load(int3(ij.y, 0, 0));

    // pow remap of t. TipBias=0 -> t=h2 (uniform). TipBias=1 -> t ~ h2^20 (clumped near b).
    float t = pow(h2, max(0.05, 1.0 - clamp(TipBias, 0.0, 0.95)));

    float3 along = lerp(a.rgb, b.rgb, t);
    float speed = lerp(a.a, b.a, t);

    float3 bone = b.rgb - a.rgb;
    float bl = max(1e-6, length(bone));
    float3 tangent = bone / bl;

    // Disc-uniform radial offset perpendicular to the bone. sqrt(h4) for uniform area density.
    float3 up = abs(tangent.y) < 0.95 ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 bn = normalize(cross(tangent, up));
    float3 nm = cross(tangent, bn);
    float ang = h3 * 6.28318530718;
    float rad = sqrt(h4) * Thickness;

    float3 sampledPos = along + (cos(ang) * bn + sin(ang) * nm) * rad;

    PosSpeed = float4(sampledPos, speed);
    DirBone  = float4(tangent, (float)picked);
}
