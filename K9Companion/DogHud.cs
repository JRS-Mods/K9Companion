using System;
using EFT.CameraControl;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// Where the dog's camera feed goes by default: under your health figure, top left. The game's status effects
    /// (bleeding, pain, fractures) line up beside the figure, so beside it is taken; under it is free. The figure slides
    /// up off the screen while you're unhurt, so it's measured only while it's actually on screen (the lowest it has been
    /// seen is kept); until then, it's reckoned from the figure's own size.
    /// </summary>
    internal static class HudLayout
    {
        private static float measuredAt = -10, seenBottom;
        private static Vector2 under;
        private static readonly Vector3[] corners = new Vector3[4];

        internal static float Gap => 8 * Screen.height / 1080f;

        /// <summary>The top left of the space under your health figure, in pixels from the screen's top left corner.</summary>
        internal static Vector2 UnderHealth
        {
            get
            {
                if (Time.unscaledTime - measuredAt < 1) return under;
                measuredAt = Time.unscaledTime;
                // Without the game's panel: about where it ends at the game's default interface scale.
                float x = 16, y = Mathf.Max(Screen.height * .22f, seenBottom);
                try
                {
                    CharacterHealthPanel panel = MonoBehaviourSingleton<CommonUI>.Instantiated ? MonoBehaviourSingleton<CommonUI>.Instance.EftBattleUIScreen?._characterHealthPanel : null;
                    var figure = panel == null ? null : panel._background != null ? panel._background.rectTransform : panel.transform as RectTransform;
                    if (figure != null && ScreenRect(figure, out Rect r) && r.height >= Screen.height * .08f && r.xMin >= 0 && r.xMin < Screen.width * .3f)
                    {
                        x = Mathf.Max(8, r.xMin);
                        bool onScreen = r.yMax <= Screen.height + 1 && r.yMin >= Screen.height * .4f;
                        if (onScreen) seenBottom = Mathf.Max(seenBottom, Screen.height - r.yMin + Gap);
                        // Not seen yet (slid away): its own height, and a little for the margin above it.
                        y = seenBottom > 0 ? seenBottom : Mathf.Max(y, r.height + Screen.height * .04f + Gap);
                    }
                }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't find your health figure on screen (the dog camera goes top left): " + e.Message); }
                under = new Vector2(x, y);
                return under;
            }
        }

        // A panel's rectangle on screen, in pixels from the bottom left (Unity's screen space).
        private static bool ScreenRect(RectTransform rect, out Rect result)
        {
            result = default;
            Canvas canvas = null;
            for (Transform t = rect; t != null; t = t.parent)
                if (t.GetComponent<Canvas>() is Canvas c) canvas = c;   // the outermost one
            if (canvas == null) return false;
            Camera camera = canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera;
            rect.GetWorldCorners(corners);
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            foreach (Vector3 corner in corners)
            {
                Vector2 p = RectTransformUtility.WorldToScreenPoint(camera, corner);
                minX = Mathf.Min(minX, p.x); minY = Mathf.Min(minY, p.y); maxX = Mathf.Max(maxX, p.x); maxY = Mathf.Max(maxY, p.y);
            }
            result = Rect.MinMaxRect(minX, minY, maxX, maxY);
            return true;
        }
    }

    /// <summary>
    /// The dog's name and health floating over its head in a raid, while it can be hurt (combat mode). It's drawn over
    /// everything, so you see it through walls and from across the map: a bar in green, amber or red, the percentage,
    /// and DOWN with the time left to revive it, or KILLED. A little smaller the further off it is. Display > Dog health bar.
    /// </summary>
    internal sealed class DogHud : MonoBehaviour
    {
        internal Companion Dog;
        private GameObject overlay;
        private RectTransform frame, fill;
        private Image fillImage;
        private TextMeshProUGUI nameLabel, valueLabel;
        private string shownName, shownValue;
        private float shownShare = -1, shownSize = -1;

        private static readonly Color Good = new Color(.45f, .68f, .32f), Caution = new Color(.86f, .62f, .2f), Danger = new Color(.8f, .25f, .2f), Gone = new Color(.4f, .4f, .4f);

        private void LateUpdate()
        {
            try
            {
                var owner = Dog?.Owner;
                Camera view = CameraManager.Instance?.Camera;
                bool show = Plugin.HealthBar.Value && Dog != null && Dog.CanBeHurt && owner != null && owner.HealthController != null
                            && owner.HealthController.IsAlive && !owner.IsInventoryOpened && view != null;
                // Through the viewport, so it lands right whatever the game renders at (resolution scaling, upscalers).
                Vector3 viewport = show ? view.WorldToViewportPoint(Dog.OverheadPoint) : Vector3.zero;
                Vector3 screen = new Vector3(viewport.x * Screen.width, viewport.y * Screen.height, viewport.z);
                // Behind you: nowhere to draw it.
                if (screen.z <= 0) show = false;
                if (!show)
                {
                    if (overlay != null && overlay.activeSelf) overlay.SetActive(false);
                    return;
                }
                Build();
                if (!overlay.activeSelf) overlay.SetActive(true);

                // Full size up close, down to 60% at 100 m and beyond.
                float size = Screen.height / 1080f * Mathf.Lerp(1f, .6f, Mathf.InverseLerp(5, 100, screen.z));
                if (Mathf.Abs(size - shownSize) > .01f)
                {
                    shownSize = size;
                    frame.sizeDelta = new Vector2(150, 30) * size;
                    nameLabel.fontSize = valueLabel.fontSize = 13 * size;
                    var track = (RectTransform)fill.parent;
                    track.sizeDelta = new Vector2(-10, 5) * size;
                    track.anchoredPosition = new Vector2(0, 4) * size;
                }
                frame.anchoredPosition = new Vector2(Mathf.Round(screen.x), Mathf.Round(screen.y));

                float max = Mathf.Max(1, Dog.HealthMax);
                float share = Dog.IsDead ? 0 : Mathf.Clamp01(Dog.HealthNow / max);
                string value = Dog.Killed ? "KILLED"
                    : Dog.Downed ? (Dog.DownedSecondsLeft >= 0 ? "DOWN  " + Clock(Dog.DownedSecondsLeft) : "DOWN")
                    : Mathf.CeilToInt(share * 100) + "%";
                string name = (Plugin.DogName.Value ?? "K9").ToUpperInvariant();
                if (name != shownName) { shownName = name; nameLabel.text = name; }
                if (value != shownValue) { shownValue = value; valueLabel.text = value; }
                if (!Mathf.Approximately(share, shownShare)) { shownShare = share; fill.anchorMax = new Vector2(share, 1); }
                Color colour = Dog.IsDead ? (Dog.Downed ? Danger : Gone) : share < .25f ? Danger : share < .7f ? Caution : Good;
                if (fillImage.color != colour) fillImage.color = colour;
                valueLabel.color = Dog.IsDead ? colour : new Color(.92f, .92f, .88f);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError("The dog's health bar failed and is off for this raid: " + e);
                enabled = false;
                if (overlay != null) overlay.SetActive(false);
            }
        }

        private static string Clock(float seconds)
        {
            int s = Mathf.CeilToInt(seconds);
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        private void Build()
        {
            if (overlay != null) return;
            overlay = new GameObject("K9 dog health");
            DontDestroyOnLoad(overlay);
            var canvas = overlay.AddComponent<Canvas>();
            // Under the dog camera's feed and the game's own screens (sorting 30 and up).
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 25;
            frame = Box(overlay.transform, "Frame", new Color(0, 0, 0, .5f));
            // Placed in screen pixels from the bottom left, centred over the dog's head.
            frame.anchorMin = frame.anchorMax = Vector2.zero; frame.pivot = new Vector2(.5f, 0);
            nameLabel = Label(frame, TextAlignmentOptions.TopLeft);
            valueLabel = Label(frame, TextAlignmentOptions.TopRight);
            // The bar: a dark track along the bottom, and the fill in it from the left.
            RectTransform track = Box(frame, "Track", new Color(.12f, .12f, .12f, .9f));
            track.anchorMin = new Vector2(0, 0); track.anchorMax = new Vector2(1, 0); track.pivot = new Vector2(.5f, 0);
            fill = Box(track, "Fill", Good);
            fill.anchorMin = Vector2.zero; fill.anchorMax = Vector2.one; fill.offsetMin = fill.offsetMax = Vector2.zero; fill.pivot = new Vector2(0, .5f);
            fillImage = fill.GetComponent<Image>();
            shownSize = -1; shownShare = -1; shownName = shownValue = null;
        }

        private static RectTransform Box(Transform parent, string name, Color colour)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var image = go.AddComponent<Image>(); image.color = colour; image.raycastTarget = false;
            return (RectTransform)go.transform;
        }

        private static TextMeshProUGUI Label(RectTransform parent, TextAlignmentOptions alignment)
        {
            var go = new GameObject("Label", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var rect = (RectTransform)go.transform;
            rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(5, 0); rect.offsetMax = new Vector2(-5, -2);
            var text = go.AddComponent<TextMeshProUGUI>();
            text.color = new Color(.92f, .92f, .88f); text.alignment = alignment; text.raycastTarget = false;
            text.enableWordWrapping = false; text.fontStyle = FontStyles.Bold;
            return text;
        }

        private void OnDestroy()
        {
            if (overlay != null) Destroy(overlay);
        }
    }
}
