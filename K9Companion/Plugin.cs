using System;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    [BepInPlugin(Id, "JRS-K9Companion", Version)]
    [BepInDependency("jrs.hideoutextensions", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.fika.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "jrs.k9companion";
        public const string Version = "1.1.0";
        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> Active, CombatMode, SurvivalNeeds, AutoFeedInHideout, CarryOverInjuries;
        internal static ConfigEntry<DogApproach> ApproachMode;
        internal static ConfigEntry<float> AutoRushRange, DefendRange;
        internal static ConfigEntry<float> HungerDecayRate, ThirstDecayRate;
        internal static ConfigEntry<float> OrderRange;
        internal static ConfigEntry<int> ValuableAlert;
        internal static ConfigEntry<float> BiteStagger, BiteCooldown, Distance, Scale, WalkSpeed, RunSpeed, SprintSpeed, RunDistance, SprintDistance, DogHealth, EngageDistance, BitesToKill, BossBitesToKill;
        internal static ConfigEntry<Aggression> DogAggression;
        internal static ConfigEntry<KeyboardShortcut> Recall;
        internal static string AssetFolder;
        private Harmony harmony;
        private static Plugin instance;
        // What runs the dog's background jobs.
        internal static MonoBehaviour Runner => instance;
        internal static void SaveName(string name) { DogName.Value = name; instance.Config.Save(); }        private float nextHideout, nextOwnerCheck;
        private bool ready;
        internal static ConfigEntry<bool> Hideout, Sounds, RareWhine, Roam;
        internal static ConfigEntry<string> DogName;
        internal static ConfigEntry<float> Volume, BreathingVolume;
        internal static ConfigEntry<KeyboardShortcut> Stay, Lie, Rename, Pet, Feed, Water, DogCamKey;
        internal static ConfigEntry<bool> DogCamShow;
        internal static ConfigEntry<float> DogCamSize;
        internal static ConfigEntry<int> DogCamFps;
        internal static ConfigEntry<DogCamQuality> DogCamQuality;
        // Experiments with the feed's picture, off unless switched on (F12): the game's grass, your view's lighting, cloud
        // and water steps, and its extra colour effects. Each can be flipped in a raid to see what it does.
        internal static ConfigEntry<bool> DogCamGrass;
        internal static ConfigEntry<DogCamPlace> DogCamPosition;
        private ConfigEntry<KeyboardShortcut> Key(string name, KeyCode key, string description, params KeyCode[] modifiers)
            => Config.Bind("Controls", name, new KeyboardShortcut(key, modifiers), description);

        private void Awake()
        {
            Log = Logger; instance = this;
            Active = Config.Bind("General", "Enabled", true, "Spawn your companion in raids.");
            Distance = Config.Bind("Movement", "FollowDistance", 2f,
                new ConfigDescription("Where the dog stops beside you, in metres.", new AcceptableValueRange<float>(1.5f, 8f)));
            RunDistance = Config.Bind("Movement", "RunDistance", 6f,
                new ConfigDescription("Further than this from you the dog runs instead of walking.", new AcceptableValueRange<float>(3f, 20f)));
            SprintDistance = Config.Bind("Movement", "SprintDistance", 15f,
                new ConfigDescription("Further than this from you the dog sprints to catch up.", new AcceptableValueRange<float>(8f, 40f)));
            WalkSpeed = Config.Bind("Movement", "WalkSpeedMultiplier", 1f,
                new ConfigDescription("Scales walking and sneaking speed; animation playback follows.", new AcceptableValueRange<float>(.5f, 2f)));
            RunSpeed = Config.Bind("Movement", "RunSpeedMultiplier", 1f,
                new ConfigDescription("Scales trotting and running speed; animation playback follows.", new AcceptableValueRange<float>(.5f, 2f)));
            SprintSpeed = Config.Bind("Movement", "SprintSpeedMultiplier", 1f,
                new ConfigDescription("Scales sprinting speed; animation playback follows.", new AcceptableValueRange<float>(.5f, 2f)));
            CombatMode = Config.Bind("Combat", "CombatMode", false,
                "Off: the dog only follows and obeys commands. AI ignore it, it cannot attack, and it cannot be hurt or killed. " +
                "On: the dog can attack, hostile AI can shoot it, and it can always be killed.");
            // As a working patrol dog: it alerts its handler and goes in on command (or, trained for handler protection,
            // when you're attacked); it never picks fights on its own unless set to Aggressive.
            DogAggression = Config.Bind("Combat", "Dog aggression", Aggression.Passive,
                "Only used when CombatMode is on. Passive: alerts you to enemies (growls, barks, tells you where) and attacks only when " +
                "ordered (Over There). Defensive: also goes for enemies that target, shoot or hurt you or the dog. Aggressive: also " +
                "attacks any hostile AI it detects within the engagement distance.");
            EngageDistance = Config.Bind("Combat", "Maximum engagement distance", DogRules.DefaultEngage,
                new ConfigDescription("How far from you, in metres, the dog will go after an enemy.", new AcceptableValueRange<float>(5f, 60f)));
            ApproachMode = Config.Bind("Combat", "Approach", DogApproach.Auto,
                "How the dog goes for an enemy. Auto: straight in if it's really close, and works its way in out of sight if it's further off, " +
                "changing as the distance does. Stealth: always works its way in out of sight (behind buildings, walls and bushes), then rushes the " +
                "last few metres. Direct: runs straight at it.");
            AutoRushRange = Config.Bind("Combat", "Auto approach: rush within", 14f, new ConfigDescription(
                "Under the Auto approach, an enemy this close (in metres) gets a straight attack.", new AcceptableValueRange<float>(5f, 40f)));
            DefendRange = Config.Bind("Combat", "Defend range", 100f, new ConfigDescription(
                "With 'defends you' or 'any enemy', how far from you (in metres) the dog goes after someone shooting at you or at it, with no order needed. " +
                "Fights it starts for any other reason stay within the maximum engagement distance.", new AcceptableValueRange<float>(10f, 300f)));
            ValuableAlert = Config.Bind("Search", "Valuable alert (roubles)", 40000, new ConfigDescription(
                "In raids the dog barks once at loot near it (loose or in a container) worth at least this much each, and says what and where. 0 = off.",
                new AcceptableValueRange<int>(0, 1000000)));
            OrderRange = Config.Bind("Combat", "Ordered attack range", 150f, new ConfigDescription(
                "How far away you can send the dog with Over There: at an enemy you aim at (or near), or to hunt where you aim. " +
                "Fights it starts on its own stay within the maximum engagement distance.", new AcceptableValueRange<float>(25f, 300f)));
            BitesToKill = Config.Bind("Combat", "Bites to kill", DogRules.DefaultBites, new ConfigDescription(
                "The dog goes for the throat, under the helmet and any collar: how many bites kill a scav, PMC or player, armour or not. " +
                "A fraction mixes the two either side: 2.5 = two bites for half of them, three for the rest.", new AcceptableValueRange<float>(1f, 10f)));
            BossBitesToKill = Config.Bind("Combat", "Bites to kill a boss", DogRules.DefaultBossBites, new ConfigDescription(
                "The same for bosses (their guards count as everyone else).", new AcceptableValueRange<float>(1f, 20f)));
            BiteCooldown = Config.Bind("Combat", "Bite cooldown", DogRules.DefaultCooldown,
                new ConfigDescription("Seconds between bites.", new AcceptableValueRange<float>(.8f, 5f)));
            BiteStagger = Config.Bind("Combat", "Bite stagger", .5f,
                new ConfigDescription("Seconds a bitten enemy is thrown off (loses its aim, shoots badly) before it turns on the dog. 0 turns it off.", new AcceptableValueRange<float>(0f, 3f)));
            DogHealth = Config.Bind("Combat", "Dog health", DogRules.DefaultHealth,
                new ConfigDescription("Hit points per raid when CombatMode is on.", new AcceptableValueRange<float>(30f, 1000f)));
            SurvivalNeeds = Config.Bind("Survival", "EnableSurvivalNeeds", true,
                "Hunger and thirst. They drop slowly while you play, are saved between raids and restarts, and only ever slow the dog down " +
                "(in combat mode); they never hurt or kill it.");
            HungerDecayRate = Config.Bind("Survival", "HungerDecayRate", 8f,
                new ConfigDescription("Hunger lost per hour of raid time, in percent (half that while resting in the hideout).", new AcceptableValueRange<float>(0f, 50f)));
            ThirstDecayRate = Config.Bind("Survival", "ThirstDecayRate", 12f,
                new ConfigDescription("Thirst lost per hour of raid time, in percent (half that while resting in the hideout).", new AcceptableValueRange<float>(0f, 50f)));
            AutoFeedInHideout = Config.Bind("Survival", "AutoFeedInHideout", true,
                "In the hideout, top up nearly empty dishes from your stash and gear automatically (dog food or meat, and water).");
            CarryOverInjuries = Config.Bind("Survival", "CarryOverInjuries", true,
                "Wounds from a combat-mode raid stay with the dog until they heal (resting at the kennel, or a medical kit in the DOG tab). " +
                "Below 25% health the dog stays home. Off: the dog starts every raid at full health, as before 0.16.");
            CombatMode.SettingChanged += (_, __) => Log.LogInfo("Combat mode " + (CombatMode.Value ? "on" : "off") + ": " + Describe(Rules(false)));
            DogAggression.SettingChanged += (_, __) => Log.LogInfo("Dog aggression: " + Describe(Rules(false)));
            Scale = Config.Bind("Appearance", "Scale", 1f,
                new ConfigDescription("Dog size; applied when it spawns.", new AcceptableValueRange<float>(0.5f, 2f)));
            Recall = Config.Bind("Controls", "Recall", new KeyboardShortcut(KeyCode.F8), "Bring the dog back beside you.");
            Hideout = Config.Bind("Hideout", "Enabled", true, "Spawn in the hideout as well as raids.");
            Roam = Config.Bind("Hideout", "Wander when bored", true, "After resting a while the dog gets up, wanders round the hideout, comes to say hello, then goes back to its bed.");
            DogName = Config.Bind("Appearance", "Name", "K9", "Name on the collar tag and command display.");            Sounds = Config.Bind("Audio", "Enabled", true, "Play companion sounds and recall whistle locally.");
            RareWhine = Config.Bind("Audio", "Rare whining", true, "Occasional quiet whine while resting; suppressed when crouching.");
            Volume = Config.Bind("Audio", "Volume", 0.35f, new ConfigDescription("Local companion sound volume.", new AcceptableValueRange<float>(0, 1)));
            BreathingVolume = Config.Bind("Audio", "Breathing volume", 0.175f, new ConfigDescription(
                "How loud the dog's breathing is, next to its other sounds (it breathes all the time: quietly at rest, harder after running or when hurt). 0 = silent.",
                new AcceptableValueRange<float>(0, 1)));
            Stay = Key("Stay or follow", KeyCode.F7, "Toggle stay and follow.");
            Lie = Key("Lie down", KeyCode.F7, "Lie down here.", KeyCode.LeftShift);
            Rename = Key("Rename dog", KeyCode.F9, "Open the in-game dog naming dialog when nearby.");
            Pet = Key("Pet", KeyCode.F10, "Pet the dog when within 2.5 metres (also in its interact menu).");
            Feed = Key("Food", KeyCode.F9, "Refill the food dish in the hideout.", KeyCode.LeftShift);
            Water = Key("Water", KeyCode.F10, "Refill the water dish in the hideout.", KeyCode.LeftShift);
            DogCamKey = Key("Dog camera", KeyCode.F6, "Show or hide the dog camera feed (raids, when the dog wears a camera).");
            DogCamShow = Config.Bind("Dog camera", "Show feed", true,
                "Top left of the screen in a raid: what the dog's camera sees, when it wears the camera vest or the clip-on action camera.");
            DogCamSize = Config.Bind("Dog camera", "Size", .24f, new ConfigDescription("Height of the feed, as a share of the screen's height.", new AcceptableValueRange<float>(.12f, .45f)));
            DogCamFps = Config.Bind("Dog camera", "Frames per second", 15, new ConfigDescription(
                "How often the feed updates. Each update draws the scene once more, so lower is cheaper.", new AcceptableValueRange<int>(5, 30)));
            DogCamPosition = Config.Bind("Dog camera", "Position", DogCamPlace.NextToHealth,
                "Where the feed shows: top left next to the health figure, top left corner, or top right.");
            DogCamQuality = Config.Bind("Dog camera", "Resolution", K9Companion.DogCamQuality.Medium,
                "Low: 384x216, no shadows or grass, less detail (cheapest). Medium: 640x360 with grass, haze and shadows close by. " +
                "High: as sharp as the feed is on screen (up to 960x540), full detail, shadows further out. Higher costs more.");
            DogCamGrass = Config.Bind("Dog camera", "Grass", true,
                "Draw the game's grass and bushes in the feed (Medium and High). Off is a little cheaper.");
            // After every setting is bound: it updates some of them. A failure here mustn't stop the dog loading.
            try { Retire(); } catch (Exception e) { Logger.LogError("Updating old settings failed (they stay as they were): " + e); }
            AssetFolder = Path.GetDirectoryName(Info.Location);
            foreach (var file in new[] { "k9dog.dog", "k9dog.png" })
                if (!File.Exists(Path.Combine(AssetFolder, file)))
                {
                    Logger.LogError($"Missing {file} in {AssetFolder}. Install the complete package.");
                    return;
                }
            ready = true;
            DogModel.WarmUp(AssetFolder);
            harmony = new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(GameWorld), nameof(GameWorld.OnGameStarted)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(RaidStarted)));
            // Each feature hooks in on its own, so one that fails to patch can't take the others down with it.
            PatchFeature("rename dialog", RenameDialog.PatchInput);
            PatchFeature("DOG gesture menu", DogMenu.Patch);
            PatchFeature("dog jacket raid trip", DogBag.Patch);
            PatchFeature("the map's navmesh on a Fika client", DogNavMesh.Patch);
            PatchFeature("dog gear (jacket slot and icons)", DogGearPatches.Patch);
            PatchFeature("dog interactions (interact key, right-click feeding)", DogInteraction.Patch);
            PatchFeature("dog on the raid screen", DogLobby.Patch);
            PatchFeature("dog gear models (inspect, on the ground)", GearVisuals.Patch);
            PatchFeature("DOG tab", DogTab.Patch);
            PatchFeature("NPC targeting", DogThreat.Patch);
            PatchFeature("the dog hearing gunfire at you", DogThreat.PatchGunfire);
            PatchFeature("K9 quest events (kills with the dog)", K9Events.Patch);
            PatchFeature("K9 Kennel in the Rest Space", KennelUi.Patch);
            PatchFeature("K9 Kennel icon hover", KennelIcon.Patch);
            DogNet.Load();
            Logger.LogInfo($"K9 Companion {Version} loaded from {AssetFolder}.");
        }

        private void PatchFeature(string feature, Action<Harmony> patch)
        {
            try { patch(harmony); }
            catch (Exception e) { Logger.LogError($"The {feature} couldn't be set up and won't work this session: {e}"); }
        }

        // Carries the old follow distance over and drops settings from earlier versions that no longer do anything.
        private void Retire()
        {
            // Version 2: dog health default raised from 150 to 300; an untouched old default is moved up with it.
            var configVersion = Config.Bind("Internal", "Config version", 1, "Used to update old settings. Do not edit.");
            if (configVersion.Value < 2 && Mathf.Approximately(DogHealth.Value, 150f)) DogHealth.Value = DogRules.DefaultHealth;
            // Version 3: the dog attacks only on command by default (it used to default to Defensive).
            if (configVersion.Value < 3 && DogAggression.Value == Aggression.Defensive) DogAggression.Value = Aggression.Passive;
            // Version 4: breathing at half its old default; a bite throws a bot off for less long, so it fights the dog.
            if (configVersion.Value < 4 && Mathf.Approximately(BreathingVolume.Value, .35f)) BreathingVolume.Value = .175f;
            if (configVersion.Value < 4 && Mathf.Approximately(BiteStagger.Value, 1.2f)) BiteStagger.Value = .5f;
            // 0.23.0 put the dog camera on F8, which is Recall.
            if (configVersion.Value < 4 && DogCamKey.Value.MainKey == KeyCode.F8 && !DogCamKey.Value.Modifiers.Any()) DogCamKey.Value = new KeyboardShortcut(KeyCode.F6);
            // Version 5: stealth on or off became an approach with three settings, and Auto is what stealth on now means.
            if (configVersion.Value < 5)
            {
                var stealth = Config.Bind("Combat", "Stealth approach", true);
                if (!stealth.Value) ApproachMode.Value = DogApproach.Direct;
                Config.Remove(stealth.Definition);
            }
            configVersion.Value = 5;
            // Removed in 0.9.0: loot retrieval, the double-tap wheel and the on-screen controls box.
            foreach (string key in new[] { "EnableLootRetrieval", "MaxRetrievalDistance", "MaxItemWeight", "MaxItemSize", "LootWhitelistEnabled", "LootWhitelist" })
                Drop("Loot retrieval", key);
            foreach (string key in new[] { "Enabled", "Double-tap window", "Mouse sensitivity" }) Drop("Command wheel", key);
            Drop("Combat", "Loot retrieval"); Drop("Display", "Show controls"); Drop("Controls", "Show controls");
            // Removed in 0.38.0: a bite's damage became bites to kill.
            Drop("Combat", "Bite damage");
            var legacy = Config.Bind("Following", "Follow distance", 2.5f);
            if (Mathf.Abs(legacy.Value - 2.5f) > .001f) Distance.Value = Mathf.Clamp(legacy.Value, 1.5f, 8f);
            Config.Remove(legacy.Definition);
            Config.Remove(Config.Bind("Combat", "Attack command", false).Definition);
            Config.Remove(Config.Bind("Appearance", "Armour vest", false).Definition);
            Config.Remove(Config.Bind("Controls", "Attack", KeyboardShortcut.Empty).Definition);
            Config.Remove(Config.Bind("Controls", "Fetch loot", KeyboardShortcut.Empty).Definition);
            // Removed in 0.32.0: the dog camera's experiments. Grass is an ordinary Dog camera setting now; the feed gets the
            // rest its own way.
            foreach (string key in new[] { "Grass", "Lighting, clouds and water", "Extra colour effects", "Everything my camera has" })
                Drop("Dog camera (experimental)", key);
            Config.Save();
        }

        // Binding a leftover entry first is what lets Remove take it out of the file.
        private void Drop(string section, string key) => Config.Remove(Config.Bind(section, key, "").Definition);

        // The one place raw combat settings turn into permissions; everything else reads this.
        internal static DogRules Rules(bool hideout) => DogRules.Resolve(Combat, Aggressiveness,
            ServerRules.EngageDistance ?? EngageDistance.Value,
            ServerRules.BitesToKill ?? BitesToKill.Value, ServerRules.BossBitesToKill ?? BossBitesToKill.Value, MaxHealth, !hideout, ServerRules.BiteCooldown ?? BiteCooldown.Value);

        // A setting as it counts: what the server's owner has fixed for everyone (ServerRules), else your own.
        internal static bool Combat => ServerRules.CombatMode ?? CombatMode.Value;
        internal static Aggression Aggressiveness => ServerRules.Aggression ?? DogAggression.Value;
        internal static DogApproach Approaching => ServerRules.Approach ?? ApproachMode.Value;
        internal static float AutoRush => ServerRules.AutoRushRange ?? AutoRushRange.Value;
        // Never less than how far it'd go after anyone anyway.
        internal static float DefendReach => Mathf.Max(ServerRules.DefendRange ?? DefendRange.Value, ServerRules.EngageDistance ?? EngageDistance.Value);
        internal static float OrderReach => ServerRules.OrderRange ?? OrderRange.Value;
        internal static float Stagger => ServerRules.BiteStagger ?? BiteStagger.Value;
        internal static float MaxHealth => ServerRules.DogHealth ?? DogHealth.Value;

        private static string Describe(DogRules r) => r.Combat
            ? $"{r.Aggression}, auto-engage {r.AutoEngage}, cooldown {r.BiteCooldown:0.0} s, engage {r.EngageDistance:0} m, bites to kill {r.BitesToKill:0.#} (bosses {r.BossBitesToKill:0.#}), health {r.Health:0}, mortal"
            : "passive, immortal, not targeted";

        internal static Tuning Tuning => new Tuning
        {
            Walk = WalkSpeed.Value, Run = RunSpeed.Value, Sprint = SprintSpeed.Value, Follow = Distance.Value,
            RunFrom = Mathf.Max(RunDistance.Value, Distance.Value + 1), SprintFrom = Mathf.Max(SprintDistance.Value, RunDistance.Value + 4)
        };

        private static void RaidStarted(GameWorld __instance)
        {
            // The raid screens' preview dog isn't needed once a game world (raid or hideout) is up.
            try { DogLobby.Release(); } catch (Exception e) { Log.LogWarning("Dropping the preview dog: " + e.Message); }
            try
            {
                // A Fika headless host has no player of its own to follow (other players' dogs still show up through DogNet).
                if (DogNet.Headless) return;
                if (!Active.Value || __instance == null ||
                    (string.Equals(__instance.LocationId, "hideout", StringComparison.OrdinalIgnoreCase) || __instance.MainPlayer is HideoutPlayer) ||
                    __instance.GetComponent<Companion>() != null) return;
                bool deployed = DogBag.Deployed ?? K9.NotDeployable() == null;
                DogBag.Deployed = null;
                if (!deployed) { DogPreload.Release(); Log.LogInfo("No dog this raid (" + (K9.NotDeployable() ?? "not deployed") + ")."); return; }
                __instance.gameObject.AddComponent<Companion>();
            }
            catch (Exception e) { Log.LogError("Dog raid setup failed: " + e); }
        }

        // The hideout's dog, once attached (so the scene isn't searched again).

        private static Companion hideoutDog;

        private void Update()
        {
            if (!ready) return;
            K9.Tick();
            if (Companion.Raid == null) K9.Recover();
            if (!Active.Value || !Hideout.Value || Time.unscaledTime < nextHideout) return;
            nextHideout = Time.unscaledTime + 2;
            // Only in the hideout, and only until its dog is there: what follows searches the whole scene, which in a
            // raid (a far bigger scene) was a stall every two seconds.
            if (Companion.Raid != null || (Comfort.Common.Singleton<GameWorld>.Instantiated && !(Comfort.Common.Singleton<GameWorld>.Instance is HideoutGameWorld))) return;
            if (hideoutDog != null && hideoutDog.HideoutOwner != null) return;
            // Only a dog you own lives in the hideout. A new handler's ownership is looked up again now and then, so the
            // dog from Trust Is Earned turns up without a restart.
            if (K9.SessionProfile != null && (!K9.Loaded || (!K9.Owned && Time.unscaledTime >= nextOwnerCheck && FindObjectOfType<HideoutPlayer>() != null)))
            { nextOwnerCheck = Time.unscaledTime + 30; K9.RefreshInBackground(); }
            if (!K9.Owned) return;
            // Hideout does not share the raid GameWorld/MainPlayer lifecycle.
            // The hideout's player exists (switched off) from the loading screen on, while the overview shows; the dog
            // lives on its own object so it's there before you click Enter.
            var dogs = UnityEngine.Object.FindObjectsOfType<Companion>(true);
            hideoutDog = dogs.FirstOrDefault(c => c.HideoutOwner != null);
            var bound = dogs.Select(c => c.HideoutOwner).Where(p => p != null).ToList();
            foreach (var player in UnityEngine.Object.FindObjectsOfType<HideoutPlayer>(true))
            {
                if (player.HealthController == null || bound.Contains(player)) continue;
                var host = new GameObject("K9 hideout companion");
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(host, player.gameObject.scene);
                hideoutDog = host.AddComponent<Companion>();
                hideoutDog.BindHideout(player);
                Log.LogInfo("Hideout companion attached to " + player.name);
            }
        }

        private void OnApplicationQuit() { NeedsStore.Save(); }
        private void OnDestroy() { RenameDialog.Close(); harmony?.UnpatchSelf(); }
    }
}
