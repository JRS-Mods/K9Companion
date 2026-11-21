using System;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The kennels' lamps follow the hideout's light switch (the overview's off, candles, bulbs, fluorescent tubes,
    /// Christmas and Halloween lights) the way the areas' own lamps and Hideout Extensions' room lamps do: lit unless the
    /// switch is off or on candles, a tube's white light for fluorescent tubes and a bulb's warm light for the rest.
    /// </summary>
    internal static class KennelLights
    {
        private static readonly Color Tube = new Color(1f, .94f, .82f), Bulb = new Color(1f, .8f, .58f);
        private static ELightingLevel? level;

        internal static void Patch(Harmony harmony)
        {
            var set = AccessTools.Method(typeof(HideoutController), nameof(HideoutController.SetLightLevel), new[] { typeof(ELightingLevel) })
                      ?? throw new MissingMethodException(nameof(HideoutController), "SetLightLevel(ELightingLevel)");
            harmony.Patch(set, postfix: new HarmonyMethod(typeof(KennelLights), nameof(Switched)));
        }

        private static void Switched(ELightingLevel level) => KennelLights.level = level;

        // What the switch is on: as the game last set it, else as it keeps it (the game's own preference).
        private static ELightingLevel Current
        {
            get
            {
                if (level == null)
                    try { level = HideoutLocalData.GlobalLightingLevel; }
                    catch (Exception) { level = ELightingLevel.FluorescentLamps; }
                return level.Value;
            }
        }

        internal static bool Ours(Light l) => l != null && (l.name == "K9 kennel lamp" || l.name == "Military kennel door lamp");

        internal static void Apply(Light lamp, Light doorLamp, float intensity, float doorIntensity)
        {
            ELightingLevel now = Current;
            bool lit = now != ELightingLevel.Off && now != ELightingLevel.Candles;
            Color colour = now == ELightingLevel.FluorescentLamps ? Tube : Bulb;
            Set(lamp, lit, colour, intensity);
            Set(doorLamp, lit, Bulb, doorIntensity);
        }

        private static void Set(Light light, bool lit, Color colour, float intensity)
        {
            if (light == null) return;
            if (light.enabled != lit) light.enabled = lit;
            if (light.color != colour) light.color = colour;
            if (light.intensity != intensity) light.intensity = intensity;
        }
    }
}
