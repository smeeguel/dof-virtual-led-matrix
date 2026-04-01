#version 450

layout(location = 0) in vec2 inLocalPos;
layout(location = 1) in vec2 inDotPos;
layout(location = 2) in vec4 inColorIntensity; // rgb in xyz, intensity in w (0..1)
layout(location = 3) in uint inFlags;

layout(set = 0, binding = 0) uniform FrameUniforms
{
    vec2 matrixSize;
    vec2 dotStride;
    vec2 viewportSize;
    float brightness;
    float gamma;
} uFrame;

layout(location = 0) out vec3 vColor;
layout(location = 1) out float vIntensity;
layout(location = 2) out vec2 vLocalUv;
layout(location = 3) flat out uint vFlags;

void main()
{
    vec2 dotOriginPx = inDotPos * uFrame.dotStride;
    vec2 pixelPos = dotOriginPx + inLocalPos;

    vec2 ndc = vec2(
        (pixelPos.x / max(1.0, uFrame.viewportSize.x)) * 2.0 - 1.0,
        1.0 - (pixelPos.y / max(1.0, uFrame.viewportSize.y)) * 2.0);

    gl_Position = vec4(ndc, 0.0, 1.0);
    vColor = inColorIntensity.xyz;
    vIntensity = inColorIntensity.w;
    vLocalUv = inLocalPos;
    vFlags = inFlags;
}
