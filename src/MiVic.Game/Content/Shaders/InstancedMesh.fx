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

/// <summary>
/// The colour of molten rock, as a function of where it is and when it is.
///
/// Shared by the lava surface technique and by the terrain technique, because lava has
/// to be drawn by whichever of them owns the cells — and on a volcano's slopes it is the
/// terrain, whose mesh follows the ground. A flat surface mesh sampled at the height of
/// its cell's centre is buried by any slope that rises more than a few centimetres across
/// nine metres, which is every slope a lava flow has ever run down.
///
/// Emissive, and deliberately unlit: lava is a light source, and molten rock that dims at
/// dusk is not molten.
/// </summary>
float3 LavaGlow(float2 p, float3 material)
{
    // A slow crust drifting over faster veins: the crust cools and darkens in plates,
    // and the cracks between them are where the heat shows.
    float crust = 0.5 + (0.5 * sin((p.x * 0.055) + (Time * 0.22)) * cos((p.y * 0.071) - (Time * 0.18)));
    float veins = 0.5 + (0.5 * sin((p.x * 0.42) + (p.y * 0.36) + (Time * 0.75)));

    // The heat has to be generous. Narrow bright veins over a dark crust is what lava
    // looks like from a few metres away, and it is what this first did — but the veins
    // are fifteen metres apart, so from a camera two hundred metres up they average
    // into the crust and a volcano's crater reads as a dull rust stain. Measured rather
    // than guessed: the first version averaged a heat of 0.41, squared to 0.17, which is
    // five sixths cold rock.
    float heat = saturate((crust * 0.85) + (veins * 0.75) - 0.05);

    float3 crustColor = material * 0.55;
    float3 molten = float3(1.00, 0.42, 0.07);

    // Still squared, so the crust keeps its dark plates and the cracks between them stay
    // the brightest thing on the surface.
    float3 color = lerp(crustColor, molten, heat * heat);

    // The hottest cores glow past their own colour, so lava reads as a light source
    // against the rock rather than as a stain on it. This is the whole reason it is drawn
    // unlit: nothing else on the ground gets to be brighter than the sun.
    return color + (molten * pow(heat, 4.0) * 0.45);
}

