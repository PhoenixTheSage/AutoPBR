#version 330 core
// Foliage-mask shaft attenuation for froxel god rays (replaces sun-cone depth march).
// Multiplies upsampled in-scatter by (1 - cutout occupancy) so milky fill does not sit on
// leaf faces. Leaf holes in shafts come from cutout shadow maps during froxel inject.
// Temporal history keeps the pre-mask upsample so this filter does not accumulate.

//!include "common/common.glsl"

in vec2 vUv;

uniform sampler2D uRays;
uniform sampler2D uFoliageMask;
uniform float uStrength;

out vec4 FragColor;

void main()
{
    vec4 rays = texture(uRays, vUv);
    float strength = saturate1(uStrength);
    float mask = texture(uFoliageMask, vUv).a;
    // mask=1 on surviving cutout fragments (leaves); leave transmittance (A) alone.
    rays.rgb *= mix(1.0, 1.0 - mask, strength);
    FragColor = rays;
}
