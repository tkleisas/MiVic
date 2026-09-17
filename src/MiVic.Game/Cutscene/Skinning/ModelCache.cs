using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Cutscene.Skinning
{
    /// <summary>
    /// Process-lifetime cache for skinned GLB models (Player, PlayerF, Fox...).
    /// Models are invariant assets: parsing once kills the per-match GLB reload
    /// and, crucially, keeps the atlas Texture2D identity stable so the
    /// FaceComposer / KitTextureFactory caches hit across matches instead of
    /// leaking hundreds of baked textures per match.
    /// All methods must be called on the game thread (GPU resource creation).
    /// </summary>
    public static class ModelCache
    {
        private static readonly Dictionary<string, SkinnedModel> _models =
            new Dictionary<string, SkinnedModel>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Returns the cached model, loading it on first use. Throws on failure.</summary>
        public static SkinnedModel Get(GraphicsDevice device, string fileName)
        {
            if (_models.TryGetValue(fileName, out var cached))
                return cached;
            var model = LoadFromAssets(device, fileName);
            _models[fileName] = model;
            return model;
        }

        /// <summary>Returns null instead of throwing when the model can't be loaded.</summary>
        public static SkinnedModel TryGet(GraphicsDevice device, string fileName)
        {
            try { return Get(device, fileName); }
            catch { return null; }
        }

        /// <summary>Warms the cache (call once at startup so the first match doesn't hitch).</summary>
        public static void Preload(GraphicsDevice device, params string[] fileNames)
        {
            foreach (var name in fileNames)
                TryGet(device, name);
        }

        private static SkinnedModel LoadFromAssets(GraphicsDevice device, string fileName)
        {
            // MiVic keeps generated models in one flat content folder, so the file name is
            // the whole path below it. NFP's PlatformHelper resolved the same thing against
            // an asset root and an Android asset manager; this project ships desktop only.
            string path = Path.Combine(
                AppContext.BaseDirectory, "Content", "Models", "Generated", fileName);

            if (!File.Exists(path))
            {
                throw new FileNotFoundException($"skinned model not found: {path}");
            }

            return SkinnedModel.Load(device, path);
        }
    }
}
