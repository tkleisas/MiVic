using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Loads the compiled effects as files.
/// <para>
/// The effects are compiled by the build's own ShadowDusk target — HLSL through DXC and
/// SPIRV-Cross straight to GLSL, natively on every desktop platform, no Wine and no
/// Windows SDK anywhere — and land next to their sources as <c>.mgfx</c> files the content
/// builder copies. A compiled effect is a container the <see cref="Effect"/> constructor
/// parses directly, so the pipeline's XNB wrapper is not part of the read.
/// </para>
/// </summary>
public static class EffectFile
{
    /// <summary>Parses a compiled effect out of the content tree.</summary>
    public static Effect Load(GraphicsDevice device, string name)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        using System.IO.Stream stream = TitleContainer.OpenStream($"Content/Shaders/{name}.mgfx")
            ?? throw new System.IO.FileNotFoundException($"Content/Shaders/{name}.mgfx is not in the content tree.");

        using var memory = new System.IO.MemoryStream();
        stream.CopyTo(memory);

        return new Effect(device, memory.ToArray());
    }
}
