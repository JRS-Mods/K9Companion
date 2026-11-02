using System;
using EFT;
using HarmonyLib;

namespace K9Companion
{
    /// <summary>
    /// The map's navmesh for a Fika client. Every map keeps its navmesh in its "_AI" scene (custom_AI, woods_AI, ...),
    /// the one scene the game marks offline-only; Fika makes a client skip it (only the host runs bots), which left the
    /// dog nowhere to stand: "No nearby navigation surface". When your dog is coming along, that scene loads as it does
    /// in single player and on the host. Without a dog, Fika's own choice stands.
    /// </summary>
    internal static class DogNavMesh
    {
        internal static void Patch(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetFromConfigAsync))
                         ?? throw new MissingMethodException(nameof(LoadScenesFromPresetOperation), nameof(LoadScenesFromPresetOperation.LoadPresetFromConfigAsync));
            // Last: Fika's prefix sets DisableServerScenes for a client, so this has to come after it.
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DogNavMesh), nameof(BeforeScenes)) { priority = Priority.Last });
        }

        private static void BeforeScenes(ref ScenePresetLoadConfig preset)
        {
            try
            {
                if (!preset.DisableServerScenes || !DogNet.Installed || DogNet.Headless) return;
                // A Fika client loads the map before the raid-start hook (DogBag.BeforeRaid) decides, so decide the same way here.
                bool coming = DogBag.Deployed ?? (Plugin.Active.Value && K9.NotDeployable() == null);
                if (!coming) return;
                preset.DisableServerScenes = false;
                Plugin.Log.LogInfo("Loading the map's AI scene too: it holds the navmesh the dog walks on (Fika skips it on clients).");
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't keep the map's navmesh for the dog: " + e.Message); }
        }
    }
}
