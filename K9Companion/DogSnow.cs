using System;
using System.Collections.Generic;
using EFT.CameraControl;
using UnityEngine;
using UnityEngine.Rendering;

namespace K9Companion
{
    /// <summary>
    /// Keeps the game's winter snow off the dog (Appearance > Snow on the dog).
    ///
    /// The snow is painted over the finished G-buffer (SnowWetRenderer, shader Hidden/Snowy, before reflections) wherever
    /// the two lowest stencil bits are 0. The game's own character shaders write 1 there (_StencilType;
    /// AmbientHighlight.StencilType.Characters), which is why people stay clear of it. The dog's Standard materials leave
    /// it at 0, so the dog came out white. Right after the G-buffer, this draws the dog's renderers again with the DEFERRED
    /// pass of the game's character shader, colour going to a throwaway target: only the stencil changes, to what a
    /// player's body has. Done for your camera and the scope's, the two the snow is drawn for.
    /// </summary>
    internal static class DogSnow
    {
        private const string CharacterShader = "p0/Reflective/Bumped Specular SMap";
        private const int Characters = 1;
        private static readonly int ThrowawayId = Shader.PropertyToID("_K9DogSnowThrowaway");
        private static readonly int StencilTypeId = Shader.PropertyToID("_StencilType");

        private static readonly List<Transform> dogs = new List<Transform>();
        private static readonly List<Renderer> renderers = new List<Renderer>();
        private static readonly Dictionary<Camera, CommandBuffer> buffers = new Dictionary<Camera, CommandBuffer>();
        private static readonly List<Camera> gone = new List<Camera>();
        private static Material marker;
        private static int pass = -1;
        private static bool hooked, failed;

        internal static void Add(Transform dog)
        {
            if (dog == null || dogs.Contains(dog)) return;
            dogs.Add(dog);
            if (!hooked) { Camera.onPreCull += PreCull; hooked = true; }
        }

        internal static void Remove(Transform dog) => dogs.Remove(dog);

        private static void PreCull(Camera camera)
        {
            if (failed) return;
            try
            {
                CameraManager manager = CameraManager.Instance;
                if (manager == null || (camera != manager.Camera && camera != manager.OpticCameraManager?.Camera)) return;
                Forget();
                buffers.TryGetValue(camera, out CommandBuffer buffer);
                dogs.RemoveAll(d => d == null);
                bool wanted = !Plugin.SnowOnDog.Value && dogs.Count > 0 && camera.actualRenderingPath == RenderingPath.DeferredShading;
                if (!wanted || Marker() == null)
                {
                    buffer?.Clear();
                    return;
                }
                if (buffer == null)
                {
                    buffer = new CommandBuffer { name = "K9 dog: no snow" };
                    camera.AddCommandBuffer(CameraEvent.AfterGBuffer, buffer);
                    buffers[camera] = buffer;
                }
                Fill(buffer);
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("Keeping snow off the dog failed; it'll get snow like the ground: " + e);
            }
        }

        private static void Fill(CommandBuffer buffer)
        {
            buffer.Clear();
            buffer.GetTemporaryRT(ThrowawayId, -1, -1, 0, FilterMode.Point, RenderTextureFormat.R8);
            // Colour into the throwaway target, depth and stencil the camera's own.
            buffer.SetRenderTarget(ThrowawayId, BuiltinRenderTextureType.CameraTarget);
            foreach (Transform dog in dogs)
            {
                if (!dog.gameObject.activeInHierarchy) continue;
                dog.GetComponentsInChildren(false, renderers);
                foreach (Renderer renderer in renderers)
                {
                    // Only what went into the G-buffer: not shadow-only or see-through parts.
                    if (!renderer.enabled || renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly || renderer.forceRenderingOff) continue;
                    Material[] materials = renderer.sharedMaterials;
                    for (int s = 0; s < materials.Length; s++)
                    {
                        if (materials[s] == null || materials[s].renderQueue > 2500) continue;
                        buffer.DrawRenderer(renderer, marker, s, pass);
                    }
                }
            }
            buffer.ReleaseTemporaryRT(ThrowawayId);
        }

        // A material of the game's character shader set to "character", once; null if the game hasn't got it.
        private static Material Marker()
        {
            if (marker != null) return marker;
            Shader shader = Shader.Find(CharacterShader);
            if (shader == null)
                foreach (Shader loaded in Resources.FindObjectsOfTypeAll<Shader>())
                    if (loaded != null && loaded.name == CharacterShader) { shader = loaded; break; }
            if (shader == null) { Fail("the game's character shader isn't loaded"); return null; }
            var material = new Material(shader) { name = "K9 dog: no snow", hideFlags = HideFlags.DontSave };
            int deferred = material.FindPass("DEFERRED");
            if (deferred < 0 || !material.HasProperty(StencilTypeId))
            {
                UnityEngine.Object.Destroy(material);
                Fail("the game's character shader has changed");
                return null;
            }
            material.SetInt(StencilTypeId, Characters);
            marker = material; pass = deferred;
            return marker;
        }

        private static void Fail(string why)
        {
            failed = true;
            Plugin.Log.LogWarning("Can't keep snow off the dog (" + why + "); it'll get snow like the ground.");
        }

        // Cameras destroyed since (a raid ended) take their buffers with them.
        private static void Forget()
        {
            gone.Clear();
            foreach (var pair in buffers) if (pair.Key == null) gone.Add(pair.Key);
            foreach (Camera camera in gone) { buffers[camera]?.Release(); buffers.Remove(camera); }
        }
    }
}
