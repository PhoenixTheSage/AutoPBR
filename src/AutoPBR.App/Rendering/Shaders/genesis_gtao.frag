#version 330 core
// Ground-truth ambient occlusion (Jimenez / XeGTAO-style multi-slice horizon search).

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
uniform int uGtaoSlices;
uniform int uGtaoSteps;
uniform float uFrameIndex;
uniform int uHasSceneDepth;
uniform int uHasViewNormal;
uniform int uEnableMultiBounce;

out vec4 FragColor;

const int GTAO_MAX_SLICES = 6;
const int GTAO_MAX_STEPS = 8;

// Cosine-weighted integral of the free arc for one slice (n = angle between
// projected normal and the slice; h0/h1 = horizon angles from that axis).
float gtaoIntegrateArc(float h0, float h1, float n)
{
    float cosN = cos(n);
    float sinN = sin(n);
    return 0.25 * (
        (-cos(2.0 * h0 - n) + cosN + 2.0 * h0 * sinN) +
        (-cos(2.0 * h1 - n) + cosN + 2.0 * h1 * sinN));
}

void main()
{
    float aoOut = 1.0;
    if (uHasSceneDepth > 0)
    {
        float depth = texture(uSceneDepth, vUv).r;
        if (ssaoHasOpaqueDepth(depth))
        {
            vec4 nPack = vec4(0.0);
            if (uHasViewNormal > 0)
            {
                nPack = texture(uViewNormal, vUv);
            }

            vec3 viewPos = ssaoViewPosFromUvDepth(vUv, depth, uInvProj);
            vec3 viewN;
            if (nPack.a > 0.5)
            {
                viewN = ssaoUnpackViewNormal(nPack);
            }
            else
            {
                viewN = ssaoViewNormalFromDepth(uSceneDepth, vUv, uInvProj, uAoTexelSize);
            }

            float noise = ssaoSpatialNoise(gl_FragCoord.xy, uFrameIndex);
            float radius = max(uAoRadius, 0.01);
            int sliceCount = clamp(uGtaoSlices, 1, GTAO_MAX_SLICES);
            int stepCount = clamp(uGtaoSteps, 1, GTAO_MAX_STEPS);

            // Project the view-space search radius to screen pixels at this depth.
            vec3 offsetPos = viewPos + vec3(radius, 0.0, 0.0);
            vec2 uvOffset = ssaoProjectViewToUv(offsetPos, uProj) - vUv;
            float pixelRadius = max(length(uvOffset / max(uAoTexelSize, vec2(1e-6))), 8.0);

            // Projected normal in view XY for per-slice n angle.
            vec2 projNormalXY = viewN.xy;
            float projNormalLen = length(projNormalXY);
            float visibility = 0.0;

            for (int slice = 0; slice < GTAO_MAX_SLICES; slice++)
            {
                if (slice >= sliceCount)
                {
                    break;
                }

                float phi = ((float(slice) + noise) / float(sliceCount)) * SSAO_PI;
                vec2 omega = vec2(cos(phi), sin(phi));

                // Angle between projected normal and this slice direction.
                float nAngle = 0.0;
                if (projNormalLen > 1e-4)
                {
                    nAngle = acos(clamp(dot(projNormalXY / projNormalLen, omega), -1.0, 1.0)) - SSAO_PI * 0.5;
                }

                // Open hemisphere: horizons at +/- pi/2 from the slice axis (cos = 0).
                // Raise cos toward +1 as occluders climb above the surface.
                float cosh0 = 0.0;
                float cosh1 = 0.0;

                for (int step = 0; step < GTAO_MAX_STEPS; step++)
                {
                    if (step >= stepCount)
                    {
                        break;
                    }

                    // Uniform steps with a small noise offset to reduce banding.
                    float stepT = (float(step) + noise) / float(stepCount);
                    float stepRadius = max(pixelRadius * stepT, 1.0);
                    vec2 offset = omega * stepRadius * uAoTexelSize;

                    vec2 uv0 = vUv + offset;
                    vec2 uv1 = vUv - offset;

                    if (uv0.x > 0.0 && uv0.x < 1.0 && uv0.y > 0.0 && uv0.y < 1.0)
                    {
                        float d0 = texture(uSceneDepth, uv0).r;
                        if (ssaoHasOpaqueDepth(d0))
                        {
                            vec3 p0 = ssaoViewPosFromUvDepth(uv0, d0, uInvProj);
                            vec3 dv = p0 - viewPos;
                            float dist2 = max(dot(dv, dv), 1e-8);
                            float dist = sqrt(dist2);
                            float falloff = 1.0 - smoothstep(0.0, radius, dist);
                            // Horizon cos from the surface normal; falloff softens distant hits.
                            float cosh = (dot(dv, viewN) * inversesqrt(dist2) - uAoBias) * falloff;
                            cosh0 = max(cosh0, cosh);
                        }
                    }

                    if (uv1.x > 0.0 && uv1.x < 1.0 && uv1.y > 0.0 && uv1.y < 1.0)
                    {
                        float d1 = texture(uSceneDepth, uv1).r;
                        if (ssaoHasOpaqueDepth(d1))
                        {
                            vec3 p1 = ssaoViewPosFromUvDepth(uv1, d1, uInvProj);
                            vec3 dv = p1 - viewPos;
                            float dist2 = max(dot(dv, dv), 1e-8);
                            float dist = sqrt(dist2);
                            float falloff = 1.0 - smoothstep(0.0, radius, dist);
                            float cosh = (dot(dv, viewN) * inversesqrt(dist2) - uAoBias) * falloff;
                            cosh1 = max(cosh1, cosh);
                        }
                    }
                }

                float h0 = acos(clamp(cosh0, -1.0, 1.0));
                float h1 = acos(clamp(cosh1, -1.0, 1.0));
                // Clamp horizons so they stay on the correct side of the projected normal.
                h0 = clamp(h0, -nAngle, SSAO_PI * 0.5);
                h1 = clamp(h1, nAngle, SSAO_PI * 0.5);
                visibility += clamp(gtaoIntegrateArc(h0, h1, nAngle), 0.0, 1.0);
            }

            float raw = clamp(visibility / float(max(sliceCount, 1)), 0.0, 1.0);
            // Intensity > 1 deepens occlusion without washing open areas.
            raw = mix(1.0, raw, clamp(uAoIntensity, 0.0, 2.0));
            if (uEnableMultiBounce > 0)
            {
                raw = ssaoGtaoMultiBounce(raw, vec3(0.33));
            }

            aoOut = pow(clamp(raw, 0.0, 1.0), max(uAoPower, 0.01));
        }
    }

    FragColor = vec4(aoOut, aoOut, aoOut, 1.0);
}