float4 LavaPS(VertexOutput input) : COLOR0
{
    // Emissive means it keeps its own brightness, but haze still applies.
    return float4(
        ApplyFog(LavaGlow(input.WorldPos.xz, input.Material.rgb), input.WorldPos),
        input.Material.a * input.Tint.a);
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

// -----------------------------------------------------------------------------
// Terrain: a treatment per ground surface, chosen per pixel.
//
// Grass, mud, sand, snow, rock, ore and woodland floor are the same thing to the
// rest of the pipeline — a terrain vertex colour out of TerrainMeshBuilder.SurfaceColor
// — and until now one lit pass shaded all of them, which is why the map read as
// painted swatches seen from above. Each of those surfaces behaves differently
// under wind and light, and this is where that difference lives.
//
// A surface id in a vertex channel was the alternative and it does not work here.
// The alpha channel is already the faction paint mask, and the others carry
// position and normal. Worse, an id stored per vertex *interpolates* across a cell
// boundary — this pipeline compiles to vs_3_0/ps_3_0 and has no flat interpolation
// — so a pixel on the edge of a marsh would be 0.4 grass and 0.6 mud and would
// have to be blended anyway. Classifying the colour the vertex already carries is
// that same blend for less, and it inherits the builder's own tints for free: a
// slope already lerped towards rock gets the rock treatment, and a track worn
// towards mud gets the wet-mud one.
//
// The classification is done on chromaticity — colour divided by its own
// brightness — rather than on raw colour. The builder shades every vertex by slope,
// so a steep rock face arrives as (87,83,73) rather than as the palette's
// (112,106,94), and in raw terms that shaded rock is closer to the ore grey than to
// rock: a cliff would have been classified as a deposit. Dividing by brightness
// removes the shade and leaves the hue, which is what identifies a surface.
//
// Brightness comes back into it for the two treatments that are points of light
// rather than fields. Rock and ore are the same hue at different brightnesses, and
// the builder lerps rock towards snow with altitude, which carries a volcano's grey
// straight through the ore colour on the way: stone that has been tinted pale is not
// a deposit, and the test for a sparkle says so. See <see cref="PointMatch"/>.
//
// Every treatment is a function of world position, never of screen position, so
// nothing here swims when the camera moves. The fast, fine ones are faded out with
// distance before they can alias into a shimmer at strategic zoom.
// -----------------------------------------------------------------------------

/// <summary>
/// The ground palette, byte for byte what <c>TerrainMeshBuilder.SurfaceColor</c>
/// bakes into the terrain mesh. A colour changed there and not here shows up as a
/// treatment on the wrong ground — a visible fault rather than a silent one.
/// </summary>
static const float3 GrassColor = float3(74, 98, 56);
static const float3 MudColor = float3(84, 62, 42);
static const float3 SandColor = float3(198, 180, 126);
static const float3 SnowColor = float3(228, 232, 238);
static const float3 RockColor = float3(112, 106, 94);
static const float3 ShallowWaterColor = float3(76, 122, 146);
static const float3 DeepWaterColor = float3(38, 68, 108);
static const float3 LavaColor = float3(196, 74, 28);
static const float3 MineColor = float3(92, 88, 82);
static const float3 ForestColor = float3(40, 66, 38);

/// <summary>
/// How strongly one palette entry describes a pixel, as an inverse square in
/// chromaticity space.
///
/// Nine distance tests per pixel is nothing, and the chromaticity — colour divided
/// by its own brightness — is what survives the builder's slope shade, which arrives
/// at a pixel as a multiplier on all three channels at once. It is also what makes
/// the test independent of the scale the two colours are written in, which is just
/// as well: the palette below is in the bytes the mesh builder bakes, and the
/// vertex attribute arrives normalised to 0..1.
///
/// Squared, so an entry wins outright rather than only mostly: at the palette's own
/// spacing a surface takes more than nine tenths of a pixel's weight, and the field
/// between two entries — which is exactly what the mesh interpolates along a border
/// — crosses over in the middle. The divisor keeps a pixel that matches an entry
/// exactly from dividing by zero, and is small enough to leave eight-bit
/// quantisation unable to move a pixel's classification.
/// </summary>
float SurfaceWeight(float3 chroma, float3 palette)
{
    palette /= max(palette.r + palette.g + palette.b, 1.0);
    float3 d = chroma - palette;

    return 1.0 / (dot(d, d) + 0.00001);
}

/// <summary>How far a pixel's chromaticity sits from an entry, read back out of its weight.</summary>
float SurfaceDistance(float weight)
{
    return sqrt(max((1.0 / weight) - 0.00001, 0.0));
}

/// <summary>
/// How well a pixel really is one of the two surfaces that carry points of light.
///
/// A point of light reads as an object sitting on the ground rather than as a shade
/// of it, so it is held to a stricter test than a wave is — and it needs one, because
/// the builder's own tints move pixels a long way from the entry they belong to. Rock
/// high on a volcano is lerped towards snow until its chromaticity is nearer the ore
/// grey than to rock, and grass a third of the way up towards the snow line arrives
/// as a pale green-grey that is neither. The first case is a mountain covered in ore
/// glints and the second a green hillside covered in white ones, and both read as
/// dust. Requiring the brightness as well as the hue puts the points back where the
/// ground really is that colour: ore is dark and snow is nearly white, so a pale
/// rock face fails on brightness even when it passes on hue.
///
/// The tolerance on brightness is relative to the entry, because the builder's shade
/// is a multiplier: a fifth off a dark grey and a fifth off white are the same fault.
/// </summary>
float PointMatch(float distance, float luma, float3 palette)
{
    float entryLuma = dot(palette, float3(0.299, 0.587, 0.114)) / 255.0;

    return saturate(1.0 - (distance / 0.012) - (abs(luma - entryLuma) / (0.55 * entryLuma)));
}

/// <summary>
/// One pseudo-random value per world-space lattice cell, stable for all time.
///
/// Taken from the cell's index rather than from the pixel, so a sparkle belongs to
/// a place on the map: it keeps its position while the camera moves, which a value
/// hashed from screen coordinates could not do.
/// </summary>
float LatticeHash(float2 cell, float salt)
{
    return frac(sin(dot(cell, float2(12.9898, 78.233)) + salt) * 43758.5453);
}

/// <summary>
/// Sparse bright points on a world-space lattice, for snow and for ore.
///
/// <paramref name="rarity"/> is the share of cells that carry a point at all, and
/// it is what separates a sparkle from static: an even glitter over every cell of
/// snow reads as television noise at any distance, while one point in forty reads
/// as a facet catching the sun. Each point twinkles on its own phase, taken from its
/// cell, so a field of them never pulses together, and the twinkle is squared so that
/// most points stay faint and a few are bright.
/// </summary>
float SparsePoints(
    float2 position,
    float cellSize,
    float rarity,
    float radius,
    float salt,
    float rate)
{
    float2 cell = floor(position / cellSize);
    float present = step(rarity, LatticeHash(cell, salt));

    float2 centre = (cell + float2(LatticeHash(cell, salt + 1.7), LatticeHash(cell, salt + 4.3))) * cellSize;
    float falloff = saturate(1.0 - (length(position - centre) / radius));
    float phase = LatticeHash(cell, salt + 7.1);
    float twinkle = saturate(0.35 + (0.65 * sin((Time * rate) + (phase * 6.2832))));

    // Squared, so most of the points on a field are faint and a few are bright. Points
    // of even brightness across a whole surface are what makes a sparkle read as
    // television static; a field where a few catch the sun reads as snow.
    return present * falloff * falloff * twinkle * twinkle;
}

/// <summary>
/// Master gain on every ground treatment. 1 is the shipped look; it exists so a
/// control render — the same frame with the treatments off — can be taken without
/// touching anything else in the scene.
/// </summary>
static const float TreatmentScale = 1.0;

float4 TerrainPS(VertexOutput input) : COLOR0
{
    float3 normal = normalize(input.Normal);
    float3 base = SurfaceColor(input.Material, input.Tint.rgb);

    // The lit technique's own lighting, unchanged: the treatments go on top of the
    // light rather than instead of it, so ground still reads as terrain rather than
    // as a pattern.
    float hemi = saturate((normal.y * 0.5) + 0.5);
    float3 ambient = AmbientColor.rgb * lerp(0.58, 1.30, hemi);
    float lambert = saturate(dot(normal, LightDirection));
    float wrap = (lambert * 0.6) + 0.4;

    // The vertex colour arrives normalised to 0..1, and the palette below and the
    // luminance the point treatments are held to are both written in the bytes
    // TerrainMeshBuilder bakes. Bringing it up to that scale once, here, is what keeps
    // the two comparable — and the chromaticity comparable as well, since dividing by
    // a sum that has been clamped at 1.0 is not a chromaticity at all.
    float3 material = input.Material.rgb * 255.0;

    float3 chroma = material / max(material.r + material.g + material.b, 1.0);
    float luma = dot(material, float3(0.299, 0.587, 0.114)) / 255.0;

    float wGrass = SurfaceWeight(chroma, GrassColor);
    float wMud = SurfaceWeight(chroma, MudColor);
    float wSand = SurfaceWeight(chroma, SandColor);
    float wSnow = SurfaceWeight(chroma, SnowColor);
    float wRock = SurfaceWeight(chroma, RockColor);
    float wMine = SurfaceWeight(chroma, MineColor);
    float wForest = SurfaceWeight(chroma, ForestColor);

    // The two liquid beds are in the table so that a lake floor is not mistaken for
    // mud or grass and given a treatment it is about to be covered by anyway. They
    // take no treatment of their own: water and lava are drawn over them by their
    // own techniques.
    float wShallow = SurfaceWeight(chroma, ShallowWaterColor);
    float wDeep = SurfaceWeight(chroma, DeepWaterColor);
    float wLava = SurfaceWeight(chroma, LavaColor);

    float scale = 1.0 / (wGrass + wMud + wSand + wSnow + wRock + wMine + wForest + wShallow + wDeep + wLava);

    float grass = wGrass * scale;
    float mud = wMud * scale;
    float sand = wSand * scale;
    float snow = wSnow * scale;
    float rock = wRock * scale;
    float mine = wMine * scale;
    float forest = wForest * scale;

    // Rock and ore share the stone treatment, and are separated only by the glint.
    // Ore is rock with metal in it, and the ground that most needs to read as layered
    // rock is the ground the builder's altitude tint has pushed towards the ore grey.
    float stone = rock + mine;

    // The two point treatments, held to the stricter test described above.
    float snowMatch = PointMatch(SurfaceDistance(wSnow), luma, SnowColor);
    float mineMatch = PointMatch(SurfaceDistance(wMine), luma, MineColor);

    float2 p = input.WorldPos.xz;
    float distanceToCamera = length(input.WorldPos - CameraPosition);

    // How much of the fine detail this pixel can still hold. A five-metre ripple is
    // nine pixels at seven hundred metres, and fewer than that looking along the
    // ground from a shallow pitch, where a pixel's footprint is metres wide — and a
    // pattern finer than the pixels sampling it is not detail, it is aliasing. The
    // fine treatments fade with distance rather than turning the far half of the map
    // into a shimmer.
    float detail = saturate(1.0 - ((distanceToCamera - 180.0) / 380.0));

    // Grass: a gust crossing open ground, which is most of the map, so this is the
    // strongest of the treatments. It travels along the bearing the trees already
    // lean on in FoliageVS, because ground and wood disagreeing about the wind is
    // the kind of detail that reads as a bug.
    float2 wind = float2(0.82, 0.57);
    float along = dot(p, wind);
    float across = dot(p, float2(-wind.y, wind.x));

    // Two waves on that bearing at the same speed: a long gust, seventy metres from
    // crest to crest, which is the one a strategic camera sees, and a short ripple
    // riding inside it, which is the one the ground shows. Both would be one scale of
    // nothing on its own — a field seen from seven hundred metres is inside a single
    // band of a twenty-metre wave, and a field seen from thirty metres is inside a
    // single band of a seventy-metre one.
    float gust = sin((along * 0.09) - (Time * 0.85));
    float ripple = sin((along * 0.27) - (Time * 2.55));

    // A gust is not a wave train: the envelope breaks it into fronts with still air
    // between them, so the field pulses rather than rippling evenly.
    float envelope = 0.55 + (0.45 * sin((across * 0.052) + (Time * 0.21)));

    // Crop rows, running with the wind and bending under the gust that crosses them.
    // Fine enough to be gone by the time a pixel covers one, which is what the
    // distance fade is for.
    float rows = sin((across * 0.55) + (gust * 0.6));
    float grassWave = (gust * envelope * 0.16) + (ripple * 0.07) + (rows * 0.06 * detail);

    // Woodland floor: dappled shade, patchy and still. It does not move because the
    // trees do — a wood whose floor shifted under a fixed canopy would be a light
    // show, and the movement in a wood should come from the wood. Crossed pairs of
    // sines give patches at three scales, which is what a canopy's gaps look like: a
    // break in the crown, smaller gaps inside it, and small ones inside those.
    float dapple = (0.45 * ((sin((p.x * 0.079) + 1.3) * sin((p.y * 0.061) - 0.7)) +
        (0.55 * sin((p.x * 0.028) - 2.2) * sin((p.y * 0.037) + 0.5)))) +
        (0.30 * sin((p.x * 0.23) + 0.7) * sin((p.y * 0.19) - 1.9));

    // Mud: a wet sheen that shifts as the film drains and pools, and darker where it
    // has gathered. The sheen is the sun on a surface that is not flat, so it takes a
    // normal whose tilt follows the damp field — the same trick the water plays with
    // its waves, at a fraction of the slope, because a puddle is a millimetre of water
    // and not a swell.
    float damp = sin((p.x * 0.21) - (Time * 0.42)) * cos((p.y * 0.17) + (Time * 0.33));
    float2 dampSlope = float2(
        cos((p.x * 0.21) - (Time * 0.42)) * 0.21,
        sin((p.y * 0.17) + (Time * 0.33)) * 0.17);
    float3 sheenNormal = normalize(float3(-dampSlope.x * 3.5, 1.0, -dampSlope.y * 3.5));
    float3 toCamera = normalize(CameraPosition - input.WorldPos);
    float3 half = normalize(-LightDirection + toCamera);

    // Absolute alignment, like the water's glint: the sign convention of the light
    // vector is the lit shader's business, and a highlight that never happens
    // because of it is a highlight nobody notices is missing.
    float sheen = pow(saturate(abs(dot(sheenNormal, half))), 18.0);

    // Sand: fine ripples, low and fast. The second harmonic is what makes them
    // asymmetric — a ripple's windward face is longer than its lee — and it is why
    // this is a shaped wave rather than a sine.
    float warp = sin(p.y * 0.17) * 2.0;
    float ripples = 0.74 * (sin((p.x * 1.25) + warp) - (0.35 * sin((p.x * 2.5) + (warp * 2.0))));

    // Snow: points, not glitter, and cool ones. One cell in forty holds one, and the
    // lattice is half a metre across, which puts the points at the scale of a facet
    // of ice rather than of a field of them.
    float sparkle = SparsePoints(p, 0.55, 0.975, 0.26, 11.0, 1.8);

    // Ore: the same idea on a coarser lattice and a slower twinkle, warm rather than
    // white. A deposit is mineral in rock and should catch the eye from across a
    // valley without being a beacon that outshines the units beside it.
    float oreGlint = SparsePoints(p, 1.15, 0.94, 0.40, 43.0, 1.1);

    // Rock and ore: bedding planes, so a cliff reads as a layered face rather than a
    // grey wall. The banding follows height because beds are horizontal, and it is
    // warped by a slow function of the ground position so the bands are not a ruler
    // laid against the mountain. Static: geology is the one thing on this map with no
    // business moving.
    float bed = (input.WorldPos.y * 0.9) + (sin(p.x * 0.035) * 1.6) + (cos(p.y * 0.041) * 1.2);
    float band = sin(bed);

    // Shaped into long plateaus with quick edges, because a bedding plane is a hard
    // change from one rock to the next with a stretch of the same rock in between: a
    // plain sine reads as a soft gradient, and a soft gradient is a lighting effect
    // rather than geology.
    float plateau = band / (0.35 + (0.65 * abs(band)));

    // And a narrow dark seam where two beds meet, which is what a joint in the rock
    // looks like from a distance. Its average is taken back out below, because a seam
    // is one-sided and would otherwise darken every cliff it is drawn on.
    float seam = pow(saturate(1.0 - abs(band)), 6.0);
    float seamAverage = 0.047;

    // Not every bed is as hard as the next, so the contrast between them varies slowly
    // with height too — which is also what stops a cliff reading as corduroy.
    float bedStrength = 0.72 + (0.28 * sin((bed * 0.31) + 2.3));

    float strata = (0.78 * plateau * bedStrength) - (0.55 * (seam - seamAverage));

    // Weighted towards faces that are actually exposed. A flat shelf of rock shows a
    // band as a flat wash of one bed's colour, which is a stain rather than strata.
    float face = saturate((1.0 - normal.y) * 2.4);

    // Every treatment is a modulation that averages to nothing over its own pattern,
    // so the ground does not gain or lose brightness overall: a tank has to stand out
    // against grass exactly as well as it did before there was any of this.
    float common = 1.0
        + (TreatmentScale * ((grass * grassWave)
        - (mud * damp * 0.18)
        + (sand * ripples * 0.08 * detail)
        + (stone * strata * (0.06 + (0.18 * face)))));

    // The wood is the one treatment that is not neutral in colour: its lit patches
    // are a touch greener than its shaded ones, which is what keeps dappled shade
    // from reading as a grey stain.
    float forestGain = TreatmentScale * forest * dapple;

    float3 gain = float3(
        common + (forestGain * 0.14),
        common + (forestGain * 0.17),
        common + (forestGain * 0.11));

    float3 color = base * (ambient + (wrap * 0.68)) * gain;

    // The three that add light rather than scale it. Kept to a small share of the
    // frame in every case: a sparkle that covers a tenth of the snow it falls on is
    // no longer a sparkle, and the ground must not gain brightness on average, or a
    // unit would stop standing out against it.
    color += float3(1.00, 0.97, 0.92) * TreatmentScale * mud * sheen * 0.32;
    color += float3(0.93, 0.97, 1.00) * TreatmentScale * snow * sparkle * 0.78 * detail * snowMatch;
    color += float3(1.00, 0.86, 0.50) * TreatmentScale * mine * oreGlint * 0.82 * detail * mineMatch;

    // Lava is the one surface that is not lit at all, so it is blended in rather than
    // modulated: it replaces the lit ground with molten rock, and it is drawn here because
    // this is the mesh that follows the slope a flow ran down. A separate flat surface
    // mesh is buried on any gradient, which is exactly what happened — the terrain was
    // painting dull interpolated rust over the top of it.
    float lavaWeight = saturate(wLava * scale);
    color = lerp(color, LavaGlow(input.WorldPos.xz, LavaColor / 255.0), lavaWeight);

    return float4(ApplyFog(color, input.WorldPos), input.Tint.a);
}

technique Terrain
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL TerrainPS();
    }
};

