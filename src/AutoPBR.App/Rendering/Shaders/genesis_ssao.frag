#version 330 core
// Hemisphere SSAO (Alchemy-style depth test) for Genesis preview screen-space AO.

//!include "common/screen_space_ao_common.glsl"

in vec2 vUv;

uniform sampler2D uSceneDepth;
uniform sampler2D uViewNormal;
uniform mat4 uInvProj;
uniform mat4 uProj;
uniform vec2 uAoTexelSize;
uniform float uAoRadius;
uniform float uAoBias;
uniform float uAoPower;
uniform float uAoIntensity;
uniform int uAoSampleCount;
uniform float uFrameIndex;
uniform int uHasSceneDepth;
uniform int uHasViewNormal;

out vec4 FragColor;

const int SSAO_MAX_SAMPLES = 24;

void main()
{
    float aoOut = 1.0;
    if (uHasSceneDepth > 0)
    {
        float depth = texture(uSceneDepth, vUv).r;
        if (ssaoHasOpaqueDepth(depth))
        {
            vec4 nPack = uHasViewNormal > 0 ? texture(uViewNormal, vUv) : vec4(0.0);
            vec3 viewPos = ssaoViewPosFromUvDepth(vUv, depth, uInvProj);
            vec3 viewN = (nPack.a > 0.5)
                ? ssaoUnpackViewNormal(nPack)
                : ssaoViewNormalFromDepth(uSceneDepth, vUv, uInvProj, uAoTexelSize);
            float noise = ssaoSpatialNoise(gl_FragCoord.xy, uFrameIndex);
            float angle = noise * SSAO_PI * 2.0;
            float radius = max(uAoRadius, 0.01);
            float occ = 0.0;
            float weightSum = 0.0;
            int sampleCount = clamp(uAoSampleCount, 1, SSAO_MAX_SAMPLES);

            // Build a tangent frame for hemisphere sampling in view space.
            vec3 up = abs(viewN.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
            vec3 tangent = normalize(cross(up, viewN));
            vec3 bitangent = cross(viewN, tangent);

            for (int i = 0; i < SSAO_MAX_SAMPLES; i++)
            {
                if (i >= sampleCount)
                {
                    break;
                }

                float fi = (float(i) + 0.5) / float(sampleCount);
                float spiral = fi * SSAO_PI * 2.0 * 2.4 + angle;
                float r = sqrt(fi) * radius;
                vec2 disk = ssaoRotate2(vec2(cos(spiral), sin(spiral)), angle) * r;
                // Lift into the hemisphere around the surface normal.
                vec3 sampleOffset = tangent * disk.x + bitangent * disk.y + viewN * (radius * 0.35 * (1.0 - fi));
                vec3 sampleView = viewPos + sampleOffset;

                vec2 sampleUv = ssaoProjectViewToUv(sampleView, uProj);
                float sampleW = 0.0;
                float sampleOcc = 0.0;
                if (sampleUv.x > 0.0 && sampleUv.x < 1.0 && sampleUv.y > 0.0 && sampleUv.y < 1.0)
                {
                    float sampleDepth = texture(uSceneDepth, sampleUv).r;
                    if (ssaoHasOpaqueDepth(sampleDepth))
                    {
                        vec3 samplePos = ssaoViewPosFromUvDepth(sampleUv, sampleDepth, uInvProj);
                        vec3 v = samplePos - viewPos;
                        float dist2 = max(dot(v, v), 1e-8);
                        float dist = sqrt(dist2);
                        float range = 1.0 - smoothstep(0.0, radius, dist);
                        // Occluder in front of the hemisphere plane.
                        float ndot = max(dot(viewN, v * inversesqrt(dist2)) - uAoBias, 0.0);
                        sampleOcc = ndot * range;
                        sampleW = range;
                    }
                }

                occ += sampleOcc;
                weightSum += max(sampleW, 0.001);
            }

            float visibility = 1.0 - clamp((occ / weightSum) * uAoIntensity, 0.0, 1.0);
            aoOut = pow(clamp(visibility, 0.0, 1.0), max(uAoPower, 0.01));
        }
    }

    FragColor = vec4(aoOut, aoOut, aoOut, 1.0);
}
