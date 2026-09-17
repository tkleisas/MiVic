using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Cutscene.Skinning;

public sealed class SkinnedModelInspector : Microsoft.Xna.Framework.Game
{
    private readonly string _directory;
    private readonly GraphicsDeviceManager _graphics;
    private int _exitCode;

    public SkinnedModelInspector(string directory)
    {
        _directory = directory;
        _graphics = new GraphicsDeviceManager(this);
        IsMouseVisible = false;
        Window.AllowUserResizing = false;
    }

    /// <summary>What the report concluded, once the hidden window has closed.</summary>
    public int ExitCode => _exitCode;

    protected override void LoadContent()
    {
        _exitCode = Report(GraphicsDevice);
        Exit();
    }

    private int Report(GraphicsDevice device)
    {
        if (!Directory.Exists(_directory))
        {
            Console.Error.WriteLine($"no _directory at {_directory}");
            return 2;
        }

        string[] files = Directory.GetFiles(_directory, "*.glb");
        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        if (files.Length == 0)
        {
            Console.Error.WriteLine($"no .glb files in {_directory}");
            return 2;
        }

        Console.WriteLine($"device: {GraphicsAdapter.DefaultAdapter.Description}");

        int failures = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            try
            {
                SkinnedModel model = SkinnedModel.Load(device, file);

                Console.WriteLine();
                Console.WriteLine($"{name}");
                Console.WriteLine($"  nodes      {model.NodeCount}");
                Console.WriteLine($"  joints     {model.JointCount}"
                                  + (model.JointCount > 72 ? "  OVER THE 72-BONE LIMIT" : string.Empty));
                Console.WriteLine($"  parts      {model.Parts.Count}");

                var names = new List<string>();
                foreach (SkinnedMeshPart part in model.Parts)
                {
                    // The texture is named too, and measured rather than described: a part
                    // that renders the wrong colour is either holding the wrong texture or
                    // lighting the right one wrongly, and those are different bugs. Size and
                    // mean texel tell the two apart without opening a renderer.
                    names.Add($"{part.Name}({part.PrimitiveCount * 3}v, {Describe(part.Texture)})");
                }

                Console.WriteLine($"             {string.Join(", ", names)}");

                int total = 0;
                foreach (SkinnedMeshPart part in model.Parts)
                {
                    total += part.PrimitiveCount;
                }

                Console.WriteLine($"  triangles  {total}");
                Console.WriteLine($"  clips      {model.Clips.Count}");
                foreach (AnimationClip clip in model.Clips)
                {
                    Console.WriteLine($"             {clip.Name} ({clip.Duration:0.00}s, "
                                      + $"{clip.Channels.Count} channels)");
                }

                if (model.JointCount > 72)
                {
                    failures++;
                }
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"{name}: FAILED — {error.GetType().Name}: {error.Message}");
                failures++;
            }
        }

        Console.WriteLine();
        Console.WriteLine(failures == 0
            ? $"{files.Length} model(s) loaded"
            : $"{failures} of {files.Length} failed");

        Console.Out.Flush();
        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// A texture as `<width>x<height> mean r,g,b`, or `null`.
    /// <para>
    /// The mean is read back off the GPU rather than described, because the question it
    /// answers — "is the dark texture dark when it gets here?" — is about the bytes that
    /// arrived, and every attempt to answer it from the file instead has been wrong.
    /// </para>
    /// </summary>
    private static string Describe(Texture2D texture)
    {
        if (texture == null)
        {
            return "null";
        }

        var sample = new Microsoft.Xna.Framework.Color[texture.Width * texture.Height];
        texture.GetData(sample);

        long r = 0, g = 0, b = 0;
        foreach (Microsoft.Xna.Framework.Color colour in sample)
        {
            r += colour.R;
            g += colour.G;
            b += colour.B;
        }

        long count = sample.Length;
        return $"{texture.Width}x{texture.Height} mean {r / count},{g / count},{b / count}";
    }
}
