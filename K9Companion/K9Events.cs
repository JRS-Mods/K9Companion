using System;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Kills for Dr. Yang's Teeth and Steel: anyone you kill while your dog is alive and near you (or that the dog's bite
    /// finishes off, since a bite counts as yours) is reported once. The game's own death handler is only watched, never changed.
    /// </summary>
    internal static class K9Events
    {
        private const float WithYou = 50f;

        internal static void Patch(Harmony harmony)
        {
            var died = AccessTools.Method(typeof(Player), nameof(Player.OnBeenKilledByAggressor))
                       ?? throw new MissingMethodException(nameof(Player), nameof(Player.OnBeenKilledByAggressor));
            harmony.Patch(died, postfix: new HarmonyMethod(typeof(K9Events), nameof(Killed)));
        }

        private static void Killed(Player __instance, IPlayer aggressor)
        {
            try
            {
                Companion dog = Companion.Raid;
                Player you = dog?.Owner;
                if (dog == null || dog.IsDead || you == null || aggressor == null || __instance == you || aggressor.ProfileId != you.ProfileId) return;
                if (dog.DistanceTo(you) > WithYou) return;
                K9.Report("kill", __instance.ProfileId);
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 kill report: " + e.Message); }
        }
    }
}
