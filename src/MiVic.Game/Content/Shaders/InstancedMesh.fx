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

// Seconds since the client started. Presentation only: nothing that moves on this
// clock can reach the simulation, exactly like the particle system, which already
// integrates on frame time.
float Time;

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
    // its xy to work out how far a pixel is from the middle of the quad, which is what
    // turns a square into a puff of smoke; tracers use its z to fade along the length
    // of a round. Three components rather than two because a round is drawn with a
    // cube and there is no vertex at the middle of one of its faces.
    float3 Local    : TEXCOORD1;
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
    output.Local = input.Position.xyz;
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
    float opacity = input.Material.a * input.Tint.a * ParticleMask(input.Local.xy);

    // A hot particle is brighter in the middle and cooler at its edge, which is
    // what stops a spark being a flat dot and makes fire look like fire.
    float core = saturate(1.0 - (length(input.Local.xy) * 2.4));
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

// -----------------------------------------------------------------------------
// Tracers: a round in flight, drawn as a streak rather than as a card or a puff.
//
// It cannot use the billboard shader. That one computes a radial falloff from the
// mesh's own coordinates, which works because a quad has vertices running from its
// centre to its edge — but a round is drawn with a cube, and a cube has no vertex
// anywhere near the middle of a face. Every vertex of it is a corner, so the falloff
// is zero at all of them and interpolates to zero across the whole surface: every
// tracer, every flak round and every electric arc has been drawn completely
// transparent.
//
// This shader shades along the round's length instead, which is the direction a
// tracer actually varies in: solid through the middle, tapering at the two ends so
// it does not read as a bar with cut ends.
// -----------------------------------------------------------------------------

float4 TracerPS(VertexOutput input) : COLOR0
{
    float3 color = input.Material.rgb * input.Tint.rgb;

    float along = saturate(abs(input.Local.z) * 2.0);
    float taper = saturate(1.2 - along);

    // Hot in the middle of the streak as well as along it, so the core of a tracer is
    // brighter than its edges at the distance the game is actually played at.
    float3 finalColor = color * (0.9 + (0.6 * taper));
    float opacity = input.Material.a * input.Tint.a * taper;

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

technique Tracer
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL TracerPS();
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

// -----------------------------------------------------------------------------
// Liquids: water and lava, drawn as their own surfaces over the terrain mesh.
//
// They get their own mesh rather than a flag on the terrain's vertices because the
// vertex alpha is already spoken for — it is the faction paint mask, and borrowing it
// would let a team's colour bleed into the sea. Two surfaces, two techniques, and the
// terrain underneath keeps drawing the lake bed.
//
// Both are shaped by the clock rather than by a texture, which is the only way a
// generated-art project can have moving water: a texture would have to be authored,
// and this has nothing authored about it. The patterns are two sines crossing at
// different rates, which is cheap, tiles without seams, and never repeats visibly at
// the scale a river is seen from.
// -----------------------------------------------------------------------------

/// <summary>Applies distance fog, which liquids need as much as anything else.</summary>
float3 ApplyFog(float3 color, float3 worldPosition)
{
    float distanceToCamera = length(worldPosition - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    return lerp(color, FogColor.rgb, fogAmount);
}

float4 WaterPS(VertexOutput input) : COLOR0
{
    float3 base = input.Material.rgb * input.Tint.rgb;
    float2 p = input.WorldPos.xz;

    // A long swell and a shorter chop, on different axes and at different speeds, so
    // the surface never looks like it is sliding in one direction.
    float swell = sin((p.x * 0.085) + (Time * 0.85)) * cos((p.y * 0.105) - (Time * 0.62));
    float chop = sin((p.x * 0.34) - (Time * 1.9) + (p.y * 0.29)) * 0.5;

    // Strong enough to read from a strategic camera, where a river is a few hundred
    // pixels of screen and a five per cent variation is nothing at all.
    float3 color = base * (0.88 + (swell * 0.22) + (chop * 0.12));

    // A slope derived from the waves rather than a normal map. It only has to be
    // enough to catch a highlight and break the surface into facets.
    float2 slope = float2(
        cos((p.x * 0.085) + (Time * 0.85)) * 0.085,
        cos((p.y * 0.105) - (Time * 0.62)) * 0.105);

    float3 normal = normalize(float3(-slope.x * 6.0, 1.0, -slope.y * 6.0));
    float3 toCamera = normalize(CameraPosition - input.WorldPos);

    // Sky reflected at a grazing angle. An RTS camera looks at a river from a shallow
    // angle almost always, and flat blue that never changes is what makes water read
    // as a painted floor.
    float grazing = pow(1.0 - saturate(abs(dot(normal, toCamera))), 3.0);
    color += float3(0.16, 0.24, 0.30) * grazing;

    // The sun on the water, which is the one thing that says "liquid" more than any
    // colour does. Taken as an absolute alignment rather than a signed one: the light
    // vector's sign convention is the lit shader's business, and a reflection that
    // silently never happens because of it is a reflection nobody notices is missing.
    float3 half = normalize(-LightDirection + toCamera);
    float glint = pow(saturate(abs(dot(normal, half))), 24.0);
    color += float3(1.0, 0.97, 0.88) * glint * 0.65;

    return float4(ApplyFog(color, input.WorldPos), input.Material.a * input.Tint.a);
}

float4 LavaPS(VertexOutput input) : COLOR0
{
    // Emissive, and deliberately not lit: lava is a light source. Ambient and sun
    // would only make it dimmer, and molten rock that dims at dusk is not molten.
    float2 p = input.WorldPos.xz;

    // A slow crust drifting over faster veins: the crust cools and darkens in plates,
    // and the cracks between them are where the heat shows.
    float crust = 0.5 + (0.5 * sin((p.x * 0.055) + (Time * 0.22)) * cos((p.y * 0.071) - (Time * 0.18)));
    float veins = 0.5 + (0.5 * sin((p.x * 0.42) + (p.y * 0.36) + (Time * 0.75)));
    float heat = saturate((crust * 0.75) + (veins * veins * 0.55) - 0.10);

    float3 crustColor = input.Material.rgb * 0.55;
    float3 molten = float3(1.00, 0.42, 0.07);

    // Squared so the molten cracks stay narrow and the crust stays dark, which is what
    // lava actually looks like from above.
    float3 color = lerp(crustColor, molten, heat * heat);

    // Emissive means it keeps its own brightness, but haze still applies.
    return float4(ApplyFog(color, input.WorldPos), input.Material.a * input.Tint.a);
}

technique Water
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL WaterPS();
    }
};

technique Lava
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL LavaPS();
    }
};
