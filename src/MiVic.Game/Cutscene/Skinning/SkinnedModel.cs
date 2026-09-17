using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MiVic.Game.Rendering;
using SharpGLTF.Schema2;
using Numerics = System.Numerics;

namespace MiVic.Game.Cutscene.Skinning
{
    /// <summary>
    /// Vertex format for GPU skinning via the built-in SkinnedEffect.
    /// Blend indices are stored as floats (BLENDINDICES), weights as floats (BLENDWEIGHT).
    /// </summary>
    public struct SkinnedVertex : IVertexType
    {
        public Vector3 Position;
        public Vector3 Normal;
        public Vector2 TextureCoordinate;
        public Vector4 BlendIndices;
        public Vector4 BlendWeight;

        public static readonly VertexDeclaration Declaration = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
            new VertexElement(24, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.BlendIndices, 0),
            new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.BlendWeight, 0));

        public VertexDeclaration VertexDeclaration => Declaration;
    }

    /// <summary>One renderable mesh part sharing the model's skeleton.</summary>
    public class SkinnedMeshPart
    {
        public string Name;
        public VertexBuffer Vertices;
        public IndexBuffer Indices;
        public Texture2D Texture;
        public int PrimitiveCount;
    }

    /// <summary>A named animation clip: per-node TRS channels with sorted keyframes.</summary>
    public class AnimationClip
    {
        public string Name;
        public float Duration;

        public class Channel
        {
            public int NodeIndex;
            public float[] Times;
            public Numerics.Vector3[] Translations;   // non-null => translation channel
            public Numerics.Quaternion[] Rotations;   // non-null => rotation channel
            public Numerics.Vector3[] Scales;         // non-null => scale channel
        }

        public List<Channel> Channels = new List<Channel>();
    }

    /// <summary>
    /// A runtime-loaded skinned GLB model: skeleton + meshes + clips.
    /// Loading is pipeline-free (SharpGLTF at runtime), so it works identically
    /// on DesktopGL and Android. Intended to be shared; use SkinnedModelInstance per character.
    /// Ported from the SkinnedSpike experiment (Spikes/SkinnedSpike/Skinning).
    /// </summary>
    public class SkinnedModel
    {
        public List<SkinnedMeshPart> Parts = new List<SkinnedMeshPart>();
        public List<AnimationClip> Clips = new List<AnimationClip>();

        // Skeleton: flat node list with parent indices and bind-pose local TRS.
        public int NodeCount => _nodeParents.Length;
        public int JointCount => _jointNodeIndices.Length;
        public BoundingBox BindPoseBounds { get; private set; }

        private int[] _nodeParents;                 // -1 for roots
        private string[] _nodeNames;
        private Numerics.Vector3[] _bindT;
        private Numerics.Quaternion[] _bindR;
        private Numerics.Vector3[] _bindS;
        private int[] _jointNodeIndices;            // skin joints -> node index
        private Matrix[] _inverseBindMatrices;      // XNA-converted

        public static SkinnedModel Load(GraphicsDevice device, string glbPath)
        {
            return LoadCore(device, ModelRoot.Load(glbPath));
        }

        /// <summary>Load from a stream (e.g. Android AssetManager).</summary>
        public static SkinnedModel Load(GraphicsDevice device, Stream glbStream)
        {
            using (var ms = new MemoryStream())
            {
                glbStream.CopyTo(ms);
                return LoadCore(device, ModelRoot.ParseGLB(ms.ToArray()));
            }
        }

        public AnimationClip FindClip(string clipName)
        {
            return Clips.FirstOrDefault(c => string.Equals(c.Name, clipName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Finds a skeleton node index by (prefix) name, or -1.</summary>
        public int FindNodeIndex(string nodeName)
        {
            for (int i = 0; i < _nodeNames.Length; i++)
                if (_nodeNames[i].StartsWith(nodeName, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        private static SkinnedModel LoadCore(GraphicsDevice device, ModelRoot gltf)
        {
            var model = new SkinnedModel();

            // ---- Flatten node hierarchy ----
            var nodeIndexMap = new Dictionary<Node, int>();
            var nodeList = new List<Node>();
            var parentList = new List<int>();

            void Visit(Node node, int parent)
            {
                int idx = nodeList.Count;
                nodeIndexMap[node] = idx;
                nodeList.Add(node);
                parentList.Add(parent);
                foreach (var child in node.VisualChildren)
                    Visit(child, idx);
            }
            foreach (var root in gltf.DefaultScene.VisualChildren)
                Visit(root, -1);

            int n = nodeList.Count;
            model._nodeParents = parentList.ToArray();
            model._nodeNames = nodeList.Select(nd => nd.Name ?? "").ToArray();
            model._bindT = new Numerics.Vector3[n];
            model._bindR = new Numerics.Quaternion[n];
            model._bindS = new Numerics.Vector3[n];
            for (int i = 0; i < n; i++)
            {
                var lt = nodeList[i].LocalTransform;
                model._bindT[i] = lt.Translation;
                model._bindR[i] = lt.Rotation;
                model._bindS[i] = lt.Scale;
            }

            // ---- Skin ----
            var skinNode = nodeList.FirstOrDefault(nd => nd.Skin != null)
                ?? throw new InvalidDataException("GLB has no skinned mesh.");
            var skin = skinNode.Skin;
            model._jointNodeIndices = skin.Joints.Select(j => nodeIndexMap[j]).ToArray();
            var ibm = skin.InverseBindMatrices;
            model._inverseBindMatrices = ibm.Select((Numerics.Matrix4x4 m) => ToXna(m)).ToArray();

            // ---- Meshes (bind them to every node that has a skinned mesh) ----
            // Parts sharing one atlas share ONE decoded+mipmapped texture
            // (decoding + software mip chain per part was pure waste)
            var textureCache = new Dictionary<Image, Texture2D>();
            var boundsMin = new Vector3(float.MaxValue);
            var boundsMax = new Vector3(float.MinValue);
            foreach (var node in nodeList.Where(nd => nd.Mesh != null && nd.Skin != null))
            {
                foreach (var prim in node.Mesh.Primitives)
                {
                    var positions = prim.GetVertexAccessor("POSITION").AsVector3Array();
                    var normals = prim.GetVertexAccessor("NORMAL")?.AsVector3Array();
                    var uvs = prim.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                    var joints = prim.GetVertexAccessor("JOINTS_0")?.AsVector4Array();
                    var weights = prim.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                    if (joints == null || weights == null)
                        throw new InvalidDataException("Mesh primitive is not skinned (missing JOINTS_0/WEIGHTS_0).");

                    var verts = new SkinnedVertex[positions.Count];
                    for (int i = 0; i < verts.Length; i++)
                    {
                        var p = positions[i];
                        verts[i] = new SkinnedVertex
                        {
                            Position = new Vector3(p.X, p.Y, p.Z),
                            Normal = normals != null ? ToXna(normals[i]) : Vector3.Up,
                            TextureCoordinate = uvs != null ? new Vector2(uvs[i].X, uvs[i].Y) : Vector2.Zero,
                            BlendIndices = new Vector4(joints[i].X, joints[i].Y, joints[i].Z, joints[i].W),
                            BlendWeight = NormalizeWeights(new Vector4(weights[i].X, weights[i].Y, weights[i].Z, weights[i].W))
                        };
                        boundsMin = Vector3.Min(boundsMin, verts[i].Position);
                        boundsMax = Vector3.Max(boundsMax, verts[i].Position);
                    }

                    var part = new SkinnedMeshPart();
                    part.Name = node.Name ?? node.Mesh.Name; // node names are semantic (Knight_Body etc.)
                    part.Vertices = new VertexBuffer(device, SkinnedVertex.Declaration, verts.Length, BufferUsage.WriteOnly);
                    part.Vertices.SetData(verts);

                    var tris = prim.GetTriangleIndices().ToArray();
                    var indices = new int[tris.Length * 3];
                    for (int i = 0; i < tris.Length; i++)
                    {
                        indices[i * 3] = tris[i].A;
                        indices[i * 3 + 1] = tris[i].B;
                        indices[i * 3 + 2] = tris[i].C;
                    }
                    part.Indices = new IndexBuffer(device, IndexElementSize.ThirtyTwoBits, indices.Length, BufferUsage.WriteOnly);
                    part.Indices.SetData(indices);
                    part.PrimitiveCount = indices.Length / 3;

                    part.Texture = LoadBaseColorTexture(device, prim, textureCache);
                    model.Parts.Add(part);
                }
            }
            model.BindPoseBounds = new BoundingBox(boundsMin, boundsMax);

            // ---- Animation clips ----
            foreach (var anim in gltf.LogicalAnimations)
            {
                var clip = new AnimationClip { Name = anim.Name ?? "clip" };
                foreach (var ch in anim.Channels)
                {
                    if (!nodeIndexMap.TryGetValue(ch.TargetNode, out int nodeIdx))
                        continue;

                    var channel = new AnimationClip.Channel { NodeIndex = nodeIdx };
                    switch (ch.TargetNodePath)
                    {
                        case PropertyPath.translation:
                        {
                            var keys = ch.GetTranslationSampler().GetLinearKeys().ToArray();
                            channel.Times = keys.Select(k => k.Key).ToArray();
                            channel.Translations = keys.Select(k => k.Value).ToArray();
                            break;
                        }
                        case PropertyPath.rotation:
                        {
                            var keys = ch.GetRotationSampler().GetLinearKeys().ToArray();
                            channel.Times = keys.Select(k => k.Key).ToArray();
                            channel.Rotations = keys.Select(k => k.Value).ToArray();
                            break;
                        }
                        case PropertyPath.scale:
                        {
                            var keys = ch.GetScaleSampler().GetLinearKeys().ToArray();
                            channel.Times = keys.Select(k => k.Key).ToArray();
                            channel.Scales = keys.Select(k => k.Value).ToArray();
                            break;
                        }
                        default:
                            continue; // morph weights etc. not needed
                    }
                    if (channel.Times.Length > 0)
                    {
                        clip.Duration = Math.Max(clip.Duration, channel.Times[^1]);
                        clip.Channels.Add(channel);
                    }
                }
                if (clip.Channels.Count > 0)
                    model.Clips.Add(clip);
            }

            return model;
        }

        private static Vector4 NormalizeWeights(Vector4 w)
        {
            float sum = w.X + w.Y + w.Z + w.W;
            return sum > 0.0001f ? w / sum : new Vector4(1, 0, 0, 0);
        }

        private static Texture2D LoadBaseColorTexture(GraphicsDevice device, MeshPrimitive prim,
            Dictionary<Image, Texture2D> textureCache)
        {
            try
            {
                var texture = prim.Material?.FindChannel("BaseColor")?.Texture;
                var image = texture?.PrimaryImage;
                if (image != null)
                {
                    if (textureCache.TryGetValue(image, out var shared))
                        return shared;

                    ReadOnlyMemory<byte> bytes = image.Content.Content;
                    if (bytes.Length > 0)
                    {
                        using (var ms = new MemoryStream(bytes.ToArray()))
                        {
                            // Mipmaps keep small features (faces) visible at distance
                            var loaded = TextureTools.MakeMipmapped(device, Texture2D.FromStream(device, ms));
                            textureCache[image] = loaded;
                            return loaded;
                        }
                    }
                }
            }
            catch { /* fall through to magenta placeholder */ }

            var fallback = new Texture2D(device, 1, 1);
            fallback.SetData(new[] { Color.Magenta });
            return fallback;
        }

        // ---- Pose evaluation (called by SkinnedModelInstance) ----

        /// <summary>Local-space pose for one node (TRS).</summary>
        public struct NodePose
        {
            public Numerics.Vector3 T;
            public Numerics.Quaternion R;
            public Numerics.Vector3 S;
        }

        public void GetBindPose(NodePose[] pose)
        {
            for (int i = 0; i < pose.Length; i++)
            {
                pose[i].T = _bindT[i];
                pose[i].R = _bindR[i];
                pose[i].S = _bindS[i];
            }
        }

        /// <summary>
        /// Fills pose with the clip sampled at time. Starts from bind pose, overrides animated channels.
        /// Looping clips wrap the time; non-looping clips clamp to the last frame (freeze).
        /// </summary>
        public void SampleClip(AnimationClip clip, float time, NodePose[] pose, bool loop = true)
        {
            GetBindPose(pose);
            if (clip.Duration > 0)
            {
                time = loop ? time % clip.Duration : Math.Min(time, clip.Duration);
            }

            foreach (var ch in clip.Channels)
            {
                var p = pose[ch.NodeIndex];
                if (ch.Translations != null) p.T = Sample(ch.Times, ch.Translations, time);
                if (ch.Rotations != null) p.R = Sample(ch.Times, ch.Rotations, time);
                if (ch.Scales != null) p.S = Sample(ch.Times, ch.Scales, time);
                pose[ch.NodeIndex] = p;
            }
        }

        /// <summary>Blends two poses into outPose with factor t (0 = a, 1 = b).</summary>
        public static void BlendPoses(NodePose[] a, NodePose[] b, float t, NodePose[] outPose)
        {
            for (int i = 0; i < a.Length; i++)
            {
                outPose[i].T = Numerics.Vector3.Lerp(a[i].T, b[i].T, t);
                outPose[i].R = Numerics.Quaternion.Slerp(a[i].R, b[i].R, t);
                outPose[i].S = Numerics.Vector3.Lerp(a[i].S, b[i].S, t);
            }
        }

        /// <summary>Computes final bone matrices (inverseBind * jointWorld) for SkinnedEffect from a local pose.</summary>
        public void ComputeBoneMatrices(NodePose[] pose, Matrix[] boneOut)
        {
            Span<Matrix> world = stackalloc Matrix[NodeCount];
            ComputeWorldMatrices(pose, world);
            for (int j = 0; j < _jointNodeIndices.Length; j++)
                boneOut[j] = _inverseBindMatrices[j] * world[_jointNodeIndices[j]];
        }

        /// <summary>World matrices for every node (roots first — parents always precede children by construction).</summary>
        public void ComputeWorldMatrices(NodePose[] pose, Span<Matrix> worldOut)
        {
            for (int i = 0; i < NodeCount; i++)
            {
                var local = ToXna(Numerics.Matrix4x4.CreateScale(pose[i].S) *
                                  Numerics.Matrix4x4.CreateFromQuaternion(pose[i].R) *
                                  Numerics.Matrix4x4.CreateTranslation(pose[i].T));
                worldOut[i] = _nodeParents[i] < 0 ? local : local * worldOut[_nodeParents[i]];
            }
        }

        // ---- Keyframe sampling helpers ----

        private static Numerics.Vector3 Sample(float[] times, Numerics.Vector3[] values, float t)
        {
            FindKeys(times, t, out int i0, out int i1, out float f);
            return Numerics.Vector3.Lerp(values[i0], values[i1], f);
        }

        private static Numerics.Quaternion Sample(float[] times, Numerics.Quaternion[] values, float t)
        {
            FindKeys(times, t, out int i0, out int i1, out float f);
            return Numerics.Quaternion.Slerp(values[i0], values[i1], f);
        }

        private static void FindKeys(float[] times, float t, out int i0, out int i1, out float f)
        {
            if (t <= times[0]) { i0 = i1 = 0; f = 0; return; }
            if (t >= times[^1]) { i0 = i1 = times.Length - 1; f = 0; return; }
            int hi = Array.BinarySearch(times, t);
            if (hi >= 0) { i0 = i1 = hi; f = 0; return; }
            hi = ~hi;
            i0 = hi - 1; i1 = hi;
            f = (t - times[i0]) / (times[i1] - times[i0]);
        }

        // ---- Numerics -> XNA conversion ----

        public static Matrix ToXna(in Numerics.Matrix4x4 m) => new Matrix(
            m.M11, m.M12, m.M13, m.M14,
            m.M21, m.M22, m.M23, m.M24,
            m.M31, m.M32, m.M33, m.M34,
            m.M41, m.M42, m.M43, m.M44);

        private static Vector3 ToXna(in Numerics.Vector3 v) => new Vector3(v.X, v.Y, v.Z);
    }
}
