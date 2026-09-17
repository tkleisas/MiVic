using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using MiVic.Game.Rendering;

namespace MiVic.Game.Cutscene.Skinning
{
    /// <summary>
    /// A per-character playback instance of a shared SkinnedModel.
    /// Owns clip state, cross-fade blending, tint and the bone matrix palette.
    /// Ported from the SkinnedSpike experiment and extended with:
    /// - Play(clipName, loop): non-looping clips play once and freeze on their
    ///   last frame, then automatically cross-fade back to the last looping clip.
    /// - SetTint: per-instance SkinnedEffect.DiffuseColor.
    /// </summary>
    public class SkinnedModelInstance
    {
        private readonly SkinnedModel _model;
        private readonly Matrix[] _boneMatrices;
        private readonly SkinnedModel.NodePose[] _poseA;
        private readonly SkinnedModel.NodePose[] _poseB;
        private readonly SkinnedModel.NodePose[] _poseOut;
        private SkinnedEffect _effect;
        private Vector3 _tint = Vector3.One;
        private readonly Dictionary<string, Texture2D> _partTextureOverrides = new Dictionary<string, Texture2D>();

        public AnimationClip CurrentClip { get; private set; }
        public float CurrentTime { get; private set; }
        public bool CurrentLooping { get; private set; } = true;
        
        /// <summary>Playback rate multiplier (1 = authored speed). Applies to clips, not the cross-fade.</summary>
        public float PlaybackSpeed { get; set; } = 1f;

        private AnimationClip _previousClip;
        private float _previousTime;
        private bool _previousLooping;
        private float _blend;          // 1 = fully in CurrentClip
        private float _blendSpeed = 1f / 0.3f; // 0.3s cross-fade

        /// <summary>Last looping clip played; non-looping clips auto-return here when finished.</summary>
        private AnimationClip _baseClip;

        /// <summary>True while a non-looping clip has played through to its last frame.</summary>
        public bool CurrentClipFinished =>
            CurrentClip != null && !CurrentLooping && CurrentTime >= CurrentClip.Duration;

        public SkinnedModelInstance(SkinnedModel model)
        {
            _model = model;
            _boneMatrices = new Matrix[Math.Min(model.JointCount, 72)]; // SkinnedEffect limit
            _poseA = new SkinnedModel.NodePose[model.NodeCount];
            _poseB = new SkinnedModel.NodePose[model.NodeCount];
            _poseOut = new SkinnedModel.NodePose[model.NodeCount];

            CurrentClip = model.Clips.FirstOrDefault();
            _baseClip = CurrentClip;
        }

        /// <summary>
        /// Plays a clip with a 0.3s cross-fade. Returns false if the clip doesn't exist.
        /// Looping clips become the new "base" clip that one-shots return to when done.
        /// </summary>
        public bool Play(string clipName, bool loop = true)
        {
            var clip = _model.FindClip(clipName);
            if (clip == null) return false;
            if (clip == CurrentClip && loop == CurrentLooping) return true;

            _previousClip = CurrentClip;
            _previousTime = CurrentTime;
            _previousLooping = CurrentLooping;
            CurrentClip = clip;
            CurrentTime = 0f;
            CurrentLooping = loop;
            _blend = 0f;

            if (loop) _baseClip = clip;
            return true;
        }

        /// <summary>Per-instance tint applied to SkinnedEffect.DiffuseColor (null = white).</summary>
        public void SetTint(Color? tint)
        {
            _tint = tint?.ToVector3() ?? Vector3.One;
        }

        private readonly Dictionary<int, float> _boneScales = new Dictionary<int, float>();

        /// <summary>Scales a named bone's local scale on every frame
        /// (e.g. shrinking the fox tail bones turns the fox into a dog).</summary>
        public void SetBoneScale(string nodeName, float scale)
        {
            int idx = _model.FindNodeIndex(nodeName);
            if (idx >= 0) _boneScales[idx] = scale;
        }
        
        /// <summary>
        /// Overrides the texture of one mesh part for this instance only
        /// (kit recoloring). Null removes the override.
        /// </summary>
        public void SetPartTexture(string partName, Texture2D texture)
        {
            if (texture == null)
                _partTextureOverrides.Remove(partName);
            else
                _partTextureOverrides[partName] = texture;
        }
        
        /// <summary>Optional match environment; when set its lighting replaces the default rig.</summary>
        /// <summary>
        /// The light this figure is drawn under. MiVic's own environment rather than a
        /// match preset: the cutscene already builds one for the room, and a skinned
        /// figure lit by anything else reads as a visitor from another scene.
        /// </summary>
        public InstancedRenderer.Environment Environment { get; set; }

        /// <summary>
        /// The model-space world transform of a named node at the current pose, if the
        /// model has one. For marker nodes the asset carries — the pipe's bowl is an
        /// empty parented to the head bone, so the smoke can rise from where the bowl
        /// actually is this frame rather than from a constant somebody has to keep true.
        /// </summary>
        public bool TryGetNodeWorld(string nodeName, out Matrix world)
        {
            int index = _model.FindNodeIndex(nodeName);
            if (index < 0)
            {
                world = Matrix.Identity;
                return false;
            }

            Span<Matrix> worlds = stackalloc Matrix[_model.NodeCount];
            _model.ComputeWorldMatrices(_poseOut, worlds);
            world = worlds[index];
            return true;
        }

