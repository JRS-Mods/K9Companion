using System;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Hideout;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// The K9 Kennel in the hideout's own Rest Space screen. The server mod lists the kennel's three levels as Rest Space
    /// improvements, so the game shows their materials and timers and takes the materials itself; it shows improvements
    /// without a name, so each gets a heading with what it adds, and a level that isn't unlocked yet (quest or Dr. Yang
    /// loyalty) says why and can't be started. The server checks the same rules.
    /// The kennel's levels show only in the kennel's own screen (its paw icon on the overview opens the Rest Space's
    /// screen as "K9 KENNEL", with nothing but the three levels); the Rest Space's screen itself stays as the game has it.
    /// </summary>
    internal static class KennelUi
    {
        private static readonly FieldInfo ImprovementField = AccessTools.Field(typeof(ImprovementView), "_improvement");
        private static readonly FieldInfo SubstrateField = AccessTools.Field(typeof(HideoutScreenOverlay), "_areaSubstrate");

        // The area screen is showing the kennel (opened from its icon) rather than the Rest Space.
        internal static bool KennelView;
        private static bool opening;

        private static readonly string[] Features =
        {
            "Dog bed and bowls, food and water, the dog's health and condition, deployment",
            "Armour locker, jacket repairs, 2 equipment presets, better rest",
            "Veterinary station (+50% healing), 4 presets, faster recovery, cosmetics, premium storage",
        };

        internal static void Patch(Harmony harmony)
        {
            MethodInfo show = AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.Show))
                              ?? throw new MissingMethodException(nameof(ImprovementView), nameof(ImprovementView.Show));
            MethodInfo can = AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.CanImprove))
                             ?? throw new MissingMethodException(nameof(ImprovementView), nameof(ImprovementView.CanImprove));
            if (ImprovementField == null) throw new MissingFieldException(nameof(ImprovementView), "_improvement");
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(KennelUi), nameof(Shown)));
            harmony.Patch(can, postfix: new HarmonyMethod(typeof(KennelUi), nameof(Allowed)));
            harmony.Patch(AccessTools.Method(typeof(ImprovementView), nameof(ImprovementView.UpdateImprovementInfo)), postfix: new HarmonyMethod(typeof(KennelUi), nameof(Status)));
            harmony.Patch(AccessTools.Method(typeof(AreaScreenSubstrate), nameof(AreaScreenSubstrate.SelectArea)), prefix: new HarmonyMethod(typeof(KennelUi), nameof(Selecting)));
            if (SubstrateField == null) throw new MissingFieldException(nameof(HideoutScreenOverlay), "_areaSubstrate");
        }

        private static int Level(Improvement improvement) => improvement == null ? 0 : Array.IndexOf(K9.KennelIds, improvement.Id) + 1;

        /// <summary>Why a kennel level can't be started yet, or null.</summary>
        internal static string Locked(int level, Profile profile)
        {
            K9.EnsureLoaded();
            if (!K9.Owned) return "Needs a dog: Dr. Yang's quest Trust Is Earned";
            int built = K9.Kennel(profile);
            if (level == 1) return K9.QuestDone(profile, K9.TrustIsEarned) || built >= 1 ? null : "Complete Dr. Yang's quest Trust Is Earned";
            if (built < level - 1) return "Build the " + K9.KennelNames[level - 2] + " first";
            int loyalty = level == 2 ? 2 : 4;
            return K9.YangLoyalty(profile) < loyalty ? "Requires Dr. Yang loyalty level " + loyalty : null;
        }

        private static void Allowed(ImprovementView __instance, ref bool __result)
        {
            int level = Level(ImprovementField.GetValue(__instance) as Improvement);
            if (level > 0 && __result && Locked(level, K9.SessionProfile) != null) __result = false;
        }

        /// <summary>The kennel's screen: the Rest Space's area screen, opened in place (no camera move) as the kennel.</summary>
        internal static void OpenKennel(HideoutScreenOverlay overlay, AreaData rest)
        {
            var substrate = SubstrateField.GetValue(overlay) as AreaScreenSubstrate;
            opening = true;
            if (substrate != null) substrate.SelectArea(rest);
            else { opening = false; Plugin.Log.LogWarning("K9 Kennel screen: the area screen isn't ready."); }
        }

        private static readonly System.Collections.Generic.List<GameObject> hiddenParts = new System.Collections.Generic.List<GameObject>();

        private static bool reported;

        private static void Selecting()
        {
            KennelView = opening; opening = false; reported = false;
            // Whatever the kennel's screen hid comes back for the next area shown.
            foreach (var part in hiddenParts) if (part != null) part.SetActive(true);
            hiddenParts.Clear();
        }

        private static void Hide(GameObject part)
        {
            if (part == null || !part.activeSelf) return;
            part.SetActive(false);
            hiddenParts.Add(part);
        }

        // Each area screen shows either the kennel's levels or the rest of the Rest Space's, never both.
        private static bool Belongs(int level) => KennelView ? level > 0 : level == 0;

        private static void Status(ImprovementView __instance)
        {
            var improvement = ImprovementField.GetValue(__instance) as Improvement;
            if (Level(improvement) > 0 && improvement.Completed && __instance._status != null) __instance._status.text = "Built";
        }

        /// <summary>Every frame on the overview: while the kennel's screen is open, it shows nothing but the kennel.</summary>
        internal static void Enforce(HideoutScreenOverlay overlay, int level)
        {
            if (!KennelView || overlay == null) return;
            var substrate = SubstrateField.GetValue(overlay) as AreaScreenSubstrate;
            if (substrate == null || !substrate.isActiveAndEnabled) { KennelView = false; return; }
            KennelIcon.Dress(substrate._areaPanel, level);
            // Everything but the levels list (and whatever holds it): the Rest Space's own panels and its upgrade button.
            var lists = substrate.GetComponentsInChildren<ImprovementPanel>(true);
            foreach (var mb in substrate.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (!(mb is IAreaRelatedPanel) || mb is ImprovementPanel) continue;
                if (lists.Any(l => l.transform.IsChildOf(mb.transform) || mb.transform.IsChildOf(l.transform))) continue;
                Hide(mb.gameObject);
            }
            if (substrate._actionButton != null) Hide(substrate._actionButton.gameObject);
            if (!reported && lists.Length > 0)
            {
                reported = true;
                Plugin.Log.LogInfo($"K9 Kennel screen: {lists.Length} level list(s), {lists.Sum(l => l.GetComponentsInChildren<ImprovementView>(true).Count(v => v.gameObject.activeSelf))} level(s) showing; hid {string.Join(", ", hiddenParts.Select(p => p.name))}.");
            }
            foreach (var panel in substrate.GetComponentsInChildren<ImprovementPanel>(true))
                foreach (var text in panel.GetComponentsInChildren<TMP_Text>(true))
                    if (text.GetComponentInParent<ImprovementView>() == null && text.text != "K9 KENNEL UPGRADES") text.text = "K9 KENNEL UPGRADES";
        }

        private static void Shown(ImprovementView __instance, Improvement improvement)
        {
            try
            {
                int level = Level(improvement);
                if (!Belongs(level)) { __instance.gameObject.SetActive(false); return; }
                if (!__instance.gameObject.activeSelf) __instance.gameObject.SetActive(true);
                if (level == 0) return;
                Transform old = __instance.transform.Find("K9 kennel heading");
                if (old != null) UnityEngine.Object.Destroy(old.gameObject);
                var go = new GameObject("K9 kennel heading", typeof(RectTransform));
                var rect = (RectTransform)go.transform;
                rect.SetParent(__instance.transform, false);
                rect.SetAsFirstSibling();
                // In a layout the heading takes its own row; otherwise it sits just above the view.
                var element = go.AddComponent<LayoutElement>(); element.minHeight = element.preferredHeight = 46;
                if (__instance.GetComponent<LayoutGroup>() == null)
                {
                    rect.anchorMin = new Vector2(0, 1); rect.anchorMax = new Vector2(1, 1); rect.pivot = new Vector2(0, 0);
                    rect.anchoredPosition = new Vector2(8, 2); rect.sizeDelta = new Vector2(-16, 46);
                }
                var text = go.AddComponent<TextMeshProUGUI>();
                TMP_Text sample = __instance._status != null ? (TMP_Text)__instance._status : __instance.GetComponentsInChildren<TMP_Text>(true).FirstOrDefault();
                if (sample != null) text.font = sample.font;
                text.fontSize = 15; text.enableWordWrapping = true; text.raycastTarget = false; text.alignment = TextAlignmentOptions.BottomLeft;
                string locked = Locked(level, K9.SessionProfile);
                text.text = $"<b>K9 KENNEL · LEVEL {level}: {K9.KennelNames[level - 1].ToUpperInvariant()}</b>\n<size=85%><color=#a8a69a>{Features[level - 1]}</color>" +
                            (locked == null || improvement.Completed || improvement.Processing ? "" : $"  <color=#c9705f>· LOCKED: {locked}</color>") + "</size>";
                __instance.ButtonUpdate();
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 Kennel heading: " + e.Message); }
        }
    }
}
