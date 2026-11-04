using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System;
using EFT.Quests;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The client side of Dr. Yang's K9 expansion. The server mod keeps everything (who owns a dog, the kennel, the dog
    /// objectives, contracts, cosmetics, presets and jacket wear) in the SPT profile; this reads it, reports the dog's
    /// events, and puts the server's answers into the profile you're playing so the quest screen updates straight away.
    /// </summary>
    internal static class K9
    {
        private const string StateRoute = "/k9companion/k9/state", EventRoute = "/k9companion/k9/event",
            SaveRoute = "/k9companion/k9/save", RaidRoute = "/k9companion/k9/raid-start", DiedRoute = "/k9companion/k9/dog-died", RevivedRoute = "/k9companion/k9/dog-revived";

        // Dr. Yang's quests and improvements (see the server mod's K9Ids).
        internal const string Yang = "6a5d0c0c4fb9a3c5e1d0d0a0";
        internal static string Quest(int n) => "6a5d0c0c4fb9a3c5e1d1" + n.ToString("x4");
        internal static readonly string[] KennelIds = { "6a5d0c0c4fb9a3c5e1d70001", "6a5d0c0c4fb9a3c5e1d70002", "6a5d0c0c4fb9a3c5e1d70003" };
        internal static readonly string[] KennelNames = { "Makeshift Kennel", "Working Dog Station", "Tactical K9 Facility" };
        internal const int TrustIsEarned = 5;

        internal static bool Loaded, Owned, BedUnlocked;
        internal static int ServerKennel, Loyalty = 1, Bed, Training;
        // The collar picked in the DOG tab before 0.31 and the ones the quests had earned (the server hands those over as
        // items once); passed back to the server unchanged. The dog wears what's in its collar slot (DogGear.CollarStyle).
        internal static string Collar = "training", Tag = "steel";
        internal static List<string> Collars = new List<string> { "training" };
        internal static readonly string[] Presets = { "", "", "", "" };
        internal static readonly Dictionary<string, Wear> Armour = new Dictionary<string, Wear>();
        internal sealed class Wear { public float Now, Max; }

        private static readonly ConcurrentQueue<string> replies = new ConcurrentQueue<string>();
        private static readonly Dictionary<string, float> lastReport = new Dictionary<string, float>();

        // ---- state ------------------------------------------------------------------------------------------------

        internal static void EnsureLoaded() { if (!Loaded) Refresh(); }

        /// <summary>Reads the state again (a small request; done when it may have changed: a new screen, after a raid).</summary>
        internal static void Refresh()
        {
            try { Apply(RequestHandler.PostJson(StateRoute, "{}")); }
            catch (Exception e) { Failed(e); }
            ServerRules.Refresh();
        }

        /// <summary>
        /// The same, without holding up the game while the server answers (a wait on the main thread stalls the menus and
        /// makes whatever sound is playing stutter): the answer is applied by Tick. For the routine ownership look-ups.
        /// </summary>
        internal static void RefreshInBackground()
        {
            if (refreshing) return;
            refreshing = true;
            try
            {
                RequestHandler.PostJsonAsync(StateRoute, "{}").ContinueWith(t =>
                {
                    if (t.IsFaulted) Plugin.Log.LogWarning("Couldn't read the K9 state from the server mod: " + t.Exception?.GetBaseException().Message);
                    else if (t.Result != null) states.Enqueue(t.Result);
                    refreshing = false;
                });
            }
            catch (Exception e) { refreshing = false; Failed(e); }
        }
        private static readonly ConcurrentQueue<string> states = new ConcurrentQueue<string>();
        private static volatile bool refreshing;

        private static void Failed(Exception e)
        {
            // Without the server mod the dog behaves as before 0.16: every profile has it.
            if (!Loaded) { Owned = true; Loaded = true; }
            Plugin.Log.LogWarning("Couldn't read the K9 state from the server mod: " + e.Message);
        }

        private static void Apply(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            JObject s = JObject.Parse(json);
            if (s["ready"] != null && !(bool)s["ready"]) return;   // no character yet
            Owned = (bool?)s["owned"] ?? false;
            ServerKennel = (int?)s["kennel"] ?? 0;
            Loyalty = (int?)s["loyalty"] ?? 1;
            Collar = (string)s["collar"] ?? "training";
            Collars = (s["collars"] as JArray)?.Select(c => (string)c).Where(c => c != null).ToList() ?? new List<string> { "training" };
            Tag = (string)s["tag"] ?? "steel";
            Bed = (int?)s["bed"] ?? 0;
            Training = (int?)s["training"] ?? 0;
            BedUnlocked = (bool?)s["bedUnlocked"] ?? false;
            var presets = s["presets"] as JArray;
            for (int i = 0; i < Presets.Length; i++) Presets[i] = presets != null && i < presets.Count ? (string)presets[i] ?? "" : "";
            Armour.Clear();
            if (s["armour"] is JObject armour)
                foreach (var p in armour.Properties())
                    Armour[p.Name] = new Wear { Now = (float?)p.Value["now"] ?? 0, Max = (float?)p.Value["max"] ?? 0 };
            ApplyCounters(s["counters"] as JObject);
            Loaded = true;
        }

        /// <summary>The profile you're playing: the raid's in a raid, otherwise the session's.</summary>
        internal static Profile SessionProfile => TarkovApplication.Exist(out TarkovApplication app) ? app.Session?.Profile : null;

        /// <summary>Kennel level as the hideout sees it now (a level finishes on its own when its timer runs out).</summary>
        internal static int Kennel(Profile profile = null)
        {
            profile = profile ?? SessionProfile;
            var built = profile?.Hideout?.Improvements;
            // A level started this session is only in the hideout's own list until the profile is next loaded.
            var live = Comfort.Common.Singleton<EFT.Hideout.HideoutRepresentation>.Instantiated
                ? Comfort.Common.Singleton<EFT.Hideout.HideoutRepresentation>.Instance.ImprovementController?._improvementDataBy : null;
            if (built == null && live == null) return ServerKennel;
            int level = 0;
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            while (level < 3)
            {
                EFT.Hideout.ImprovementData i = null;
                if (live == null || !live.TryGetValue(KennelIds[level], out i)) built?.TryGetValue(KennelIds[level], out i);
                if (i == null || !(i.Completed || (i.CompleteTimestamp > 0 && i.CompleteTimestamp <= now))) break;
                level++;
            }
            return level;
        }

        internal static bool QuestDone(Profile profile, int quest)
        {
            string id = Quest(quest);
            return profile?.QuestsData?.Any(q => q.Id == id && q.Status == EQuestStatus.Success) == true;
        }

        internal static int YangLoyalty(Profile profile)
        {
            try { if (profile?.TradersInfo != null && profile.TradersInfo.TryGetValue(new MongoID(Yang), out var info)) return info.LoyaltyLevel; }
            catch { /* not loaded yet */ }
            return Loyalty;
        }

        // ---- health and deployment ----------------------------------------------------------------------------------

        internal const float DeployFrom = 25f;

        /// <summary>Why the dog can't go into a raid with you now, or null.</summary>
        internal static string NotDeployable()
        {
            EnsureLoaded();
            if (!Owned) return "you have no dog yet";
            if (Plugin.SoloOnly && DogNet.InGroup) return "dogs only come on solo raids, and this is a group raid";
            DogNeeds needs = NeedsStore.Current;
            if (Plugin.CarryOverInjuries.Value && needs != null && needs.Health < DeployFrom) return Plugin.DogName.Value + " is too badly hurt to deploy (" + Mathf.RoundToInt(needs.Health) + "%)";
            return null;
        }

        internal static string Condition(DogNeeds needs)
        {
            if (needs == null) return "Unknown";
            if (needs.Health < 1) return "Critical";
            if (needs.Health < DeployFrom) return "Badly injured";
            if (needs.Health < 70) return "Injured";
            if (Plugin.SurvivalNeeds.Value && Mathf.Min(needs.Hunger, needs.Thirst) < DogNeeds.Hungry) return needs.Hunger <= needs.Thirst ? "Hungry" : "Thirsty";
            return "Healthy";
        }

        // Resting between raids: health (% per hour) comes back faster the better the kennel and its bed; hunger and thirst
        // go down more slowly at a better kennel.
        internal static float RecoveryPerHour(int kennel, int bed) => kennel <= 0 ? 6 : kennel == 1 ? 10 + 3 * bed : kennel == 2 ? 16 + 3 * bed : 25 + 3 * bed;
        internal static float RestPace(int kennel) => kennel <= 0 ? .6f : kennel == 1 ? .5f : kennel == 2 ? .35f : .25f;

        private static float lastRecovery;

        /// <summary>Called every frame out of raids: the dog's wounds heal while it rests.</summary>
        internal static void Recover()
        {
            if (Time.unscaledTime < lastRecovery + 10) return;
            float hours = lastRecovery <= 0 ? 0 : (Time.unscaledTime - lastRecovery) / 3600f;
            lastRecovery = Time.unscaledTime;
            if (hours <= 0 || !Loaded || !Owned || !Plugin.CarryOverInjuries.Value || SessionProfile == null) return;
            DogNeeds needs = NeedsStore.Current;
            if (needs == null || needs.Health >= 100) return;
            needs.Health = Mathf.Min(100, needs.Health + hours * RecoveryPerHour(Kennel(), Bed));
            if (Time.unscaledTime >= nextRecoverySave) { nextRecoverySave = Time.unscaledTime + 120; NeedsStore.Save(); }
        }
        private static float nextRecoverySave;

        // ---- reporting --------------------------------------------------------------------------------------------

        /// <summary>One of the dog's events (follow, sit, come, locate, retrieve, kill). Sent in the background.</summary>
        internal static void Report(string type, string key = null)
        {
            if (!Owned) return;
            // A command repeated within a few seconds is the same lesson; finds and kills carry their own key.
            string tag = type + ":" + key;
            if (key == null && lastReport.TryGetValue(tag, out float at) && Time.unscaledTime - at < 5) return;
            lastReport[tag] = Time.unscaledTime;
            string body = new JObject { ["type"] = type, ["key"] = key }.ToString(Newtonsoft.Json.Formatting.None);
            Send(EventRoute, body);
        }

        internal static void RaidStart(bool deployed)
        {
            try
            {
                string reply = RequestHandler.PostJson(RaidRoute, deployed ? "{\"deployed\":true}" : "{\"deployed\":false}");
                if (reply != null) replies.Enqueue(reply);
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server mod about the raid: " + e.Message); }
        }

        internal static void DogDied() => Send(DiedRoute, "{}");
        internal static void DogRevived() => Send(RevivedRoute, "{}");

        /// <summary>Saves cosmetics, bed, presets and jacket wear.</summary>
        internal static void Save()
        {
            var armour = new JObject();
            foreach (var pair in Armour) armour[pair.Key] = new JObject { ["now"] = Math.Round(pair.Value.Now, 2), ["max"] = Math.Round(pair.Value.Max, 2) };
            var body = new JObject { ["collar"] = Collar, ["tag"] = Tag, ["bed"] = Bed, ["training"] = Training, ["presets"] = new JArray(Presets), ["armour"] = armour };
            Send(SaveRoute, body.ToString(Newtonsoft.Json.Formatting.None));
        }

        private static void Send(string route, string body)
        {
            try
            {
                RequestHandler.PostJsonAsync(route, body).ContinueWith(t =>
                {
                    if (t.IsFaulted) Plugin.Log.LogWarning("K9 report failed: " + t.Exception?.GetBaseException().Message);
                    else if (t.Result != null) replies.Enqueue(t.Result);
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 report failed: " + e.Message); }
        }

        /// <summary>Main thread: the server's answers go into the profile you're playing.</summary>
        internal static void Tick()
        {
            while (states.TryDequeue(out string state))
            {
                try { Apply(state); }
                catch (Exception e) { Plugin.Log.LogWarning("K9 state couldn't be read: " + e.Message); }
            }
            while (replies.TryDequeue(out string reply))
            {
                try { ApplyCounters(JObject.Parse(reply)["counters"] as JObject); }
                catch (Exception e) { Plugin.Log.LogWarning("K9 reply couldn't be read: " + e.Message); }
            }
        }

        // The dog objectives' counters, so the quest screen and its notifications move as soon as the server counts.
        private static void ApplyCounters(JObject counters)
        {
            if (counters == null) return;
            var profiles = new HashSet<Profile>();
            if (SessionProfile != null) profiles.Add(SessionProfile);
            if (GamePlayerOwner.MyPlayer?.Profile != null) profiles.Add(GamePlayerOwner.MyPlayer.Profile);
            foreach (var p in counters.Properties())
            {
                int value = (int?)p.Value ?? 0;
                var id = new MongoID(p.Name);
                foreach (Profile profile in profiles)
                    if (profile.TaskConditionCounters != null && profile.TaskConditionCounters.TryGetValue(id, out TaskConditionCounter counter) && counter.Value < value)
                        counter.Value = value;
            }
        }


        internal const string TacticalCollarItem = "6a5d0c0c4fb9a3c5e1d60016";

        // ---- jacket wear --------------------------------------------------------------------------------------------

        /// <summary>A jacket's armour wear (now/max), remembered between raids; a new jacket starts at its tier's full value.</summary>
        internal static Wear WearOf(string jacketId, float tierDurability)
        {
            if (!Armour.TryGetValue(jacketId, out Wear wear) || wear.Max <= 0) Armour[jacketId] = wear = new Wear { Now = tierDurability, Max = tierDurability };
            return wear;
        }
    }

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
