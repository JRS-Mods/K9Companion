using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Matchmaker;
using EFT.UI.UI.Matchmaker;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace K9Companion
{
    /// <summary>
    /// The dog on the way into a raid:
    ///  - The last screen (CONFIRMATION, with READY): the dog next to your character, in the gear it wears into the raid,
    ///    how it's doing, and TAKE DOG (the same switch as the config's Enabled).
    ///  - Deploying to the location: the dog in the party list under you, with a paw, its training level and its name.
    ///    Click its name and the big picture shows the dog (drag to turn it); click yours and it's your character again.
    ///    Its eye opens its loadout: jacket, head gear and camera (the jacket opens like a container).
    /// The dog in both is a copy of the raid dog's model on a layer of its own, filmed by its own camera into a texture
    /// shaped like where it's shown, far below the menu scene; it's loaded the first time it's needed and dropped once
    /// the raid has started.
    /// </summary>
    internal static class DogLobby
    {
        private static DogLobbyPanel panel;
        private static DogStage stage;
        private static Sprite paw;

        internal static void Patch(Harmony harmony)
        {
            var show = AccessTools.Method(typeof(MatchMakerAcceptScreen), nameof(MatchMakerAcceptScreen.Show),
                new[] { typeof(IEftSession), typeof(RaidSettings), typeof(RaidSettings) })
                ?? throw new MissingMethodException(nameof(MatchMakerAcceptScreen), "Show(session, raid settings, offline settings)");
            harmony.Patch(show, postfix: new HarmonyMethod(typeof(DogLobby), nameof(Shown)));
            harmony.Patch(AccessTools.Method(typeof(MatchMakerAcceptScreen), nameof(MatchMakerAcceptScreen.Close)),
                prefix: new HarmonyMethod(typeof(DogLobby), nameof(Closed)));
            var deploying = AccessTools.Method(typeof(MatchmakerTimeHasCome), nameof(MatchmakerTimeHasCome.Show),
                new[] { typeof(IEftSession), typeof(RaidSettings), typeof(MatchmakerPlayersController) })
                ?? throw new MissingMethodException(nameof(MatchmakerTimeHasCome), "Show(session, raid settings, matchmaker)");
            harmony.Patch(deploying, postfix: new HarmonyMethod(typeof(DogLobby), nameof(Deploying)));
        }

        // Whether the dog is coming on this raid (it's set, and it's fit to).
        private static bool Coming => K9.Owned && Plugin.Active.Value && K9.NotDeployable() == null;

        internal static DogStage Stage
        {
            get
            {
                if (stage == null) stage = DogStage.Create();
                return stage;
            }
        }

        internal static Sprite Paw
        {
            get
            {
                if (paw != null) return paw;
                string path = Path.Combine(Plugin.AssetFolder, "kennel_icon.png");
                if (!File.Exists(path)) return null;
                var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "K9 paw" };
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true)) return null;
                return paw = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f));
            }
        }

        private static void Shown(MatchMakerAcceptScreen __instance, IEftSession session, RaidSettings raidSettings)
        {
            try
            {
                Closed();
                Profile profile = session?.Profile;
                K9.Refresh();
                if (profile == null || !K9.Owned || __instance._playerModelView == null) return;
                // Fika's server browser takes over this screen: the dog's panel would sit on top of it. TAKE DOG is the
                // config's Enabled, so it can still be set (F12).
                if (DogNet.Installed) return;
                Stage.Dress(raidSettings.IsPmc ? profile : null);
                panel = DogLobbyPanel.Create(__instance, profile, raidSettings.IsPmc, Stage);
            }
            catch (Exception e) { Plugin.Log.LogError("The dog couldn't be shown on the raid screen: " + e); }
        }

        private static void Closed()
        {
            if (panel != null) UnityEngine.Object.Destroy(panel.gameObject);
            panel = null;
        }

        private static void Deploying(MatchmakerTimeHasCome __instance, IEftSession session, RaidSettings raidSettings)
        {
            try
            {
                Profile profile = session?.Profile;
                if (profile == null || !Coming) return;
                Stage.Dress(raidSettings.IsPmc ? profile : null);
                DogDeployRow.Create(__instance, raidSettings.IsPmc ? profile : null);
            }
            catch (Exception e) { Plugin.Log.LogError("The dog couldn't be added to the deploying screen: " + e); }
        }

        /// <summary>The raid has started: the preview dog isn't needed any more.</summary>
        internal static void Release()
        {
            Closed();
            DogLoadout.Close();
            if (stage != null) { stage.Dispose(); UnityEngine.Object.Destroy(stage.gameObject); }
            stage = null;
        }
    }

    /// <summary>The preview dog: its model and gear, a camera and two lights, on a layer nothing else in the menu uses.</summary>
    internal sealed class DogStage : MonoBehaviour, IDisposable
    {
        private const int Height = 900;
        // Metres the box shows, top to bottom, to have the dog at your character's scale.
        internal const float CharacterScale = 2.12f;
        private DogModel model;
        private CompanionDecor decor;
        private Transform dog;
        private Camera cam;
        private int users;
        internal RenderTexture Picture { get; private set; }
        internal float Turn = -60;   // degrees: mostly side on, head to the left, so it fits a narrow box

        internal static DogStage Create()
        {
            int layer = FreeLayer();
            var root = new GameObject("K9 lobby stage");
            DontDestroyOnLoad(root);
            root.transform.position = new Vector3(0, -800, 0);
            var stage = root.AddComponent<DogStage>();
            stage.dog = new GameObject("K9 lobby dog").transform;
            stage.dog.SetParent(root.transform, false);
            stage.model = DogModel.Load(Plugin.AssetFolder, stage.dog, Plugin.Scale.Value);
            stage.decor = new CompanionDecor(stage.dog, stage.model);
            stage.decor.SetCosmetics(DogGear.CollarStyle(K9.SessionProfile));
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true)) t.gameObject.layer = layer;

            var camObject = new GameObject("K9 lobby camera");
            camObject.transform.SetParent(root.transform, false);
            stage.cam = camObject.AddComponent<Camera>();
            stage.cam.cullingMask = 1 << layer; stage.cam.clearFlags = CameraClearFlags.SolidColor; stage.cam.backgroundColor = new Color(0, 0, 0, 0);
            stage.cam.nearClipPlane = .1f; stage.cam.farClipPlane = 10; stage.cam.orthographic = true;
            stage.cam.allowHDR = false; stage.cam.useOcclusionCulling = false; stage.cam.enabled = false;
            foreach (var (angles, intensity) in new[] { (new Vector3(35, 150, 0), 1.15f), (new Vector3(20, -60, 0), .45f) })
            {
                var light = new GameObject("K9 lobby light").AddComponent<Light>();
                light.transform.SetParent(root.transform, false); light.transform.localEulerAngles = angles;
                light.type = LightType.Directional; light.intensity = intensity; light.cullingMask = 1 << layer; light.shadows = LightShadows.None;
            }
            stage.Fit(new Vector2(1, 1));
            return stage;
        }

        // A layer no one has named (EFT names the ones it uses), so neither the menu scene's cameras nor its objects meet ours.
        private static int FreeLayer()
        {
            for (int i = 31; i >= 8; i--) if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) return i;
            return 31;
        }

        /// <summary>
        /// Shapes the picture like the box it's shown in (a square picture in a tall box is what squashed the dog), and
        /// films it square on (no perspective) at your character's scale: the box is as tall as the space your character
        /// stands in (about 2.1 m), the floor near its bottom, so the dog stands on the same floor and comes up to about
        /// your character's hip, as a working dog does. Returns the picture to show.
        /// </summary>
        internal RenderTexture Fit(Vector2 box, float tall = CharacterScale)
        {
            float aspect = Mathf.Clamp(box.y > 1 ? box.x / box.y : 1, .3f, 3f);
            int width = Mathf.RoundToInt(Height * aspect);
            if (Picture == null || Picture.width != width)
            {
                if (Picture != null) { cam.targetTexture = null; Picture.Release(); Destroy(Picture); }
                Picture = new RenderTexture(width, Height, 24, RenderTextureFormat.ARGB32) { name = "K9 lobby picture", antiAliasing = 4 };
                cam.targetTexture = Picture;
            }
            const float floor = .08f;   // share of what the box shows that's below the floor
            cam.orthographic = true;
            cam.orthographicSize = tall / 2;
            cam.transform.localPosition = new Vector3(0, tall / 2 - floor * tall, 4);
            cam.transform.localRotation = Quaternion.Euler(0, 180, 0);
            return Picture;
        }

        /// <summary>The gear the dog wears into this raid (none on a Scav raid).</summary>
        internal void Dress(Profile pmc)
        {
            decor.SetVest(pmc != null ? DogGear.Look(DogGear.Worn(pmc)) : null);
            decor.SetHead(pmc != null ? DogGear.HeadLook(pmc) : null);
            decor.SetCamera(pmc != null && DogGear.CameraLook(pmc) != null);
            decor.SetCosmetics(DogGear.CollarStyle(K9.SessionProfile));   // on a Scav raid too: the collar always goes
        }

        // Filmed only while something shows it.
        internal void Use(bool on)
        {
            users = Mathf.Max(0, users + (on ? 1 : -1));
            cam.enabled = users > 0;
        }

        private void Update()
        {
            if (model == null || users == 0) return;
            dog.localRotation = Quaternion.Euler(0, Turn, 0);
            model.Tick("Stand_00", 1, Time.unscaledDeltaTime);
        }

        public void Dispose()
        {
            decor?.Dispose(); decor = null;
            model?.Dispose(); model = null;
            if (Picture != null) { Picture.Release(); Destroy(Picture); Picture = null; }
        }
    }

    /// <summary>The dog's picture in a box: shaped to it, filmed while it shows, dragged to turn the dog.</summary>
    internal sealed class DogPicture : MonoBehaviour, IDragHandler
    {
        private DogStage stage;
        private RawImage image;
        private Vector2 shownSize;
        private bool counted;   // counted among the stage's viewers (it's only filmed while it has some)
        private float tall;

        internal static DogPicture Add(RectTransform box, DogStage stage, float tall = DogStage.CharacterScale)
        {
            var image = box.gameObject.AddComponent<RawImage>();
            // AddComponent runs OnEnable at once, before the stage is known: it's counted here instead.
            var picture = box.gameObject.AddComponent<DogPicture>();
            picture.stage = stage; picture.image = image; picture.tall = tall;
            picture.Count(picture.isActiveAndEnabled);
            return picture;
        }

        private void Count(bool on)
        {
            if (stage == null || on == counted) return;
            counted = on;
            stage.Use(on);
        }

        private void OnEnable() { Count(true); shownSize = default; }
        private void OnDisable() { Count(false); }

        private void LateUpdate()
        {
            if (stage == null) return;
            Vector2 size = ((RectTransform)transform).rect.size;
            if (size != shownSize) { shownSize = size; image.texture = stage.Fit(size, tall); }
        }

        public void OnDrag(PointerEventData data) { if (stage != null) stage.Turn -= data.delta.x * .4f; }
    }

    /// <summary>The panel next to your character on the confirmation screen: the dog, its name and state, TAKE DOG.</summary>
    internal sealed class DogLobbyPanel : MonoBehaviour
    {
        private Profile profile;
        private bool pmc;
        private TextMeshProUGUI status, takeLabel;
        private TMP_FontAsset font;

        internal static DogLobbyPanel Create(MatchMakerAcceptScreen screen, Profile profile, bool pmc, DogStage stage)
        {
            var player = (RectTransform)screen._playerModelView.transform;
            var go = new GameObject("K9 lobby panel", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(player.parent, false);
            // Right beside your character, the same height, a little narrower.
            rect.anchorMin = player.anchorMin; rect.anchorMax = player.anchorMax; rect.pivot = player.pivot;
            Vector2 size = player.rect.size;
            rect.sizeDelta = new Vector2(size.x * .9f, size.y);
            rect.anchoredPosition = player.anchoredPosition + new Vector2(size.x * (1 - player.pivot.x) + size.x * .9f * rect.pivot.x + 12, 0);
            var view = go.AddComponent<DogLobbyPanel>();
            view.profile = profile; view.pmc = pmc;
            view.font = screen.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f != null);
            view.Build(rect.rect.size, stage);
            return view;
        }

        private void Build(Vector2 size, DogStage stage)
        {
            float w = size.x, h = size.y;
            // The dog first, under everything: it stands on your character's floor.
            DogPicture.Add(Child("Dog picture", Vector2.zero, new Vector2(w, h)), stage);
            Text((Plugin.DogName.Value ?? "K9").ToUpperInvariant(), 22, FontStyles.Bold, new Color(.86f, .86f, .82f), new Vector2(12, -8), new Vector2(w - 24, 30));
            status = Text("", 15, FontStyles.Normal, new Color(.75f, .76f, .72f), new Vector2(12, -40), new Vector2(w - 24, 64));
            (takeLabel, _) = Button(new Vector2(12, -108), new Vector2(Mathf.Min(w - 24, 220), 34), () => { Plugin.Active.Value = !Plugin.Active.Value; Refresh(); });
            Refresh();
        }

        private void Refresh()
        {
            DogNeeds needs = NeedsStore.Current;
            string stays = K9.NotDeployable();
            bool going = Plugin.Active.Value && stays == null;
            takeLabel.text = "TAKE DOG: " + (Plugin.Active.Value ? "YES" : "NO");
            string state = needs == null ? "" : $"Health {Mathf.RoundToInt(needs.Health)}%   Hunger {Mathf.RoundToInt(needs.Hunger)}%   Thirst {Mathf.RoundToInt(needs.Thirst)}%\n";
            string note = stays != null ? "Stays home: " + stays + "." : !Plugin.Active.Value ? "Stays home this raid." : pmc ? "Comes with you." : "Comes with you (no gear on a Scav raid).";
            status.text = state + (going ? "<color=#9fc29a>" : "<color=#d9a066>") + note + "</color>";
        }

        private void Update()
        {
            if (Time.frameCount % 30 == 0) Refresh();
        }

        private RectTransform Child(string name, Vector2 topLeft, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = topLeft; rect.sizeDelta = size;
            return rect;
        }

        private TextMeshProUGUI Text(string text, float size, FontStyles style, Color colour, Vector2 topLeft, Vector2 box, Transform parent = null)
        {
            RectTransform rect = Child("Text", topLeft, box);
            if (parent != null) rect.SetParent(parent, false);
            return DogUi.Label(rect, font, text, size, style, colour);
        }

        private (TextMeshProUGUI, Button) Button(Vector2 topLeft, Vector2 size, Action click)
            => DogUi.Button(Child("Button", topLeft, size), font, click);
    }

    /// <summary>
    /// The dog's line in the deploying screen's party list: a copy of your own line, with a paw for the level badge, the
    /// dog's training level and name. Click it for the dog in the big picture; its eye opens the dog's loadout.
    /// </summary>
    internal sealed class DogDeployRow : MonoBehaviour, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private const string RowName = "K9 dog row";
        private MatchmakerTimeHasCome screen;
        private Profile profile;
        private GameObject hover;
        private RectTransform dogView;
        private CanvasGroup characterFade;

        internal static void Create(MatchmakerTimeHasCome screen, Profile profile)
        {
            PartyInfoPanel party = screen._partyInfoPanel;
            Transform list = party?._playersContainer;
            if (list == null) return;
            foreach (Transform old in list.Cast<Transform>().Where(t => t.name == RowName).ToList()) Destroy(old.gameObject);
            PartyPlayerItem source = list.GetComponentsInChildren<PartyPlayerItem>(false).FirstOrDefault() ?? party._playerItemTemplate;
            if (source == null) return;

            GameObject copy = Instantiate(source.gameObject, list, false);
            copy.name = RowName;
            copy.SetActive(true);
            var item = copy.GetComponent<PartyPlayerItem>();
            var row = copy.AddComponent<DogDeployRow>();
            row.screen = screen; row.profile = profile;
            row.hover = item._hoverBg;
            if (item._hoverBg != null) item._hoverBg.SetActive(false);
            if (item._borderLeader != null) item._borderLeader.SetActive(false);
            if (item._borderNormal != null) item._borderNormal.SetActive(true);
            // A paw for the level badge, with the dog's training level (1 untrained, up to 4).
            if (item._levelPanel != null)
            {
                item._levelPanel.gameObject.SetActive(true);
                if (item._levelPanel._level != null) item._levelPanel._level.text = $"{K9.Training + 1:D2}";
                if (item._levelPanel._icon != null && DogLobby.Paw != null) item._levelPanel._icon.sprite = DogLobby.Paw;
            }
            if (item._chatSpecialIcon != null)
            {
                if (item._chatSpecialIcon._specialLabel != null) item._chatSpecialIcon._specialLabel.text = Plugin.DogName.Value ?? "K9";
                if (item._chatSpecialIcon._icon != null && DogLobby.Paw != null) item._chatSpecialIcon._icon.sprite = DogLobby.Paw;
                if (item._chatSpecialIcon._iconPrestige != null) item._chatSpecialIcon._iconPrestige.gameObject.SetActive(false);
            }
            if (item._eyeButton != null)
            {
                item._eyeButton.SetAvailability(true);
                item._eyeButton.OnClick.Subscribe(() => DogLoadout.Toggle(row.profile, screen.transform));
            }
            // The copy's own script would answer its clicks as your line's; this row answers them instead, through a
            // see-through background that catches the pointer (the script's own did, and went with it).
            DestroyImmediate(item);
            if (copy.GetComponent<Graphic>() == null) copy.AddComponent<Image>().color = new Color(0, 0, 0, .001f);
            // Clicking any player's line (or eye) puts that player back in the big picture.
            party.OnPlayerClick += _ => { if (row != null) row.ShowDog(false); };
        }

        public void OnPointerClick(PointerEventData data)
        {
            if (data.button == PointerEventData.InputButton.Left) ShowDog(true);
        }

        public void OnPointerEnter(PointerEventData data) { if (hover != null) hover.SetActive(true); }
        public void OnPointerExit(PointerEventData data) { if (hover != null) hover.SetActive(false); }

        // The dog in the big picture where your character stands (your character only faded out, so it's back at once).
        private void ShowDog(bool on)
        {
            if (screen == null || screen._playerModelView == null) return;
            if (on && dogView == null)
            {
                var player = (RectTransform)screen._playerModelView.transform;
                dogView = new GameObject("K9 deploying dog", typeof(RectTransform)).GetComponent<RectTransform>();
                dogView.SetParent(player.parent, false);
                dogView.SetSiblingIndex(player.GetSiblingIndex() + 1);
                dogView.anchorMin = player.anchorMin; dogView.anchorMax = player.anchorMax; dogView.pivot = player.pivot;
                dogView.anchoredPosition = player.anchoredPosition; dogView.sizeDelta = player.sizeDelta;
                DogPicture.Add(dogView, DogLobby.Stage);
                characterFade = screen._playerModelView.GetComponent<CanvasGroup>() ?? screen._playerModelView.gameObject.AddComponent<CanvasGroup>();
            }
            if (dogView == null) return;
            dogView.gameObject.SetActive(on);
            if (characterFade != null) { characterFade.alpha = on ? 0 : 1; characterFade.blocksRaycasts = !on; }
        }

        private void OnDestroy()
        {
            if (characterFade != null) { characterFade.alpha = 1; characterFade.blocksRaycasts = true; }
            if (dogView != null) Destroy(dogView.gameObject);
        }
    }

    /// <summary>The dog's loadout, opened by its eye: what's on it, slot by slot, and its jacket to open.</summary>
    internal sealed class DogLoadout : MonoBehaviour
    {
        private static DogLoadout open;

        internal static void Toggle(Profile profile, Transform screen)
        {
            if (open != null) { Close(); return; }
            var root = new GameObject("K9 dog loadout");
            var canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 120;
            var scaler = root.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1920, 1080); scaler.matchWidthOrHeight = 1;
            root.AddComponent<GraphicRaycaster>();
            open = root.AddComponent<DogLoadout>();
            TMP_FontAsset font = screen != null ? screen.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f != null) : null;
            open.Build(profile, font);
        }

        internal static void Close()
        {
            if (open != null) Destroy(open.gameObject);
            open = null;
        }

        private void Build(Profile profile, TMP_FontAsset font)
        {
            var window = new GameObject("Window", typeof(RectTransform)).GetComponent<RectTransform>();
            window.SetParent(transform, false);
            window.anchorMin = window.anchorMax = window.pivot = new Vector2(.5f, .5f);
            window.anchoredPosition = new Vector2(380, 40); window.sizeDelta = new Vector2(440, 492);
            window.gameObject.AddComponent<Image>().color = new Color(.06f, .065f, .06f, .96f);
            RectTransform At(Vector2 topLeft, Vector2 size)
            {
                var r = new GameObject("Part", typeof(RectTransform)).GetComponent<RectTransform>();
                r.SetParent(window, false);
                r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1);
                r.anchoredPosition = topLeft; r.sizeDelta = size;
                return r;
            }
            DogUi.Label(At(new Vector2(18, -14), new Vector2(330, 30)), font, (Plugin.DogName.Value ?? "K9").ToUpperInvariant() + "  LOADOUT", 20, FontStyles.Bold, new Color(.86f, .86f, .82f));
            DogUi.Button(At(new Vector2(394, -12), new Vector2(32, 32)), font, Close).Item1.text = "X";
            // What went into the raid with the dog, as it was at raid start (DogBag): by now the jacket is in the raid container.
            bool taken = DogGear.RaidLoadoutTaken;
            Item jacket = taken ? DogGear.RaidJacket : profile != null ? DogGear.Worn(profile) : null;
            var rows = new List<(string, Item)>
            {
                ("JACKET", jacket),
                ("HEAD", taken ? DogGear.RaidHeadItem : profile != null ? DogGear.SlotOf(profile, DogGear.HeadSlot)?.ContainedItem : null),
                ("CAMERA", taken ? DogGear.RaidCameraItem : profile != null ? DogGear.CameraWorn(profile) : null),
                // The collar goes on a Scav raid too (it's never lost).
                ("COLLAR", taken ? DogGear.RaidCollarItem : DogGear.SlotOf(profile ?? K9.SessionProfile, DogGear.CollarSlot)?.ContainedItem),
            };
            float y = -60;
            foreach (var (slot, item) in rows)
            {
                At(new Vector2(18, y), new Vector2(404, 84)).gameObject.AddComponent<Image>().color = new Color(1, 1, 1, .04f);
                var icon = At(new Vector2(24, y - 6), new Vector2(110, 72)).gameObject.AddComponent<Image>();
                icon.preserveAspect = true; icon.raycastTarget = false;
                icon.sprite = item != null ? DogGearPatches.IconFor(item) : null;
                icon.color = icon.sprite != null ? Color.white : new Color(1, 1, 1, 0);
                DogUi.Label(At(new Vector2(146, y - 10), new Vector2(270, 22)), font, slot, 13, FontStyles.Bold, new Color(.6f, .62f, .58f));
                DogUi.Label(At(new Vector2(146, y - 34), new Vector2(270, 44)), font, item != null ? item.LocalizedName() : (profile == null && slot != "COLLAR" ? "None on a Scav raid" : "None"), 16, FontStyles.Normal, new Color(.86f, .86f, .82f));
                y -= 92;
            }
            var (label, openJacket) = DogUi.Button(At(new Vector2(18, y - 8), new Vector2(404, 40)), font, () =>
            {
                CompoundItem bag = jacket as CompoundItem;
                if (bag == null || ItemUiContext.Instance == null) return;
                try { ItemUiContext.Instance.OpenItem(bag, new DefaultItemContext(bag, EItemViewType.InventoryDuringMatching)); }
                catch (Exception e) { Plugin.Log.LogWarning("The dog's jacket couldn't be opened here: " + e.Message); }
            });
            label.text = "OPEN JACKET";
            openJacket.interactable = jacket != null;
        }
    }

    /// <summary>Plain labels and buttons in the game's font, for the dog's bits of the raid screens.</summary>
    internal static class DogUi
    {
        internal static TextMeshProUGUI Label(RectTransform rect, TMP_FontAsset font, string text, float size, FontStyles style, Color colour)
        {
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            label.text = text; label.fontSize = size; label.fontStyle = style; label.color = colour;
            label.enableWordWrapping = true; label.raycastTarget = false; label.alignment = TextAlignmentOptions.TopLeft;
            return label;
        }

        internal static (TextMeshProUGUI, Button) Button(RectTransform rect, TMP_FontAsset font, Action click)
        {
            var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(.16f, .17f, .16f, .92f);
            var button = rect.gameObject.AddComponent<Button>();
            button.targetGraphic = image;
            var colours = button.colors; colours.highlightedColor = new Color(.35f, .37f, .33f); colours.disabledColor = new Color(.4f, .4f, .4f, .5f); button.colors = colours;
            button.onClick.AddListener(() => click());
            var text = new GameObject("Label", typeof(RectTransform)).GetComponent<RectTransform>();
            text.SetParent(rect, false);
            text.anchorMin = Vector2.zero; text.anchorMax = Vector2.one; text.offsetMin = text.offsetMax = Vector2.zero;
            var label = Label(text, font, "", 16, FontStyles.Bold, new Color(.86f, .86f, .82f));
            label.alignment = TextAlignmentOptions.Center;
            return (label, button);
        }
    }
}
