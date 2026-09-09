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
// MiVic fog of war overlay.
//
// The overlay is a copy of the terrain surface, textured by the team's
// visibility mask: red is how much fog covers the ground, green is how much of
// it the team remembers. Sampling with linear filtering is the whole trick — it
// turns a cell-sized grid of on/off visibility into a soft fog boundary, so the
// player never sees the lattice the simulation reasons about.
// -----------------------------------------------------------------------------

float4x4 ViewProjection;
texture2D VisibilityMask;
float4 RememberedTint;
float4 UnknownTint;

sampler2D MaskSampler = sampler_state
{
    Texture = <VisibilityMask>;
    MinFilter = Linear;
    MagFilter = Linear;
    MipFilter = Linear;
    AddressU = Clamp;
    AddressV = Clamp;
};

struct VertexInput
{
    float4 Position : POSITION0;
    float2 TexCoord : TEXCOORD0;
};

struct VertexOutput
{
    float4 Position : SV_POSITION;
    float2 TexCoord : TEXCOORD0;
};

VertexOutput MainVS(VertexInput input)
{
    VertexOutput output;
    output.Position = mul(input.Position, ViewProjection);
    output.TexCoord = input.TexCoord;
    return output;
}

float4 MainPS(VertexOutput input) : COLOR0
{
    float4 mask = tex2D(MaskSampler, input.TexCoord);

    // Green chooses the tint: remembered ground keeps a blue-grey cast so it
    // still reads as terrain, while ground never seen goes to near black.
    float3 tint = lerp(UnknownTint.rgb, RememberedTint.rgb, saturate(mask.g));

    // Red is the blend weight, so fully visible ground costs nothing: the
    // fragment is discarded rather than blended with a zero alpha.
    float alpha = saturate(mask.r);
    clip(alpha - 0.004);

    return float4(tint, alpha);
}

technique FogOverlay
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL MainPS();
    }
};