// -----------------------------------------------------------------------------
// Foliage: trees, the one thing on the map that moves without being told to.
//
// A wood is drawn as one instanced mesh per species, so the sway has to happen in
// the vertex shader: there is no geometry stage in this pipeline — DesktopGL
// compiles these as vs_3_0/ps_3_0 — and nothing per-tree is uploaded except its
// matrix. Moving the vertices is also the cheap answer, which is what matters when
// several hundred trees share one draw call.
//
// Two details decide whether this reads as wind or as jitter.
//
// The phase comes from the instance's own world position, so neighbouring trees
// are out of step. A wood that sways in unison is one object being shaken, and it
// is the single most obvious way this effect goes wrong.
//
// And the sway is weighted by height above the tree's own base, so the trunk
// stays planted and only the canopy carries the movement. A tree displaced
// uniformly slides across the grass with its roots trailing, which is worse than
// no wind at all.
// -----------------------------------------------------------------------------

/// <summary>
/// Height of the mesh being drawn, in metres, so the sway can be weighted by it.
/// Set per mesh: the trees range from seven metres to ten, and a fixed divisor
/// would have the tall ones swaying from halfway up their trunks.
/// </summary>
float FoliageHeight;

VertexOutput FoliageVS(VertexInput input, InstanceInput instance)
{
    float4x4 world = float4x4(instance.Row0, instance.Row1, instance.Row2, instance.Row3);
    float4 worldPosition = mul(input.Position, world);

    // How far up its own tree this vertex is, 0 at the root and 1 at the crown.
    // The tree meshes are grounded on import — their lowest point sits at y = 0 —
    // so the local y is already the height above the base.
    float reach = saturate(input.Position.y / max(FoliageHeight, 0.001));

    // Squared, because a tree bends: the base of the trunk moves by almost
    // nothing, the crown moves by the whole amplitude, and the curve between them
    // is not a straight line.
    float weight = reach * reach;

    // Phase from where this tree is standing. Two trees a few metres apart must
    // not start their gust together, and the wood has to look like a wood and not
    // like a field of metronomes.
    float phase = (instance.Row3.x * 0.19) + (instance.Row3.z * 0.27);

    // A slow lean with a faster flutter over it. The lean alone reads as a
    // metronome and the flutter alone reads as vibration: air is both.
    float lean = sin((Time * 0.85) + phase);
    float flutter = sin((Time * 2.35) + (phase * 1.7));

    // One wind direction for the whole map, so the wood leans together the way a
    // real one does, plus a small cross-wind component so it is not a wobble in a
    // single line. The amplitudes are the movement at the very top of a tree:
    // thirty-odd centimetres of lean and a hand's width of flutter.
    float2 along = float2(0.82, 0.57);
    float2 sway = (along * lean * 0.34) + (float2(-along.y, along.x) * flutter * 0.11);

    worldPosition.xyz += float3(sway.x * weight, 0.0, sway.y * weight);

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
/// Trees are lit exactly as anything else is, with one difference: the instance
/// tint multiplies the material instead of being mixed into it by the paint mask.
///
/// A tree belongs to nobody, so every one of its materials has a mask of zero —
/// the value that tells the lit shader to leave the material alone. That is what
/// keeps a wood from being repainted in whichever team's colour happens to be
/// drawing it, and it is also why the lit technique cannot give one tree a
/// different green from its neighbour. Here the tint *is* the variation: the
/// client hands each tree a slightly different shade, and a wood stops being one
/// flat colour repeated three hundred times.
/// </summary>
float4 FoliagePS(VertexOutput input) : COLOR0
{
    float3 normal = normalize(input.Normal);
    float3 base = input.Material.rgb * input.Tint.rgb;

    float hemi = saturate((normal.y * 0.5) + 0.5);
    float3 ambient = AmbientColor.rgb * lerp(0.58, 1.30, hemi);

    float lambert = saturate(dot(normal, LightDirection));
    float wrap = (lambert * 0.6) + 0.4;
    float3 lit = base * (ambient + (wrap * 0.68));

    float distanceToCamera = length(input.WorldPos - CameraPosition);
    float fogRange = max(FogEnd - FogStart, 0.001);
    float fogAmount = saturate((distanceToCamera - FogStart) / fogRange) * FogColor.a;

    return float4(lerp(lit, FogColor.rgb, fogAmount), input.Tint.a);
}

technique Foliage
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL FoliageVS();
        PixelShader  = compile PS_SHADERMODEL FoliagePS();
    }
};

// -----------------------------------------------------------------------------
// Ghosts: the footprint of something the player is about to place.
//
// Deliberately unlit and deliberately flat. A footprint is a diagram of the ground about
// to be taken, and lighting it would make it read as a thing already standing there —
// which is exactly the wrong signal from a preview the player has not committed to. The
// colour and the opacity both come from the instance tint, so one mesh is a green promise
// or a red refusal without being rebuilt, and fog still applies so a ghost a long way off
// sits in the haze like everything else rather than glowing through it.
//
// It sits at the end of the file because it is the only technique that borrows ApplyFog
// from the liquid section above it.
// -----------------------------------------------------------------------------

float4 GhostPS(VertexOutput input) : COLOR0
{
    float3 color = input.Material.rgb * input.Tint.rgb;

    return float4(ApplyFog(color, input.WorldPos), input.Material.a * input.Tint.a);
}

technique Ghost
{
    pass P0
    {
        VertexShader = compile VS_SHADERMODEL MainVS();
        PixelShader  = compile PS_SHADERMODEL GhostPS();
    }
};
