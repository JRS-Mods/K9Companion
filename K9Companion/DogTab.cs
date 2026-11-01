using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// A DOG tab next to OVERALL / GEAR / HEALTH ... on the character screen (menu, hideout and raid). It shows the
    /// game's own gear view with your stash on the right, and replaces the equipment columns with the dog's panel:
    /// the dog's outline with its JACKET slot (the game's own slot view, so you drag jackets and their contents in and
    /// out as usual), its health, hunger, thirst and combat settings. The tab button is a copy of an existing one; no game tab is changed.
    /// </summary>
    internal static class DogTab
    {
        private static readonly FieldInfo TabsField = AccessTools.Field(typeof(InventoryScreen), "_tabs");
        private static readonly FieldInfo ControllerField = AccessTools.Field(typeof(Tab), "Controller");
        private static TabGroup hookedGroup;

        internal static void Patch(Harmony harmony)
        {
            MethodInfo show = AccessTools.GetDeclaredMethods(typeof(InventoryScreen))
                .FirstOrDefault(m => m.Name == nameof(InventoryScreen.Show) && m.GetParameters().Length == 10)
                ?? throw new MissingMethodException(nameof(InventoryScreen), "Show(10 args)");
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(DogTab), nameof(AddTab)));
        }

        private static void AddTab(InventoryScreen __instance)
        {
            try
            {
                var group = (TabGroup)TabsField.GetValue(__instance);
                var tabs = __instance._tabDictionary;
                if (group == null || tabs == null || !tabs.TryGetValue(EInventoryTab.Gear, out Tab gear)) return;
                Tab template = tabs.TryGetValue(EInventoryTab.Prestige, out Tab p) && p != null ? p : tabs[EInventoryTab.Achievements];
                Transform bar = template.transform.parent;
                Tab dog = bar.Find("K9 DOG tab")?.GetComponent<Tab>();
                if (dog == null)
                {
                    dog = UnityEngine.Object.Instantiate(template, bar);
                    dog.name = "K9 DOG tab";
                    foreach (var localized in dog.GetComponentsInChildren<LocalizedText>(true)) localized.enabled = false;
                    foreach (var label in dog.GetComponentsInChildren<TMPro.TMP_Text>(true)) label.text = "DOG";
                }
                Place(dog, template, tabs);
                dog.gameObject.SetActive(true);
                dog.SetInteractable(true);
                dog.UpdateVisual(selected: false);
                dog.Init(new DogTabController(__instance, gear));
                // The screen builds a new tab group each time it opens: move the DOG tab over to it.
                if (hookedGroup != null) dog.OnSelectionChanged -= hookedGroup.SelectionChangedHandler;
                if (!group._tabs.Contains(dog)) group._tabs = group._tabs.Append(dog).ToArray();
                dog.OnSelectionChanged += group.SelectionChangedHandler;
                hookedGroup = group;
            }
            catch (Exception e) { Plugin.Log.LogError("Couldn't add the DOG tab: " + e); }
        }

        // The tab bar places its tabs by hand (no layout group), so a copy lands exactly on the tab it was copied from and
        // hides underneath it. Step it one place to the right, by the same spacing as the last two tabs.
        private static void Place(Tab dog, Tab template, IReadOnlyDictionary<EInventoryTab, Tab> tabs)
        {
            var bar = template.transform.parent;
            var layout = bar.GetComponent<UnityEngine.UI.LayoutGroup>();
            if (layout != null && layout.enabled) { Report(dog, "by the tab bar's layout"); return; }
            var to = (RectTransform)dog.transform; var from = (RectTransform)template.transform;
            Tab before = tabs.Values.Where(t => t != null && t != template && t.transform.parent == bar)
                .OrderBy(t => Mathf.Abs(((RectTransform)t.transform).anchoredPosition.x - from.anchoredPosition.x)).FirstOrDefault();
            float step = before != null ? Mathf.Abs(from.anchoredPosition.x - ((RectTransform)before.transform).anchoredPosition.x) : 0;
            if (step < 1) step = from.rect.width;
            to.anchorMin = from.anchorMin; to.anchorMax = from.anchorMax; to.pivot = from.pivot; to.sizeDelta = from.sizeDelta;
            to.anchoredPosition = from.anchoredPosition + new Vector2(step, 0);
            Report(dog, $"at {to.anchoredPosition}, {step:0} right of {template.name}");
        }

        private static bool reported;
        private static void Report(Tab dog, string where)
        {
            if (reported) return;
            reported = true;
            Transform bar = dog.transform.parent;
            var row = Enumerable.Range(0, bar.childCount).Select(i => bar.GetChild(i)).OfType<RectTransform>()
                .Select(c => $"{c.name}{(c.gameObject.activeSelf ? "" : " (hidden)")} @{c.anchoredPosition}");
            Plugin.Log.LogInfo($"DOG tab added {where}. Tab bar '{bar.name}' ({((RectTransform)bar).rect.size}): {string.Join(", ", row)}");
        }

        internal static ITabController ControllerOf(Tab tab) => ControllerField.GetValue(tab) as ITabController;
    }

    internal sealed class DogTabController : ITabController
    {
        private static readonly FieldInfo ItemContextField = AccessTools.Field(typeof(InventoryScreen), "_itemContext");
        private static readonly FieldInfo ProfileField = AccessTools.Field(typeof(InventoryScreen), "_profile");
        private readonly InventoryScreen screen;
        private readonly Tab gearTab;
        private ITabController gear;
        private readonly List<GameObject> hidden = new List<GameObject>();

        public DogTabController(InventoryScreen screen, Tab gearTab) { this.screen = screen; this.gearTab = gearTab; }

        public void Show()
        {
            // The game's gear view supplies the stash on the right; the equipment columns make way for the dog.
            gear = DogTab.ControllerOf(gearTab);
            gear?.Show();
            ItemsPanel items = screen._itemsPanel;
            hidden.Clear();
            var parts = new Component[] { items._equipmentPanel, items._containers, items._equipmentBuildPanel, items._healthParametersPanel, items._quickUse, items._healthPanel }
                .Where(p => p != null).Select(p => p.gameObject).ToList();
            // Your special slots (their own panel, outside the equipment columns) would show through beside the dog.
            var stashes = new Component[] { items._complexStashPanel, items._simpleStashPanel }.Where(p => p != null).Select(p => p.transform).ToList();
            foreach (Transform t in items.GetComponentsInChildren<Transform>(true))
                if (t.name.IndexOf("special", StringComparison.OrdinalIgnoreCase) >= 0 && !stashes.Any(s => t.IsChildOf(s))
                    && !parts.Any(p => t.IsChildOf(p.transform)) && !parts.Any(p => p.transform.IsChildOf(t)))
                    parts.Add(t.gameObject);
            foreach (GameObject part in parts)
                if (part.activeSelf) { part.SetActive(false); hidden.Add(part); }
            // The game shows some of these a frame later, so the dog's screen keeps all of them hidden while it's open.
            DogScreen.Open(screen, items, ItemContextField.GetValue(screen) as ItemContext, ProfileField.GetValue(screen) as Profile, parts);
        }

        public async Task<bool> TryHide()
        {
            DogScreen.Close();
            foreach (GameObject part in hidden) if (part != null) part.SetActive(true);
            hidden.Clear();
            return gear == null || await gear.TryHide();
        }
    }
}
