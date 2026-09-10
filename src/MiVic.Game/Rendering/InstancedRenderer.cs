using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Draws many copies of a mesh in a single draw call.
/// <para>
/// This is the component that decides whether a full-3D RTS is affordable: draw
/// calls scale with the number of distinct meshes on screen, not with the number
/// of units. All units of a kind are gathered into one instance array and issued
/// as one <see cref="GraphicsDevice.DrawInstancedPrimitives(PrimitiveType, int, int, int, int)"/>.
/// </para>
/// </summary>
public sealed class InstancedRenderer : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private readonly EffectParameter _viewProjectionParameter;
    private readonly EffectParameter _lightDirectionParameter;
    private readonly EffectParameter _ambientColorParameter;
    private readonly EffectParameter _fogColorParameter;
    private readonly EffectParameter _cameraPositionParameter;
    private readonly EffectParameter _fogStartParameter;
    private readonly EffectParameter _fogEndParameter;

    private DynamicVertexBuffer? _instanceBuffer;
    private int _instanceCapacity;

    public InstancedRenderer(GraphicsDevice device, ContentManager content)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(content);

        _device = device;
        _effect = content.Load<Effect>("Shaders/InstancedMesh");

        _viewProjectionParameter = _effect.Parameters["ViewProjection"]
            ?? throw new InvalidOperationException("Shader is missing the ViewProjection parameter.");
        _lightDirectionParameter = _effect.Parameters["LightDirection"];
        _ambientColorParameter = _effect.Parameters["AmbientColor"];
        _fogColorParameter = _effect.Parameters["FogColor"];
        _cameraPositionParameter = _effect.Parameters["CameraPosition"];
        _fogStartParameter = _effect.Parameters["FogStart"];
        _fogEndParameter = _effect.Parameters["FogEnd"];
    }

    /// <summary>A mesh uploaded to the GPU, with its draw parameters cached.</summary>
    public sealed class Mesh : IDisposable
    {
        internal Mesh(VertexBuffer vertices, IndexBuffer indices, int primitiveCount)
        {
            VertexBuffer = vertices;
            IndexBuffer = indices;
            PrimitiveCount = primitiveCount;
        }

        internal VertexBuffer VertexBuffer { get; }

        internal IndexBuffer IndexBuffer { get; }

        /// <summary>Number of triangles in the mesh.</summary>
        public int PrimitiveCount { get; }

        public void Dispose()
        {
            VertexBuffer.Dispose();
            IndexBuffer.Dispose();
        }
    }

    /// <summary>Lighting and fog settings applied to every draw in a pass.</summary>
    public readonly record struct Environment(
        Vector3 LightDirection,
        Color AmbientColor,
        Color FogColor,
        float FogStart,
        float FogEnd);

    /// <summary>Which blend mode the unlit particle pass uses.</summary>
    public enum ParticleBlend
    {
        /// <summary>Straight alpha: smoke, dust and debris.</summary>
        Alpha = 0,

        /// <summary>Additive: sparks and muzzle flashes, which add light.</summary>
        Additive = 1,
    }

    /// <summary>
    /// Which unlit shader an unlit pass draws with. They are not interchangeable: a
    /// billboard is shaded radially from its own coordinates, a blast body from which
    /// way each facet faces the camera, and a tracer along its length.
    /// </summary>
    public enum ParticlePass
    {
        /// <summary>Camera-facing cards: smoke, dust, sparks, fire, debris.</summary>
        Billboard = 0,

        /// <summary>Spheres with a surface: the body of a detonation.</summary>
        Blast = 1,

        /// <summary>Streaks along their length: rounds in flight.</summary>
        Tracer = 2,
    }

    private ParticleBlend _particleBlend = ParticleBlend.Alpha;
    private bool _particles;

    /// <summary>Uploads a CPU mesh and returns a handle that must be disposed.</summary>
    public Mesh CreateMesh(MeshData data)
    {
        (VertexBuffer vertices, IndexBuffer indices) = data.Upload(_device);
        return new Mesh(vertices, indices, data.PrimitiveCount);
    }

    /// <summary>Begins a render pass for the given camera.</summary>
    public void Begin(in Matrix view, in Matrix projection, Vector3 cameraPosition, in Environment environment)
    {
        _effect.Parameters["ViewProjection"].SetValue(view * projection);
        _lightDirectionParameter?.SetValue(Vector3.Normalize(environment.LightDirection));
        _ambientColorParameter?.SetValue(environment.AmbientColor.ToVector4());
        _fogColorParameter?.SetValue(environment.FogColor.ToVector4());
        _cameraPositionParameter?.SetValue(cameraPosition);
        _fogStartParameter?.SetValue(environment.FogStart);
        _fogEndParameter?.SetValue(environment.FogEnd);

        _particles = false;
        _effect.CurrentTechnique = _effect.Techniques["Instanced"];
        _effect.CurrentTechnique.Passes[0].Apply();
    }

    /// <summary>
    /// Switches to the unlit particle technique. The caller must call
    /// <see cref="Begin"/> first; <see cref="EndParticles"/> switches back.
    /// </summary>
    /// <param name="blend">How the pass composites.</param>
    /// <param name="pass">
    /// Which unlit shader to use. The three are not interchangeable: a billboard is
    /// shaded radially from its own coordinates, a blast body from which way each
    /// facet faces the camera, and a tracer along its length. Drawing one with
    /// another's shader is how a round ends up invisible — a cube has no vertex near
    /// the middle of a face, so a radial falloff is zero across all of it.
    /// </param>
    public void BeginParticles(ParticleBlend blend, ParticlePass pass = ParticlePass.Billboard)
    {
        _particles = true;
        _particleBlend = blend;
        _effect.CurrentTechnique = _effect.Techniques[pass switch
        {
            ParticlePass.Blast => "Blast",
            ParticlePass.Tracer => "Tracer",
            _ => "Particles",
        }];

        _effect.CurrentTechnique.Passes[0].Apply();
    }

    /// <summary>Returns to the lit technique.</summary>
    public void EndParticles()
    {
        _particles = false;
        _effect.CurrentTechnique = _effect.Techniques["Instanced"];
        _effect.CurrentTechnique.Passes[0].Apply();
    }

    /// <summary>Issues one instanced draw call for <paramref name="count"/> copies of a mesh.</summary>
    public void Draw(Mesh mesh, InstanceData[] instances, int count)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        ArgumentNullException.ThrowIfNull(instances);

        if (count <= 0)
        {
            return;
        }

        if (count > instances.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Count exceeds the instance array length.");
        }

        EnsureInstanceCapacity(count);

        DynamicVertexBuffer buffer = _instanceBuffer!;
        buffer.SetData(instances, 0, count, SetDataOptions.Discard);

        BlendState? previous = null;
        RasterizerState? previousRasterizer = null;

        if (_particles)
        {
            // The shader returns unmultiplied colour, so the alpha pass is
            // NonPremultiplied rather than MonoGame's premultiplied AlphaBlend.
            previous = _device.BlendState;
            previousRasterizer = _device.RasterizerState;
            _device.BlendState = _particleBlend == ParticleBlend.Additive
                ? BlendState.Additive
                : BlendState.NonPremultiplied;

            // Billboards are single-sided and their winding depends on the
            // camera basis, so culling them would make half of them vanish.
            _device.RasterizerState = RasterizerState.CullNone;
        }

        _device.SetVertexBuffers(
            new VertexBufferBinding(mesh.VertexBuffer, 0, 0),
            new VertexBufferBinding(buffer, 0, 1));
        _device.Indices = mesh.IndexBuffer;

        _device.DrawInstancedPrimitives(
            PrimitiveType.TriangleList,
            baseVertex: 0,
            startIndex: 0,
            primitiveCount: mesh.PrimitiveCount,
            instanceCount: count);

        if (previous is not null)
        {
            _device.BlendState = previous;
        }

        if (previousRasterizer is not null)
        {
            _device.RasterizerState = previousRasterizer;
        }
    }

    /// <summary>Ends a render pass and drops the index binding.</summary>
    public void End()
    {
        // MonoGame rejects a null VertexBufferBinding, so the vertex streams are
        // left bound; every Draw rebinds what it needs before submitting.
        _device.Indices = null;
    }

    private void EnsureInstanceCapacity(int count)
    {
        if (_instanceBuffer is not null && count <= _instanceCapacity)
        {
            return;
        }

        int capacity = Math.Max(count, Math.Max(_instanceCapacity * 2, 1024));
        _instanceBuffer?.Dispose();
        _instanceBuffer = new DynamicVertexBuffer(
            _device,
            InstanceData.Declaration,
            capacity,
            BufferUsage.WriteOnly);
        _instanceCapacity = capacity;
    }

    public void Dispose()
    {
        _instanceBuffer?.Dispose();
        _effect.Dispose();
    }
}
