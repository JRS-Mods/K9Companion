using System;
using System.Collections.Generic;
using EFT;
using EFT.InputSystem;
using EFT.UI.Gestures;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// Puts the dog's commands into the game's own Y (gesture) menu as a DOG group, next to COMMAND, REACTION and the rest.
    /// The group is built with the menu's own CreatePhraseGroup, so it looks, opens and is selected exactly like the others
    /// (hover, click or release Y). Commands can also be bound to F-keys with a right-click, the same way as phrases.
    /// Each command uses an id far outside the game's phrase range; picking one is caught before the game would try to say it.
    /// </summary>
    internal static class DogMenu
    {
        private const int Base = 7300;
        private static readonly DogOrder[] Orders = { DogOrder.OverThere, DogOrder.ComeHere, DogOrder.Sit, DogOrder.LieDown, DogOrder.OpenBag, DogOrder.Feed, DogOrder.Water, DogOrder.GoToBed, DogOrder.Stay, DogOrder.Follow, DogOrder.Search, DogOrder.FetchMeds, DogOrder.FetchFood, DogOrder.AttackMode, DogOrder.ApproachMode };
        private static readonly string[] Labels = { "Over there (attack)", "Come here", "Sit", "Lie down", "Open jacket", "Feed", "Give water", "Go to bed", "Stay", "Follow", "Search (removed)", "Fetch (removed)", "Fetch (removed)", "When to attack", "Approach" };
        // New commands go on the end: the ids (Base + position) are what F-key bindings remember. Search and the two
        // fetches were taken out in 0.30: their places stay (an old binding just does nothing) but they're not in the menu.
        private static readonly HashSet<DogOrder> Retired = new HashSet<DogOrder> { DogOrder.Search, DogOrder.FetchMeds, DogOrder.FetchFood };
        // Not in the menu either: Go to bed is for the hideout, where there is no Y menu (the DOG tab and the kennel do it).
        private static readonly HashSet<DogOrder> NotInMenu = new HashSet<DogOrder>(Retired) { DogOrder.GoToBed };

        private static bool IsDog(int id) => id >= Base && id < Base + Orders.Length;

        internal static void Patch(Harmony harmony)
        {
            Unbind();
            // F1-F12: the game's bind list shows each bound entry by name and greys out phrases the character can't say.
            harmony.Patch(Method(typeof(GestureCommands), nameof(GestureCommands.GetCommandName)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(BindName)));
            harmony.Patch(Method(typeof(GesturesBindItem), nameof(GesturesBindItem.UpdateVisual)),
                postfix: new HarmonyMethod(typeof(DogMenu), nameof(BindAvailable)));
            harmony.Patch(Method(typeof(GesturesMenu), nameof(GesturesMenu.InitPhraseGroups)),
                postfix: new HarmonyMethod(typeof(DogMenu), nameof(AddGroup)));
            harmony.Patch(Method(typeof(GesturesMenu), nameof(GesturesMenu.Show)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(MakeAvailable)), postfix: new HarmonyMethod(typeof(DogMenu), nameof(Position)));
            harmony.Patch(Method(typeof(GestureBaseItem), nameof(GestureBaseItem.ShowInternal)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(Label)));
            harmony.Patch(Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.PlayPhraseOrGesture)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(Picked)));
        }

        // F-keys still bound to a command that's no longer in the menu (the old search and fetches, Go to bed) are
        // unbound, in the game's own saved bindings (PMC and Scav), before the game reads them.
        private static void Unbind()
        {
            var dead = new HashSet<int>();
            for (int i = 0; i < Orders.Length; i++) if (NotInMenu.Contains(Orders[i])) dead.Add(Base + i);
            foreach (string key in new[] { "BindCommands", "ScavBindCommands" })
            {
                try
                {
                    string json = PlayerPrefs.GetString(key);
                    if (string.IsNullOrEmpty(json)) continue;
                    var binds = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var gone = new List<string>();
                    foreach (var p in binds.Properties())
                        if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Integer && dead.Contains((int)p.Value)) gone.Add(p.Name);
                    if (gone.Count == 0) continue;
                    foreach (string name in gone) binds.Remove(name);
                    PlayerPrefs.SetString(key, binds.ToString(Newtonsoft.Json.Formatting.None));
                    PlayerPrefs.Save();
                    Plugin.Log.LogInfo($"Unbound {gone.Count} F-key(s) from dog commands that are no longer in the menu ({key}: {string.Join(", ", gone)}).");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Couldn't tidy the dog's F-key bindings ({key}): {e.Message}"); }
            }
        }

        private static System.Reflection.MethodInfo Method(Type type, string name)
            => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

        private static void AddGroup(GesturesMenu __instance)
        {
            try
            {
                var shown = new List<EPhraseTrigger>();
                for (int i = 0; i < Orders.Length; i++) if (!NotInMenu.Contains(Orders[i])) shown.Add((EPhraseTrigger)(Base + i));
                EPhraseTrigger[] phrases = shown.ToArray();
                GesturesAudioItem group = __instance.CreatePhraseGroup("DOG", __instance._commonGroupTemplate, phrases);
                // Kept out of the menu's fixed slot layout (which put it in the middle, under the gestures) and placed
                // by hand in the free spot at the bottom of the ring each time the menu opens.
                var element = group.gameObject.GetComponent<LayoutElement>() ?? group.gameObject.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                Groups[__instance] = (RectTransform)group.transform;
            }
            catch (Exception e) { Plugin.Log.LogError("Could not add the DOG group to the gesture menu: " + e); }
        }

        private static readonly Dictionary<GesturesMenu, RectTransform> Groups = new Dictionary<GesturesMenu, RectTransform>();

        private static readonly System.Reflection.FieldInfo NameField = AccessTools.Field(typeof(GesturesAudioItem), "_nameText");

        // Below the middle of the ring, between its two lower hexagons: 70% of the way down to where the top hexagon
        // (REACTION) would be mirrored, so the command list it opens stays clear of the F-key list under the menu. Placed
        // by the hexagons' own centres (their labels) in the ring's own space, so the group's pivot, size, screen
        // resolution and UI scale don't matter.
        private const float DownFromMiddle = .7f;
        private static void Position(GesturesMenu __instance)
        {
            if (!Groups.TryGetValue(__instance, out RectTransform dog) || dog == null) return;
            var ring = (RectTransform)__instance._audioContainer;
            LayoutRebuilder.ForceRebuildLayoutImmediate(ring);
            RectTransform top = null; Vector2 topCentre = default;
            var others = new List<(Vector2 centre, RectTransform group)>();
            for (int i = 0; i < ring.childCount; i++)
            {
                if (!(ring.GetChild(i) is RectTransform group) || group == dog || !group.gameObject.activeInHierarchy) continue;
                Vector2 c = Centre(ring, group);
                others.Add((c, group));
                if (top == null || c.y > topCentre.y) { top = group; topCentre = c; }
            }
            if (top == null) return;
            Vector2 middle = ring.rect.center;
            Vector2 target = new Vector2(topCentre.x, middle.y - (topCentre.y - middle.y) * DownFromMiddle);
            // The game's SITUATIONAL group sits there (it reports itself shown even when empty): the DOG group keeps the spot,
            // and that one moves a row down, below the F-key list's top (it only has entries now and then).
            float step = Mathf.Max(60, top.rect.height * 1.15f);
            string moved = null;
            foreach (var (c, group) in others)
                if ((c - target).magnitude < step * .6f) { group.localPosition += Vector3.down * step; moved = group.name; }
            dog.anchorMin = dog.anchorMax = top.anchorMin;
            Vector2 now = Centre(ring, dog);
            dog.localPosition += (Vector3)(target - now);
            if (!logged) { logged = true; Plugin.Log.LogInfo($"DOG group centred at {Centre(ring, dog)} (ring middle {middle}, top group {top.name} at {topCentre})" + (moved != null ? $"; {moved} moved down a row." : ".")); }
        }

        // Where a group's hexagon is drawn, in the ring's own coordinates: the middle of its name label.
        private static Vector2 Centre(RectTransform ring, RectTransform group)
        {
            var label = (NameField?.GetValue(group.GetComponent<GesturesAudioItem>()) as Component)?.transform as RectTransform;
            RectTransform at = label != null ? label : group;
            return ring.InverseTransformPoint(at.TransformPoint(at.rect.center));
        }
        private static bool logged;

        // The menu hides phrases the character can't say; the dog's commands are always listed (greyed out when unavailable).
        // Four underscores: Harmony strips three, leaving the field's own name, _availablePhrases.
        private static void MakeAvailable(HashSet<EPhraseTrigger> ____availablePhrases)
        {
            if (____availablePhrases == null) return;
            for (int i = 0; i < Orders.Length; i++) ____availablePhrases.Add((EPhraseTrigger)(Base + i));
        }

        // Runs as each item is shown: names the dog's commands and greys out the ones that can't be given right now.
        private static void Label(GestureBaseItem __instance, ref string caption)
        {
            if (!(__instance is GesturesAudioSubItem item) || !IsDog((int)item.PhraseTrigger)) return;
            int i = (int)item.PhraseTrigger - Base;
            Companion dog = Companion.Current;
            caption = dog?.NeedsLabelFor(Orders[i]) ?? Labels[i];
            string why = null;
            bool ok = dog != null && dog.Available(Orders[i], out why);
            if (dog == null) why = "no dog";
            item._textField.text = ok ? caption : "<color=#7d7d7d>" + caption + "</color> <size=75%><color=#c9705f>" + why + "</color></size>";
        }

        private static bool BindName(int index, ref string __result)
        {
            if (!IsDog(index)) return true;
            __result = "Dog: " + Labels[index - Base];
            return false;
        }

        private static void BindAvailable(GesturesBindItem __instance, int index)
        {
            if (IsDog(index)) __instance.IsAvailable = true;
        }

        // A dog command picked in the menu, or its F-key pressed (the game's F-key handler plays bound entries through the
        // same method).
        private static bool Picked(int actionId)
        {
            if (!IsDog(actionId)) return true;
            Companion dog = Companion.Current;
            if (dog != null) dog.Order(Orders[actionId - Base]);
            return false;
        }
    }
}
