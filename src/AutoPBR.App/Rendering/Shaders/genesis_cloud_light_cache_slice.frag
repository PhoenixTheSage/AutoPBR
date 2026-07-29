#version 330 core
// CQ3 desktop GL 3.3 fallback generator for one cloud-light cache slice.

//!include "common/common.glsl"
//!include "common/volumetric_clouds_density_maps.glsl"
//!include "common/cloud_light_cache_generation.glsl"

in vec2 vUv;

uniform sampler2D uPreviousPrefix;
uniform int uLayerIndex;
uniform int uHasPrevious;

layout(location = 0) out vec2 FragCache;
layout(location = 1) out vec2 FragPrefix;

void main()
{
    vec2 previous = uHasPrevious > 0
        ? texture(uPreviousPrefix, vUv).rg
        : vec2(0.0, 1.0);
    vec2 cacheValue = cq3IntegrateLayer(vUv, uLayerIndex, previous);
    FragCache = cacheValue;
    FragPrefix = cacheValue;
}
