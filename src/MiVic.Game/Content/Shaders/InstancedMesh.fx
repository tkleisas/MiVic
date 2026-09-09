#if OPENGL
    #define SV_POSITION POSITION
    #define VS_SHADERMODEL vs_3_0
    #define PS_SHADERMODEL ps_3_0
#else
    #define SV_POSITION SV_Position
    #define VS_SHADERMODEL vs_4_0_level_9_1
    #define PS_SHADERMODEL ps_4_0_level_9_1
#endif

// -----------------------------------------------------------------------------
// MiVic instanced mesh shader.
//
// One draw call renders every unit that shares a mesh. The per-instance data
// arrives as a world matrix plus a colour, so a single 36-vertex box can stand
// in for hundreds of tanks. This is the piece that makes a full-3D RTS
// affordable: draw calls scale with mesh variety, not with unit count.
// -----------------------------------------------------------------------------

float4x4 ViewProjection;
float3 LightDirection;
float4 AmbientColor;
float4 FogColor;
float3 CameraPosition;
float FogStart;
float FogEnd;

struct VertexInput
{
    float4 Position : POSITION0;
    float3 Normal   : NORMAL0;
    float4 Color    : COLOR0;
};

struct InstanceInput
{
    float4 Row0  : TEXCOORD1;
    float4 Row1  : TEXCOORD2;
    float4 Row2  : TEXCOORD3;
    float4 Row3  : TEXCOORD4;
    float4 Color : TEXCOORD5;
};

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float3 Normal   : TEXCOORD0;
    float4 Color    : COLOR0;
    float3 WorldPos : TEXCOORD6;
};

VertexOutput MainVS(VertexInput input, InstanceInput instance)
{
    float4x4 world = float4x4(instance.Row0, instance.Row1, instance.Row2, instance.Row3);
    float4 worldPosition = mul(input.Position, world);

    VertexOutput output;
    output.Position = mul(worldPosition, ViewProjection);
    output.Normal = normalize(mul(input.Normal, (float3x3)world));
    // The mesh's own material shade times the per-instance faction tint.
    output.Color = input.Color * instance.Color;
    output.WorldPos = worldPosition.xyz;
    return output;
}

float4 MainPS(VertexOutput input) : COLOR0
{
    float3 normal = normalize(input.Normal);

    // Half-lambert keeps unlit faces readable instead of crushing them to black.
    float lambert = saturate(dot(normal, LightDirection));
    float wrap = (lambert * 0.5) + 0.5;
    float3 lit = input.Color.rgb * (AmbientColor.rgb + (wrap * 0.75));

    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    float3 finalColor = lerp(lit, FogColor.rgb, fogAmount);
    return float4(finalColor, input.Color.a);
}

// -----------------------------------------------------------------------------
// Particles: smoke, dust, sparks and debris.
//
// The same vertex and instance layout as the lit technique, so a particle needs
// no new vertex format — but no lighting either. A smoke puff lit by the sun
// would read as a solid lump; what sells it is the per-particle colour and alpha
// fading out over its life, which the CPU already computed.
// -----------------------------------------------------------------------------

float4 ParticlePS(VertexOutput input) : COLOR0
{
    // Distance fog still applies, so a smoke column far away sits in the haze
    // like everything else rather than glowing through it.
    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    float3 color = lerp(input.Color.rgb, FogColor.rgb, fogAmount);
    return float4(color, input.Color.a);
}

technique Instanced
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
};

technique Particles
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL ParticlePS();
    }
};
