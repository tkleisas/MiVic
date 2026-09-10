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

// COLOR0 is the mesh's own material colour, and its alpha says how much of the
// faction colour replaces it: 1 is "paint this plate in the team colour", 0 is
// "leave this material alone". Tracks, gun barrels, glass and terrain sit at the
// material end, hull plating at the team end, so a tank keeps a black rubber
// track and a grey gun while its armour still reads as red, amber or blue.
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
    float4 Material : COLOR0;
    float3 WorldPos : TEXCOORD6;
    float4 Tint     : TEXCOORD7;
};

VertexOutput MainVS(VertexInput input, InstanceInput instance)
{
    float4x4 world = float4x4(instance.Row0, instance.Row1, instance.Row2, instance.Row3);
    float4 worldPosition = mul(input.Position, world);

    VertexOutput output;
    output.Position = mul(worldPosition, ViewProjection);
    output.Normal = normalize(mul(input.Normal, (float3x3)world));
    output.Material = input.Color;
    output.Tint = instance.Color;
    output.WorldPos = worldPosition.xyz;
    return output;
}

/// <summary>
/// Mixes a material colour with the owning faction's colour.
///
/// The faction colour is shaded by the material's own brightness rather than
/// replacing it outright: that keeps the panel-to-panel contrast the modeller
/// authored (a light deck plate against a dark skirt) inside the team hue, which
/// is what stops every unit of a faction reading as one flat silhouette.
/// </summary>
float3 SurfaceColor(float4 material, float3 tint)
{
    float luma = dot(material.rgb, float3(0.299, 0.587, 0.114));
    float3 team = tint * (0.25 + (0.75 * luma));

    return lerp(material.rgb, team, saturate(material.a));
}

float4 MainPS(VertexOutput input) : COLOR0
{
    float3 normal = normalize(input.Normal);
    float3 base = SurfaceColor(input.Material, input.Tint.rgb);

    // A hemisphere instead of one flat ambient term: upward faces catch the sky
    // and downward ones fall into the ground's shadow, which is what gives a
    // low-poly model readable shading from an RTS camera looking down.
    float hemi = saturate((normal.y * 0.5) + 0.5);
    float3 ambient = AmbientColor.rgb * lerp(0.58, 1.30, hemi);

    // Half-lambert keeps unlit faces readable instead of crushing them to black.
    float lambert = saturate(dot(normal, LightDirection));
    float wrap = (lambert * 0.6) + 0.4;
    float3 lit = base * (ambient + (wrap * 0.68));

    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    float3 finalColor = lerp(lit, FogColor.rgb, fogAmount);
    return float4(finalColor, input.Tint.a);
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
    // Particles are unlit by design, and their mesh is plain white: the colour
    // and the fade both come from the per-particle instance tint.
    float3 color = input.Material.rgb * input.Tint.rgb;
    float opacity = input.Material.a * input.Tint.a;

    // Distance fog still applies, so a smoke column far away sits in the haze
    // like everything else rather than glowing through it.
    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    return float4(lerp(color, FogColor.rgb, fogAmount), opacity);
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
