using System;
using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.Ballistics;
using HarmonyLib;

namespace K9Companion
{
    /// <summary>
    /// Makes sure whoever the dog kills finishes dying. Player.OnDead counts the kill and tells everyone who's listening first,
    /// and only then makes the body, so an error in anything run on the way (another mod's kill counter, say) stops it there:
    /// health gone, but still standing or walking off, with no body to loot and nothing left to shoot. A bite is the one kill
    /// of yours with no weapon to it, and a mod that takes one for granted falls over on it. With Fika that's only on the dog
    /// owner's game, where the kill is counted: the others see the body. So for a dog's kill such an error is written to the
    /// log, with where it came from, and the death carries on. Every other death is left exactly as it was.
    /// </summary>
    internal static class DogKill
    {
        private static readonly HashSet<string> Bitten = new HashSet<string>();
        private static readonly AccessTools.FieldRef<Player, DamageInfo> LastDamage = AccessTools.FieldRefAccess<Player, DamageInfo>("LastDamageInfo");

        // One event's handlers, as they were and as they're guarded, while a dog's kill is dying.
        private sealed class Guarded
        {
            internal FieldInfo Field;
            internal object Owner;
            internal Delegate Before, Stand;
            internal bool Done;
        }

        internal static void Patch(Harmony harmony)
        {
            // Fika's own players override this, each adding to it: an error can come from any of them.
            int hooked = 0;
            foreach (Type type in new[] { typeof(Player), typeof(LocalPlayer),
                                          AccessTools.TypeByName("Fika.Core.Main.Players.FikaPlayer"),
                                          AccessTools.TypeByName("Fika.Core.Main.Players.FikaBot"),
                                          AccessTools.TypeByName("Fika.Core.Main.Players.ObservedPlayer") })
            {
                MethodInfo killed = type == null ? null : AccessTools.DeclaredMethod(type, nameof(Player.OnBeenKilledByAggressor));
                if (killed == null) continue;
                harmony.Patch(killed, finalizer: new HarmonyMethod(typeof(DogKill), nameof(AfterCounted)));
                hooked++;
            }
            if (hooked == 0) throw new MissingMethodException(nameof(Player), nameof(Player.OnBeenKilledByAggressor));
            MethodInfo dead = AccessTools.DeclaredMethod(typeof(Player), nameof(Player.OnDead))
                              ?? throw new MissingMethodException(nameof(Player), nameof(Player.OnDead));
            harmony.Patch(dead, prefix: new HarmonyMethod(typeof(DogKill), nameof(BeforeDead)) { priority = Priority.Last },
                                finalizer: new HarmonyMethod(typeof(DogKill), nameof(AfterDead)));
        }

        internal static void Bite(Player target)
        {
            if (target != null) Bitten.Add(target.ProfileId);
        }

        internal static void RaidOver() => Bitten.Clear();

        // Bitten by this game's dog; or (another player's dog, on the host's game or a third player's) the melee kill with no
        // weapon that only a bite is.
        private static bool Ours(Player victim, EDamageType how)
        {
            if (how != EDamageType.Melee || victim == null) return false;
            if (Bitten.Contains(victim.ProfileId)) return true;
            ref DamageInfo last = ref LastDamage(victim);
            return last.Weapon == null && last.Player != null;
        }

        private static Exception AfterCounted(Exception __exception, Player __instance, EDamageType __3)
        {
            if (__exception == null || !Ours(__instance, __3)) return __exception;
            Report("An error while counting the dog's kill (it dies all the same)", __exception);
            return null;
        }

        private static void BeforeDead(Player __instance, EDamageType __0, out List<Guarded> __state)
        {
            __state = null;
            try
            {
                if (!Ours(__instance, __0)) return;
                __state = new List<Guarded>();
                Guard(__state, __instance, nameof(Player.OnPlayerDead), g => new OnPlayerDead((a, b, c, d) => Run(g, h => ((OnPlayerDead)h)(a, b, c, d))));
                Guard(__state, null, nameof(Player.OnPlayerDeadStatic), g => new Action<Player, IPlayer, DamageInfo, EBodyPart>(
                    (a, b, c, d) => Run(g, h => ((Action<Player, IPlayer, DamageInfo, EBodyPart>)h)(a, b, c, d))));
                Guard(__state, __instance, nameof(Player.OnPlayerDeadOrUnspawn), g => new OnPlayerDeadOrUnspawn(a => Run(g, h => ((OnPlayerDeadOrUnspawn)h)(a))));
                Guard(__state, __instance, nameof(Player.OnIPlayerDeadOrUnspawn), g => new Action<IPlayer>(a => Run(g, h => ((Action<IPlayer>)h)(a))));
            }
            catch (Exception e) { Plugin.Log.LogWarning("Guarding the dog's kill: " + e); }
        }

        private static void AfterDead(Exception __exception, List<Guarded> __state)
        {
            if (__state == null) return;
            foreach (Guarded g in __state) Restore(g);
            if (__exception != null) Report("The death of the dog's kill failed (it may be left without a body)", __exception);
        }

        private static void Guard(List<Guarded> all, Player owner, string name, Func<Guarded, Delegate> stand)
        {
            FieldInfo field = AccessTools.DeclaredField(typeof(Player), name);
            if (field == null || !(field.GetValue(owner) is Delegate before)) return;
            var g = new Guarded { Field = field, Owner = owner, Before = before };
            g.Stand = stand(g);
            all.Add(g);
            field.SetValue(owner, g.Stand);
        }

        // Puts the real handlers back (with any added meanwhile).
        private static void Restore(Guarded g)
        {
            if (g.Done) return;
            g.Done = true;
            g.Field.SetValue(g.Owner, Delegate.Combine(g.Before, Delegate.Remove((Delegate)g.Field.GetValue(g.Owner), g.Stand)));
        }

        // The same handlers in the same order, but one that fails doesn't take the rest (and the body) with it. The real ones
        // go back first, so a handler that takes itself off (most do) really comes off.
        private static void Run(Guarded g, Action<Delegate> call)
        {
            Restore(g);
            foreach (Delegate handler in g.Before.GetInvocationList())
            {
                try { call(handler); }
                catch (Exception e) { Report("An error in what's told about the dog's kill (it dies all the same)", e); }
            }
        }

        private static void Report(string what, Exception e) => Plugin.Log.LogWarning(what + ": " + e);
    }
}
