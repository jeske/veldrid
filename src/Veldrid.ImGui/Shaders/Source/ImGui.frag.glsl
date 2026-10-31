#version 450

#extension GL_ARB_separate_shader_objects : enable
#extension GL_ARB_shading_language_420pack : enable

layout(set = 1, binding = 0) uniform texture2D FontTexture;
layout(set = 0, binding = 1) uniform sampler FontSampler;

layout (location = 0) in vec4 vsout_color;
layout (location = 1) in vec2 vsout_texCoord;
layout (location = 0) out vec4 outputColor;

void main()
{
    outputColor = vsout_color * texture(sampler2D(FontTexture, FontSampler), vsout_texCoord);
}
