#version 450

layout(location = 0) in vec3 vColor;
layout(location = 1) in float vIntensity;
layout(location = 2) in vec2 vLocalUv;
layout(location = 3) flat in uint vFlags;

layout(push_constant) uniform BulbParams
{
    vec3 offTint;
    float bodyContribution;
    float coreContribution;
    float specularContribution;
    float coreBase;
    float coreIntensityScale;
    float specularBase;
    float specularIntensityScale;
    float specularMax;
} p;

layout(location = 0) out vec4 outColor;

void main()
{
    float rootIntensity = sqrt(clamp(vIntensity, 0.0, 1.0));
    float coreOpacity = (vIntensity > 0.0)
        ? clamp(p.coreBase + (rootIntensity * p.coreIntensityScale), 0.0, 1.0)
        : 0.0;
    float specOpacity = (vIntensity > 0.0)
        ? clamp(p.specularBase + (rootIntensity * p.specularIntensityScale), 0.0, clamp(p.specularMax, 0.0, 1.0))
        : 0.0;

    // TODO: replace with sampled kernel textures for body/core/spec.
    float bodyMask = 1.0;
    float coreMask = 1.0;
    float specMask = 1.0;

    vec3 result =
        (p.offTint * bodyMask * p.bodyContribution) +
        (vColor * coreMask * coreOpacity * p.coreContribution) +
        (vec3(1.0) * specMask * specOpacity * p.specularContribution);

    outColor = vec4(clamp(result, 0.0, 1.0), 1.0);
}
