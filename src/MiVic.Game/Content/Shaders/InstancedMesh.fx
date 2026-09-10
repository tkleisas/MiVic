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

    // The vertex's own position inside its mesh, before any transform. Particles use
    // it to work out how far a pixel is from the middle of the quad, which is what
    // turns a square into a puff of smoke: the mesh is a unit quad, so its own
    // coordinates are the only radial coordinate the shader needs, and it needs
    // nothing added to the vertex format to get them.
    float2 Local    : TEXCOORD1;
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
    output.Local = input.Position.xy;
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
//
// What the pixel shader adds is a round edge. The mesh is a unit quad, so
// `Local` runs -0.5..0.5 across it and its length is a radial coordinate for
// free: without it every puff, every spark and every wisp of smoke is a square,
// and a battlefield is a field of small drifting squares.
// -----------------------------------------------------------------------------

/// <summary>
/// How opaque this pixel of a quad particle is, 1 in the middle and 0 at the rim.
///
/// Squared rather than linear: a linear ramp leaves a visible square edge at the
/// corners, because the distance to a corner is longer than to the middle of an
/// edge. Squaring pulls the falloff in so the shape reads as a disc.
/// </summary>
float ParticleMask(float2 local)
{
    float r = saturate(length(local) * 2.0);
    float soft = 1.0 - (r * r);

    return soft * soft * (1.0 + (0.35 * soft));
}

float4 ParticlePS(VertexOutput input) : COLOR0
{
    // Particles are unlit by design, and their mesh is plain white: the colour
    // and the fade both come from the per-particle instance tint.
    float3 color = input.Material.rgb * input.Tint.rgb;
    float opacity = input.Material.a * input.Tint.a * ParticleMask(input.Local);

    // A hot particle is brighter in the middle and cooler at its edge, which is
    // what stops a spark being a flat dot and makes fire look like fire.
    float core = saturate(1.0 - (length(input.Local) * 2.4));
    color *= 0.75 + (core * 0.75);

    // Distance fog still applies, so a smoke column far away sits in the haze
    // like everything else rather than glowing through it.
    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    return float4(lerp(color, FogColor.rgb, fogAmount), opacity);
}

// -----------------------------------------------------------------------------
// Blast bodies: the fireball of a large explosion, as a ball rather than a card.
//
// A billboard is the right shape for smoke, which is a soft thing with no
// surface. It is the wrong shape for the first instant of a detonation, which has
// a definite volume: drawn flat it reads as a rectangle of orange that grows. A
// low-poly sphere with this shading reads as something with a front and a back.
//
// Shaded additively from the interpolated normal, so the middle of the ball —
// where the surface faces the camera — is the hottest part and the rim, where it
// turns away, is cooler and dimmer. That is the opposite of a lit sphere, and it
// is what a ball of fire actually looks like.
// -----------------------------------------------------------------------------

float4 BlastPS(VertexOutput input) : COLOR0
{
    float3 normal = normalize(input.Normal);
    float3 toCamera = normalize(CameraPosition - input.WorldPos);
    float facing = saturate(dot(normal, toCamera));

    float3 color = input.Material.rgb * input.Tint.rgb;

    // Facets, not a smooth gradient: this is a low-poly ball on purpose, and
    // flattening the shading would only make it look like a bad sphere.
    float core = pow(facing, 1.35);
    float shell = pow(1.0 - facing, 2.0) * 0.45;

    float3 finalColor = color * (0.35 + (core * 1.15) + shell);
    float opacity = input.Material.a * input.Tint.a * (0.35 + (core * 0.8));

    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    return float4(lerp(finalColor, FogColor.rgb, fogAmount), opacity);
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

technique Blast
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL BlastPS();
    }
};
