// Froxel inject output packing.
// GLES/ANGLE: component writes (no swizzles in vec4() constructors; avoid identifier "packed").
// Desktop GL: direct vec4() return.
//
// Layout: R = medium density, G/B = scalar sun energy (shadowed), A = occupancy.
// Chroma is NOT stored — integrate multiplies energy by uLightColor so day/dusk/dawn/night
// shaft tint (and scene light color) reaches terrain fog the same way as screen-space rays.

#ifndef GENESIS_VOLUME_INJECT_PACK_GLSL
#define GENESIS_VOLUME_INJECT_PACK_GLSL

vec4 viPackFroxelInject(float mediumRho, vec3 lightColor, float shadowGate)
{
    float occ = step(GEN_EPS, mediumRho);
    // lightColor kept in the signature for call-site compatibility; chroma applied at integrate.
    float sunEnergy = mediumRho * shadowGate * 1.15 + 0.0 * (lightColor.r + lightColor.g + lightColor.b);
#ifdef GENESIS_GLES
    vec4 injectOut;
    injectOut.r = mediumRho;
    injectOut.g = sunEnergy;
    injectOut.b = sunEnergy;
    injectOut.a = occ;
    return injectOut;
#else
    return vec4(mediumRho, sunEnergy, sunEnergy, occ);
#endif
}

#endif // GENESIS_VOLUME_INJECT_PACK_GLSL