        public void Update(float deltaTime)
        {
            // One-shot finished: freeze was on the last frame; cross-fade back to the base loop.
            if (CurrentClipFinished && _baseClip != null && CurrentClip != _baseClip)
            {
                _previousClip = CurrentClip;
                _previousTime = CurrentTime;
                _previousLooping = false;
                CurrentClip = _baseClip;
                CurrentTime = 0f;
                CurrentLooping = true;
                _blend = 0f;
            }

            CurrentTime += deltaTime * PlaybackSpeed;
            _previousTime += deltaTime * PlaybackSpeed;
            _blend = Math.Min(1f, _blend + _blendSpeed * deltaTime);

            if (CurrentClip == null)
            {
                _model.GetBindPose(_poseOut);
            }
            else if (_previousClip != null && _blend < 1f)
            {
                _model.SampleClip(_previousClip, _previousTime, _poseA, _previousLooping);
                _model.SampleClip(CurrentClip, CurrentTime, _poseB, CurrentLooping);
                SkinnedModel.BlendPoses(_poseA, _poseB, _blend, _poseOut);
            }
            else
            {
                _previousClip = null;
                _model.SampleClip(CurrentClip, CurrentTime, _poseOut, CurrentLooping);
            }

            foreach (var (boneIdx, boneScale) in _boneScales)
                _poseOut[boneIdx].S *= boneScale;

            _model.ComputeBoneMatrices(_poseOut, _boneMatrices);
        }

        /// <summary>
        /// Poses the figure at an absolute time in its clip rather than advancing it by a
        /// frame delta.
        /// <para>
        /// MiVic's rule is that the simulation owns the state and presentation reads it. A
        /// cutscene's clock is the director's — the same clock the camera moves on, that the
        /// probes seek, and that makes two runs photograph the same moment. A figure posed
        /// from that clock is the same figure on every machine at every frame rate; one
        /// advanced by a delta is a function of how long somebody waited, which is the thing
        /// this project has spent the most effort not doing.
        /// </para>
        /// </summary>
        public void PoseAt(float seconds)
        {
            CurrentTime = seconds;
            _previousTime = seconds;
            _previousClip = null;
            _blend = 1f;

            if (CurrentClip == null)
            {
                _model.GetBindPose(_poseOut);
            }
            else
            {
                _model.SampleClip(CurrentClip, CurrentTime, _poseOut, CurrentLooping);
            }

            foreach (var (boneIndex, boneScale) in _boneScales)
            {
                _poseOut[boneIndex].S *= boneScale;
            }

            _model.ComputeBoneMatrices(_poseOut, _boneMatrices);
        }

        public void Draw(GraphicsDevice device, Matrix world, Matrix view, Matrix projection)
        {
            device.RasterizerState = RasterizerState.CullClockwise; // glTF front faces are CCW
            device.DepthStencilState = DepthStencilState.Default;
            device.SamplerStates[0] = SamplerState.LinearWrap;
            device.BlendState = BlendState.Opaque;

            _effect ??= new SkinnedEffect(device) { WeightsPerVertex = 4 };
            _effect.World = world;
            _effect.View = view;
            _effect.Projection = projection;
            _effect.DiffuseColor = _tint;

            // No vertex-colour channel here, and that is a real limit rather than an
            // oversight: MonoGame's SkinnedEffect has no VertexColorEnabled — that is
            // BasicEffect's — so a skinned model is coloured by its texture and nothing else.
            // COLOR_0 is parsed and carried on the vertex because the data is in the file and
            // a custom skinned shader is the only thing that could read it; today a generated
            // rigged asset needs UVs and a painted map like every other model.
            // MiVic's LightDirection points *towards* the light — its shader takes
            // dot(normal, LightDirection) — and SkinnedEffect wants the direction the light
            // travels. Passing it straight through would light the figure from behind.
            _effect.AmbientLightColor = Environment.AmbientColor.ToVector3();
            _effect.DirectionalLight0.Enabled = true;
            _effect.DirectionalLight0.Direction = -Environment.LightDirection;
            _effect.DirectionalLight0.DiffuseColor = Vector3.One;
            _effect.DirectionalLight0.SpecularColor = Vector3.Zero;
            _effect.DirectionalLight1.Enabled = false;
            _effect.DirectionalLight2.Enabled = false;
            _effect.FogEnabled = true;
            _effect.FogColor = Environment.FogColor.ToVector3();
            _effect.FogStart = Environment.FogStart;
            _effect.FogEnd = Environment.FogEnd;
            _effect.SetBoneTransforms(_boneMatrices);

            foreach (var part in _model.Parts)
            {
                _effect.Texture = _partTextureOverrides.TryGetValue(part.Name, out var overrideTexture)
                    ? overrideTexture
                    : part.Texture; // never null: the loader's answer or the sidecar map

                device.SetVertexBuffer(part.Vertices);
                device.Indices = part.Indices;
                foreach (var pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, part.PrimitiveCount);
                }
            }
        }
    }
}
