using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.CameraControl;
using EFT.Hideout;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The kennel's hex on the hideout overview, like the game's own areas: kennel_icon.png (the paw), over the kennel,
    /// with "K9 KENNEL" and its level. It is one of the game's area panels made for the Rest Space (where the kennel's
    /// levels are built); its picture, label, position, click (the kennel's own screen) and hover (the game's outline
    /// on the kennel, not the Rest Space) are the kennel's. The same goes for its entry in the area bar along the bottom,
    /// which sits next to the Rest Space's.
    /// </summary>
    internal static class KennelIcon
    {
        private static readonly FieldInfo Panels = AccessTools.Field(typeof(HideoutScreenRear), "_areaWorldPanels");
        private static HideoutScreenRear screen;
        private static AreaWorldPanel panel;
        private static AreasPanel bar;
        private static AreaPanel barEntry;
        private static Sprite sprite;
        private static bool firstPerson = true, failed;
        private static float nextLook;
        private static Transform kennelHome;

        internal static void Patch(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(AreaPanel), nameof(AreaPanel.PointerEnterHandler)), prefix: new HarmonyMethod(typeof(KennelIcon), nameof(Entered)));
            harmony.Patch(AccessTools.Method(typeof(AreaPanel), nameof(AreaPanel.PointerExitHandler)), prefix: new HarmonyMethod(typeof(KennelIcon), nameof(Left)));
        }

        // Hovering the paw outlines the kennel (the way hovering an area outlines that area), never the Rest Space.
        private static bool Entered(AreaPanel __instance) { if (!Ours(__instance)) return true; Hover(true); return false; }
        private static bool Left(AreaPanel __instance) { if (!Ours(__instance)) return true; Hover(false); return false; }
        private static bool Ours(AreaPanel shown) => shown != null && (shown == panel || shown == barEntry);

        private static void Hover(bool on)
        {
            panel?.AreaIconCreated?.SetHover(on);
            HighLightMesh outline = CameraManager.Instance?.MeshHighlighter;
            if (outline == null) return;
            if (on && kennelHome != null)
            {
                outline.Color = (panel ?? barEntry).Data.HighlightColor;
                outline.Target = kennelHome;
                outline.enabled = true;
            }
            else if (outline.Target == kennelHome) { outline.Target = null; outline.enabled = false; }
        }

        public static void Tick(Transform kennel, int level, Companion companion)
        {
            if (failed) return;
            if (kennel == null) { Close(); return; }
            kennelHome = kennel;
            try
            {
                if (screen == null && Time.unscaledTime >= nextLook) { nextLook = Time.unscaledTime + 1; screen = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>(); }
                if (screen == null || !screen.isActiveAndEnabled) return;
                if (panel == null && !Create()) return;
                Bar(level);
                // Right-clicking the paw moves the kennel, with Hideout Extensions.
                if (level >= 1) HideoutExtensionsLink.Kennel(panel.gameObject, kennel, companion);
                else HideoutExtensionsLink.Unmovable(panel.gameObject, "Build the K9 Kennel first: then it can be moved.");
                if (screen.FirstPerson != firstPerson) { firstPerson = screen.FirstPerson; panel.SetPointOfView(firstPerson); }
                Dress(level);
                Camera camera = CameraManager.Instance?.Camera;
                if (camera == null) return;
                // Placed the way the game places its own: just over the dog's bed, in screen space, with distance as depth.
                Vector3 point = level >= 1 ? kennel.TransformPoint(new Vector3(0, .8f, -.05f)) : kennel.position + Vector3.up * .9f;
                Vector3 view = camera.WorldToViewportPoint(point);
                float distance = (camera.transform.position - point).magnitude * (view.z < 0 ? -1 : 1);
                panel.SetPosition(new Vector3(view.x * Screen.width, view.y * Screen.height, distance));
            }
            catch (Exception e)
            {
                failed = true;
                Plugin.Log.LogWarning("Kennel icon disabled: " + e.Message);
                Close();
            }
        }

        /// <summary>The kennel's own screen (its levels), as clicking its icon opens it; false if the hideout's screen isn't up.</summary>
        internal static bool OpenScreen()
        {
            if (screen == null) screen = UnityEngine.Object.FindObjectOfType<HideoutScreenRear>();
            var rest = screen == null ? null : (Panels.GetValue(screen) as List<AreaWorldPanel>)?.FirstOrDefault(p => p != null && p.Data?.Template?.Type == EAreaType.RestSpace);
            if (rest == null) return false;
            KennelUi.OpenKennel(screen._hideoutScreenOverlay, rest.Data);
            return true;
        }

        private static bool Create()
        {
            var rest = (Panels.GetValue(screen) as List<AreaWorldPanel>)?.FirstOrDefault(p => p != null && p.Data?.Template?.Type == EAreaType.RestSpace);
            if (rest == null) return false;
            panel = UnityEngine.Object.Instantiate(screen._areaPanelTemplate, screen._areaIconsContainer, false);
            panel.name = "K9 Kennel icon";
            var overlay = screen._hideoutScreenOverlay;
            panel.Show(rest.Data, p => KennelUi.OpenKennel(overlay, p.Data));
            firstPerson = screen.FirstPerson;
            panel.SetPointOfView(firstPerson);
            return true;
        }

        private static void Dress(int level)
        {
            Dress(panel, level);
            KennelUi.Enforce(screen._hideoutScreenOverlay, level);
        }

        // The game re-applies the Rest Space's picture and texts whenever that area updates, so they're set every frame.
        internal static void Dress(AreaPanel panel, int level)
        {
            if (panel == null) return;
            if (panel.AreaName != null && panel.AreaName.text != "K9 KENNEL") panel.AreaName.text = "K9 KENNEL";
            AreaIcon icon = panel.AreaIconCreated;
            if (icon == null) return;
            // The paw in the game's own hex, as every area's symbol is.
            Sprite paw = Paw();
            if (paw != null && icon._areaIconImage != null && icon._areaIconImage.sprite != paw)
            {
                icon._areaIconImage.sprite = paw;
                icon._areaIconImage.preserveAspect = true;
            }
            string number = level.ToString("00");
            if (icon._currentAreaLevel != null && icon._currentAreaLevel.text != number) icon._currentAreaLevel.text = number;
        }

        private static Sprite Paw()
        {
            if (sprite != null) return sprite;
            string path = Path.Combine(Plugin.AssetFolder, "kennel_icon.png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true) { name = "K9 Kennel icon" };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) { UnityEngine.Object.Destroy(texture); return null; }
            texture.wrapMode = TextureWrapMode.Clamp;
            sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
            return sprite;
        }

        // The kennel's entry in the area bar along the bottom: a copy of the bar's own, right after the Rest Space's.
        private static void Bar(int level)
        {
            if (bar == null)
            {
                if (Time.unscaledTime < nextBar) return;
                nextBar = Time.unscaledTime + 1;
                bar = UnityEngine.Object.FindObjectOfType<AreasPanel>(true);
                if (bar == null) return;
            }
            if (bar._areaPanelsContainer == null) return;
            // The Rest Space's entry, looked for twice a second rather than every frame.
            if (barRest == null || Time.unscaledTime >= nextBarRest)
            {
                nextBarRest = Time.unscaledTime + .5f;
                barRest = bar._areaPanelsContainer.GetComponentsInChildren<AreaPanel>(true)
                    .FirstOrDefault(p => p != barEntry && p.Data?.Template?.Type == EAreaType.RestSpace);
            }
            AreaPanel rest = barRest;
            if (rest == null) return;
            if (barEntry == null)
            {
                barEntry = UnityEngine.Object.Instantiate(bar._areaPanelTemplate, bar._areaPanelsContainer, false);
                barEntry.name = "K9 Kennel area entry";
                var overlay = screen._hideoutScreenOverlay;
                barEntry.Show(rest.Data, p => KennelUi.OpenKennel(overlay, p.Data));
                HideoutExtensionsLink.Unmovable(barEntry.gameObject, "Move the kennel with the paw over it on the overview.");
            }
            if (!barEntry.gameObject.activeSelf) barEntry.gameObject.SetActive(true);
            // The game re-sorts the bar as areas change: keep the kennel beside the Rest Space.
            int after = rest.transform.GetSiblingIndex() + (barEntry.transform.GetSiblingIndex() < rest.transform.GetSiblingIndex() ? 0 : 1);
            if (barEntry.transform.GetSiblingIndex() != after) barEntry.transform.SetSiblingIndex(after);
            Dress(barEntry, level);
        }
        private static float nextBar, nextBarRest;
        private static AreaPanel barRest;

        public static void Close()
        {
            if (barEntry != null)
            {
                try { barEntry.Close(); } catch (Exception) { }
                if (barEntry != null) UnityEngine.Object.Destroy(barEntry.gameObject);
            }
            barEntry = null; bar = null; barRest = null;
            if (panel != null)
            {
                try { panel.Close(); } catch (Exception) { }
                if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            }
            panel = null; screen = null;
        }
    }
}
