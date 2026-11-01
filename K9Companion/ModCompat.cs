using System;
using System.Reflection;
using BepInEx.Bootstrap;

namespace K9Companion
{
    /// <summary>
    /// What the dog needs to know from other mods, asked only when they're installed and through their public API (no
    /// reference to them, so the dog works the same without them).
    ///  - ORBIT (squad AI): bots it has put to sleep far from you ("dormant") aren't there to be hunted or bitten.
    /// AI mods that work through the bots' brains need nothing here: the dog's bot layer sits above their combat layers and
    /// below their avoid-danger ones (see DogThreat). Fika has its own part, DogNet and K9Companion.Fika.
    /// </summary>
    internal static class ModCompat
    {
        private static Func<string, bool> orbitDormant;
        private static bool looked;

        internal static bool Dormant(string profileId)
        {
            if (!looked)
            {
                looked = true;
                try
                {
                    if (Chainloader.PluginInfos.TryGetValue("com.chazut.orbit", out var orbit) && orbit.Instance != null)
                    {
                        MethodInfo check = orbit.Instance.GetType().Assembly.GetType("Orbit.Api.OrbitTelemetry")?.GetMethod("IsBotDormant", new[] { typeof(string) });
                        if (check != null) orbitDormant = (Func<string, bool>)Delegate.CreateDelegate(typeof(Func<string, bool>), check);
                        Plugin.Log.LogInfo(orbitDormant != null ? "ORBIT found: the dog leaves its dormant bots alone." : "ORBIT found, but not its dormancy check; the dog goes by the bots' own state.");
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("ORBIT's dormancy check couldn't be used: " + e.Message); }
            }
            if (orbitDormant == null || string.IsNullOrEmpty(profileId)) return false;
            try { return orbitDormant(profileId); }
            catch (Exception) { orbitDormant = null; return false; }
        }
    }
}
