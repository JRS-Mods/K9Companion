using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System;
using EFT.HealthSystem;
using EFT.InventoryLogic;
using EFT.Quests;
using EFT.UI.DragAndDrop;
using EFT.UI.Health;
using EFT.UI;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using TMPro;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The DOG tab, in place of your own equipment. On the left the dog itself, in its gear (drag to turn it), with its head
    /// and collar slots, its condition, its care and how it behaves. On the right its JACKET (the game's own slot view,
    /// so jackets and their contents are dragged in and out like any gear) and its kennel. Your stash stays beyond that,
    /// as in the gear view.
    /// </summary>
    internal sealed partial class DogScreen : MonoBehaviour
    {
        private static readonly FieldInfo ControllerField = AccessTools.Field(typeof(InventoryScreen), "_inventoryController");
        private static readonly FieldInfo InsuranceField = AccessTools.Field(typeof(InventoryScreen), "_insurance");
        private static readonly FieldInfo SlotTemplateField = AccessTools.Field(typeof(ContainersPanel), "_defaultSlotTemplate");
        private const float RefW = 1240, RefH = 860;
        // Two columns: the dog and its care on the left, its jacket and kennel on the right.
        private const float LeftX = 16, LeftW = 540, RightX = 580, RightW = 644;
        // The game's slots and grid cells keep their own size whatever the scale, and a slot's title sits above its position.
        private const float SlotHeader = 20, SlotBox = 127, GridCell = 63.2f, JacketCells = 6;
        private const float JacketTop = 4, DogTop = 70, DogHeight = 396;
        // The rows down the left-hand side, from just under the dog.
        private const float StatTop = -482, CareTop = -576, BehaviourTop = -640, BehaviourHeight = 166;
        // Where the largest jacket's grid ends, in layout units.
        private float JacketBottom => (JacketTop * s + SlotHeader + JacketCells * GridCell) / s;
        private const float DogZoom = 1.42f;   // metres the picture of the dog shows, top to bottom
        private static readonly Color Ink = new Color(.86f, .86f, .82f), Soft = new Color(.6f, .61f, .58f), Accent = new Color(.78f, .73f, .55f),
            Backing = new Color(.03f, .035f, .04f, .96f), PanelColour = new Color(.075f, .08f, .085f, .92f), Rule = new Color(.36f, .36f, .32f, .9f),
            Good = new Color(.55f, .72f, .36f), Caution = new Color(.85f, .65f, .25f), Danger = new Color(.79f, .3f, .25f),
            Water = new Color(.33f, .65f, .84f), Energy = new Color(.84f, .78f, .42f);

        private static DogScreen open;
        private static Sprite outlineSprite;

        private InventoryScreen screen;
        private Profile profile;
        private ItemContext context, holderContext;
        private readonly List<SlotView> slotViews = new List<SlotView>();
        private readonly List<GameObject> keepHidden = new List<GameObject>();
        private TMP_FontAsset font;
        private float s = 1;
        private TextMeshProUGUI headerLine, behaviourLine, approachLine, armourLine, jacketNote;
        private Bar armourBar;
        private DogStage stage;
        private float nextDress;
        private Button openJacket;
        private float nextRefresh;


        internal static void Open(InventoryScreen screen, ItemsPanel items, ItemContext context, Profile profile, IEnumerable<GameObject> hidden)
        {
            Close();
            var root = new GameObject("K9 DOG screen", typeof(RectTransform));
            var view = root.AddComponent<DogScreen>();
            open = view;
            try { view.Build(screen, items, context, profile, hidden); }
            catch (Exception e) { Plugin.Log.LogError("DOG tab couldn't be drawn: " + e); }
        }

        internal static void Close()
        {
            if (open == null) return;
            open.Teardown();
            Destroy(open.gameObject);
            open = null;
        }

        private void Teardown()
        {
            foreach (SlotView view in slotViews)
                try { view?.Close(); } catch (Exception e) { Plugin.Log.LogWarning("Closing a dog slot: " + e.Message); }
            slotViews.Clear();            if (holderContext != null)
            {
                ItemUiContext.Instance?.UnregisterView(holderContext);
                holderContext.Dispose(); holderContext = null;
            }
            foreach (ItemContext window in opened)
                try { window.Dispose(); } catch (Exception e) { Plugin.Log.LogWarning("Closing a dog window: " + e.Message); }
            opened.Clear();
        }

        // The game shows its equipment columns a frame or two after the tab opens; keep them out of the dog's way.
        private void LateUpdate()
        {
            if (screen == null || !screen.isActiveAndEnabled) { Close(); return; }
            foreach (GameObject part in keepHidden) if (part != null && part.activeSelf) part.SetActive(false);
            if (Time.unscaledTime >= nextRefresh) { nextRefresh = Time.unscaledTime + .25f; Refresh(); }
        }

        // ---- layout -------------------------------------------------------------------------------------------------

        private void Build(InventoryScreen screen, ItemsPanel items, ItemContext context, Profile profile, IEnumerable<GameObject> hidden)
        {
            this.screen = screen; this.profile = profile; this.context = context;
            keepHidden.AddRange(hidden);
            font = screen.GetComponentsInChildren<TMP_Text>(true).Select(t => t.font).FirstOrDefault(f => f != null);

            // Exactly where the equipment columns were.
            var rect = (RectTransform)transform;
            var parent = (RectTransform)items.transform;
            rect.SetParent(parent, false);
            Rect area = Union(parent, items._equipmentPanel?.transform as RectTransform, items._containers?.transform as RectTransform);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(.5f, .5f);
            rect.sizeDelta = area.size;
            rect.anchoredPosition = area.center - parent.rect.center;
            s = Mathf.Min(area.width / RefW, area.height / RefH);
            if (s <= 0 || float.IsNaN(s)) s = 1;

            // Over the whole area, so nothing of the gear view underneath shows through or catches a click.
            var back = gameObject.AddComponent<Image>(); back.color = Backing;

            K9.Refresh();
            bool atHome = !DogVet.InRaid;
            string name = (Plugin.DogName.Value ?? "K9").ToUpperInvariant();
            Text(name, 34, FontStyles.Bold, Ink, new Vector2(LeftX + 2, -4), new Vector2(LeftW - 120, 44));
            headerLine = Text("", 14, FontStyles.Normal, Soft, new Vector2(LeftX + 4, -46), new Vector2(LeftW - 8, 22));
            headerLine.richText = true;

            // The dog itself, with what it wears on its head and neck beside it.
            Panel(null, new Vector2(LeftX, -DogTop), new Vector2(LeftW, DogHeight));
            ShowDog(atHome);
            if (atHome) ShowSlot(items);
            else ShowRaidJacket();
            ShowStats(items);
            BuildBehaviour();
            BuildKennel();
            Refresh();
        }

        // The real dog in its real gear, to be dragged round; in a raid its outline (it's out there).
        private void ShowDog(bool atHome)
        {
            float clear = (SlotBox + 16) / s;   // right of the head and collar slots
            RectTransform box = Child("Dog picture", new Vector2(LeftX + clear, -DogTop - 2), new Vector2(LeftW - clear - 4, DogHeight - 4));
            if (atHome && K9.Owned)
            {
                try
                {
                    stage = DogLobby.Stage;
                    stage.Dress(profile);
                    DogPicture.Add(box, stage, DogZoom);
                    var hint = Text("drag to turn", 11, FontStyles.Italic, Soft, new Vector2(LeftX + LeftW - 92, -DogTop - DogHeight + 20), new Vector2(84, 16));
                    hint.alignment = TextAlignmentOptions.TopRight;
                    return;
                }
                catch (Exception e) { stage = null; Plugin.Log.LogWarning("The DOG tab's picture of the dog: " + e.Message); }
            }
            var image = box.gameObject.AddComponent<Image>();
            image.sprite = Outline(); image.preserveAspect = true; image.raycastTarget = false;
            if (image.sprite == null) image.color = new Color(1, 1, 1, 0);
        }

        // ---- behaviour ----------------------------------------------------------------------------------------------

        private sealed class Choice { public Image Back; public TextMeshProUGUI Label; public Button Button; public Func<bool> Chosen, Open; }
        private readonly List<Choice> choices = new List<Choice>();

        private void BuildBehaviour()
        {
            const float top = BehaviourTop;
            Panel("Behaviour", new Vector2(LeftX, top), new Vector2(LeftW, BehaviourHeight));
            Row("COMBAT", top - 26, new (string, Func<bool>, Action)[]
            {
                ("ON", () => Plugin.Combat, () => Plugin.CombatMode.Value = true),
                ("OFF", () => !Plugin.Combat, () => Plugin.CombatMode.Value = false)
            }, () => ServerRules.CombatMode == null);
            Row("WHEN TO ATTACK", top - 52, new (string, Func<bool>, Action)[]
            {
                ("ON ORDER", () => Plugin.Aggressiveness == Aggression.Passive, () => Plugin.DogAggression.Value = Aggression.Passive),
                ("DEFENDS YOU", () => Plugin.Aggressiveness == Aggression.Defensive, () => Plugin.DogAggression.Value = Aggression.Defensive),
                ("ANY ENEMY", () => Plugin.Aggressiveness == Aggression.Aggressive, () => Plugin.DogAggression.Value = Aggression.Aggressive)
            }, () => Plugin.Combat && ServerRules.Aggression == null);
            Row("APPROACH", top - 78, new (string, Func<bool>, Action)[]
            {
                ("STEALTH", () => Plugin.Approaching == DogApproach.Stealth, () => Plugin.ApproachMode.Value = DogApproach.Stealth),
                ("DIRECT", () => Plugin.Approaching == DogApproach.Direct, () => Plugin.ApproachMode.Value = DogApproach.Direct),
                ("AUTO", () => Plugin.Approaching == DogApproach.Auto, () => Plugin.ApproachMode.Value = DogApproach.Auto)
            }, () => Plugin.Combat && ServerRules.Approach == null);
            // Fika groups: General > Solo raids only, unless the server fixes it. (It never comes on Scav raids.)
            Row("GROUP RAIDS", top - 104, new (string, Func<bool>, Action)[]
            {
                ("COMES WITH YOU", () => !Plugin.SoloOnly, () => Plugin.SoloRaidsOnly.Value = false),
                ("STAYS HOME", () => Plugin.SoloOnly, () => Plugin.SoloRaidsOnly.Value = true)
            }, () => ServerRules.SoloOnly == null);
            behaviourLine = Text("", 11.5f, FontStyles.Italic, Soft, new Vector2(LeftX + 10, top - 129), new Vector2(LeftW - 20, 18));
            approachLine = Text("", 11.5f, FontStyles.Italic, Soft, new Vector2(LeftX + 10, top - 146), new Vector2(LeftW - 20, 18));
        }

        private void Row(string title, float y, (string Text, Func<bool> Chosen, Action Click)[] options, Func<bool> open)
            => Row(title, y, options.Select(o => (o.Text, o.Chosen, o.Click, open)).ToArray());

        // Each option with its own say on whether it can be changed (set by the server, or not without combat mode).
        private void Row(string title, float y, (string Text, Func<bool> Chosen, Action Click, Func<bool> Open)[] options)
        {
            const float labelWidth = 118, height = 23, gap = 4;
            var label = Text(title, 11.5f, FontStyles.Bold, Soft, new Vector2(LeftX + 10, y - 4), new Vector2(labelWidth, 18));
            label.enableWordWrapping = false;
            float width = (LeftW - labelWidth - 20 - gap * (options.Length - 1)) / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var (text, chosen, click, open) = options[i];
                var (button, caption) = ButtonWithLabel(text, new Vector2(LeftX + 10 + labelWidth + i * (width + gap), y), () => { if (open()) click(); }, new Vector2(width, height));
                caption.fontSize = 11.5f * s;
                choices.Add(new Choice { Back = button.targetGraphic as Image, Label = caption, Button = button, Chosen = chosen, Open = open });
            }
        }

        private void RefreshBehaviour()
        {
            foreach (Choice c in choices)
            {
                bool on = c.Chosen(), open = c.Open();
                c.Button.interactable = open;
                c.Back.color = on ? (open ? new Color(.62f, .57f, .38f, 1) : new Color(.36f, .34f, .26f, 1)) : new Color(.1f, .11f, .11f, .92f);
                c.Label.color = on ? new Color(.06f, .06f, .05f) : open ? Ink : Soft;
            }
            if (behaviourLine == null) return;
            string does = !Plugin.Combat ? "Only follows you: it can't attack or be hurt, and enemies ignore it."
                : Plugin.Aggressiveness == Aggression.Passive ? "Warns you about enemies, and attacks when you order Over There."
                : Plugin.Aggressiveness == Aggression.Defensive ? $"Also goes for whoever shoots at you or it, up to {Plugin.DefendReach:0} m away, unasked."
                : "Goes for every enemy it detects, and whoever shoots at you.";
            bool server = ServerRules.CombatMode != null || ServerRules.Aggression != null || ServerRules.Approach != null || ServerRules.SoloOnly != null;
            behaviourLine.text = Lang.T(does) + (server ? "  " + Lang.T("(Some of this is set by the server.)") : "");
            approachLine.text = !Plugin.Combat ? ""
                : Plugin.Approaching == DogApproach.Auto ? $"Straight at anything within {Plugin.AutoRush:0} m, and works its way in to anything further off."
                : Plugin.Approaching == DogApproach.Stealth ? "Works its way in out of sight, then rushes the last few metres."
                : "Runs straight at them.";
        }

        // The game's own slot view (the one BACKPACK and POUCH use: slot, then the container's grid beside it), bound to
        // the jacket slot of the dog's hidden holder item.
        private void ShowSlot(ItemsPanel items)
        {
            CompoundItem holder = DogGear.Holder(profile);
            Slot slot = DogGear.JacketSlot(profile);
            var template = items._containers != null ? SlotTemplateField?.GetValue(items._containers) as SlotView : null;
            var controller = ControllerField?.GetValue(screen) as InventoryController;
            if (holder == null || slot == null || template == null || controller == null || context == null)
            {
                Note(!K9.Owned ? "You don't have a dog yet. Dr. Yang's questline (Trust Is Earned) brings you one."
                     : holder == null ? "The dog's gear isn't set up yet: restart the SPT server (the K9 Companion server mod sets it up)."
                                    : "The jacket slot couldn't be shown.");
                if (holder != null) Plugin.Log.LogWarning($"DOG tab slot: template {template != null}, controller {controller != null}, context {context != null}.");
                return;
            }
            holderContext = context.CreateChild(holder);
            ItemUiContext.Instance.RegisterView(holderContext);
            var insurance = InsuranceField?.GetValue(screen) as EFT.UI.Insurance.InsuranceCompany;
            void Show(Slot shown, Vector2 at, string emptyLook = null)
            {
                SlotView view = Instantiate(template, transform, false);
                view.gameObject.SetActive(true);
                var rect = (RectTransform)view.transform;
                rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
                // 'at' is where the slot's title goes, which sits above the slot's own position.
                rect.anchoredPosition = at * s - new Vector2(0, SlotHeader);
                rect.localScale = Vector3.one;
                view.Show(shown, holderContext, controller, ItemUiContext.Instance, profile.Skills, insurance, false);
                // An empty slot shows what goes in it (the game would show its default, a chest rig).
                Sprite empty = emptyLook != null ? DogGearPatches.Silhouette(emptyLook) : null;
                if (empty != null && view.SlotBackImage != null) view.SetSlotBackImage(empty);
                slotViews.Add(view);
            }
            // Head gear and the collar beside the dog; the jacket at the head of the right-hand column, its grid opening out to
            // the right of it. An older server mod has no head or collar slot: those just don't show.
            Show(slot, new Vector2(RightX, -JacketTop));
            Slot head = DogGear.SlotOf(profile, DogGear.HeadSlot), camera = DogGear.SlotOf(profile, DogGear.CameraSlot),
                collar = DogGear.SlotOf(profile, DogGear.CollarSlot);
            // Head and collar, centred down the dog's panel with a gap between them.
            const float between = 22;
            float both = 2 * (SlotHeader + SlotBox) + between;
            float headAt = -(DogTop * s + (DogHeight * s - both) / 2) / s, collarAt = headAt - (SlotHeader + SlotBox + between) / s;
            if (head != null) Show(head, new Vector2(LeftX + 8 / s, headAt), "head_goggles_tan");
            if (collar != null) Show(collar, new Vector2(LeftX + 8 / s, collarAt), "collar_training");
            // The camera clips onto the jacket now; the dog's own Camera slot only shows while an old camera is still in it
            // (the server moves it onto the jacket or into the stash at game start), where the collar would go.
            else if (camera != null && camera.ContainedItem != null) Show(camera, new Vector2(LeftX + 8 / s, collarAt), "camera_action");
            Text("Drag a K9 jacket, goggles or collar into its slot; plates and an action camera onto the jacket. Jacket and goggles go into PMC raids with the dog and are lost if you die, or if it's killed and you don't take its gear off it; the collar is never lost.",
                 12, FontStyles.Italic, Soft, new Vector2(LeftX + 2, BehaviourTop - BehaviourHeight - 34), new Vector2(RightX + RightW - LeftX, 20));
        }

        // In a raid the jacket rides in the raid container: show what's in it and open it next to the dog.
        private void ShowRaidJacket()
        {
            Player player = GamePlayerOwner.MyPlayer;
            CompoundItem jacket = DogBag.FindInRaid(player);
            Panel("Jacket", new Vector2(RightX, -JacketTop), new Vector2(RightW, JacketBottom - JacketTop));
            jacketNote = Text("", 18, FontStyles.Normal, Ink, new Vector2(RightX + 10, -34), new Vector2(RightW - 20, 60));
            if (jacket == null) { jacketNote.text = Lang.T("No jacket this raid."); return; }
            (openJacket, _) = ButtonWithLabel("OPEN JACKET", new Vector2(RightX + 10, -100), () =>
            {
                CompoundItem bag = DogBag.FindInRaid(GamePlayerOwner.MyPlayer);
                if (bag != null && ItemUiContext.Instance != null && context != null) ItemUiContext.Instance.OpenItem(bag, ContextOf(bag));
            });
        }

        private void Refresh()
        {
            Companion dog = Companion.Raid;
            bool inRaid = GamePlayerOwner.MyPlayer != null && dog != null && !(GamePlayerOwner.MyPlayer is HideoutPlayer);
            DogNeeds needs = NeedsStore.Current;
            // In a raid the live dog; at home the health it came back with (wounds carry over, see K9.Recover).
            float max = inRaid ? dog.HealthMax : Plugin.MaxHealth;
            float carried = Plugin.CarryOverInjuries.Value && needs != null ? needs.Health / 100f : 1f;
            float now = inRaid ? (dog.IsDead ? 0 : dog.HealthNow) : max * carried;
            float share = max > 0 ? now / max : 0;
            SetStat(health, now, max, inRaid ? dog.IsDead || now < max * .25f : carried < K9.DeployFrom / 100f, 0, share < .25f ? Danger : share < .7f ? Caution : Good);
            bool on = Plugin.SurvivalNeeds.Value && needs != null;
            SetStat(hydration, on ? needs.Thirst : 100, 100, on && needs.Thirst < DogNeeds.Hungry, 0, Water);
            SetStat(energy, on ? needs.Hunger : 100, 100, on && needs.Hunger < DogNeeds.Hungry, 0, Energy);
            CompoundItem worn = inRaid ? DogBag.FindInRaid(GamePlayerOwner.MyPlayer) : DogGear.Worn(profile);
            float kg = worn == null ? 0 : worn.GetAllItems().Sum(i => i.Weight * Mathf.Max(1, i.StackObjectsCount));
            SetStat(weight, kg, CarryLimit, kg > CarryLimit, 1, Accent);
            if (stage != null && Time.unscaledTime >= nextDress) { nextDress = Time.unscaledTime + 1; stage.Dress(profile); }
            string state = !K9.Owned ? "No dog yet"
                : inRaid && dog.IsDead ? "<color=#c9705f>Dead</color>"
                : ConditionText(needs);
            string where = inRaid ? "in the raid with you" : K9.NotDeployable() is string stays ? "<color=#c9705f>" + stays + "</color>" : K9.Owned ? "<color=#9fc58a>ready to deploy</color>" : "";
            if (headerLine != null) headerLine.text = Lang.T("K9 dog  ·  " + state + (where.Length > 0 ? "  ·  " + where : ""));
            RefreshBehaviour();
            if (jacketNote != null && inRaid)
            {
                CompoundItem jacket = DogBag.FindInRaid(GamePlayerOwner.MyPlayer);
                bool near = dog != null && dog.DistanceTo(GamePlayerOwner.MyPlayer) <= 3f;
                if (jacket != null) jacketNote.text = Lang.T($"{jacket.LocalizedName()}: {jacket.GetAllItems().Count() - 1} item(s).") + (near ? "" : "\n" + Lang.T("Stand next to the dog to open it."));
                if (openJacket != null) openJacket.interactable = jacket != null && near;
            }
            RefreshKennel();
        }

        // ---- pieces -------------------------------------------------------------------------------------------------

        private static Rect Union(RectTransform space, params RectTransform[] parts)
        {
            var corners = new Vector3[4]; bool any = false;
            float minX = 0, minY = 0, maxX = 0, maxY = 0;
            foreach (RectTransform part in parts)
            {
                if (part == null) continue;
                part.GetWorldCorners(corners);
                foreach (Vector3 c in corners)
                {
                    Vector3 p = space.InverseTransformPoint(c);
                    if (!any) { minX = maxX = p.x; minY = maxY = p.y; any = true; }
                    minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x); minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                }
            }
            if (!any || maxX - minX < 100 || maxY - minY < 100)
            {
                Rect r = space.rect;   // fall back to the left two-thirds of the items panel
                return new Rect(r.xMin, r.yMin, r.width * .62f, r.height);
            }
            return Rect.MinMaxRect(minX, minY, maxX, maxY);
        }

        private RectTransform Child(string name, Vector2 topLeft, Vector2 size)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(transform, false);
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = topLeft * s; rect.sizeDelta = size * s;
            return rect;
        }

        private TextMeshProUGUI Text(string text, float size, FontStyles style, Color colour, Vector2 topLeft, Vector2 box, RectTransform parent = null)
        {
            RectTransform rect = Child("Text", topLeft, box);
            if (parent != null) rect.SetParent(parent, false);
            var label = rect.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) label.font = font;
            label.text = Lang.T(text); label.fontSize = size * s; label.fontStyle = style; label.color = colour;
            label.enableWordWrapping = true; label.raycastTarget = false; label.alignment = TextAlignmentOptions.TopLeft;
            return label;
        }

        private void Note(string text) => Text(text, 18, FontStyles.Normal, new Color(.9f, .75f, .5f), new Vector2(RightX + 8, -30), new Vector2(RightW - 16, 120));

        private static string ConditionText(DogNeeds needs)
        {
            string condition = K9.Condition(needs);
            string colour = needs == null ? "#9a9a94" : needs.Health < K9.DeployFrom ? "#c9705f" : condition == "Healthy" ? "#9fc58a" : "#d9a441";
            return "<color=" + colour + ">" + condition + "</color>";
        }

        // A dark panel with a rule along its top and, if it has one, its title.
        private RectTransform Panel(string title, Vector2 topLeft, Vector2 size)
        {
            RectTransform box = Child("Panel", topLeft, size);
            var back = box.gameObject.AddComponent<Image>(); back.color = PanelColour; back.raycastTarget = false;
            RectTransform rule = Child("Rule", topLeft, new Vector2(size.x, 1));
            var line = rule.gameObject.AddComponent<Image>(); line.color = Rule; line.raycastTarget = false;
            if (title != null) Text(Lang.T(title).ToUpperInvariant(), 13, FontStyles.Bold, Accent, topLeft + new Vector2(10, -5), new Vector2(size.x - 20, 18));
            return box;
        }

        // A thin level bar.
        private sealed class Bar { public RectTransform Fill; public Image Colour, Track; public float Width; }

        private Bar MakeBar(Vector2 topLeft, float width, float height)
        {
            var track = Child("Bar", topLeft, new Vector2(width, height)).gameObject.AddComponent<Image>();
            track.color = new Color(1, 1, 1, .09f); track.raycastTarget = false;
            RectTransform fill = Child("Bar fill", topLeft, new Vector2(width, height));
            var colour = fill.gameObject.AddComponent<Image>(); colour.raycastTarget = false;
            return new Bar { Fill = fill, Colour = colour, Track = track, Width = width };
        }

        private void SetBar(Bar bar, float share, Color colour)
        {
            if (bar == null) return;
            bar.Fill.sizeDelta = new Vector2(bar.Width * Mathf.Clamp01(share) * s, bar.Fill.sizeDelta.y);
            bar.Colour.color = colour;
        }

        // ---- the stats row, copied from your gear screen ------------------------------------------------------------

        private const float CarryLimit = 20f;   // what the dog comfortably carries, kg (shown like your own weight limit)
        private static readonly FieldInfo Current = AccessTools.Field(typeof(HealthParameterPanel), "_currentValue");
        private static readonly FieldInfo Maximum = AccessTools.Field(typeof(HealthParameterPanel), "_maxValue");
        private static readonly string[] Unused = { "_poisoning", "_radiation", "_bloodPressure", "_temperature" };
        private sealed class Stat { public TMP_Text Value, Max; public Color Normal; public RectTransform Box; public Bar Level; }
        private const float StatStep = 133;
        private Stat health, hydration, energy, weight;

        private void ShowStats(ItemsPanel items)
        {
            HealthParametersPanel template = items._healthParametersPanel;
            if (template == null) { Plugin.Log.LogWarning("DOG tab: the gear screen's stats row wasn't found."); return; }
            // Copied while the original is hidden, so the copy's own scripts never start; the numbers are set here.
            bool was = template.gameObject.activeSelf;
            template.gameObject.SetActive(false);
            GameObject row = Instantiate(template.gameObject, transform, false);
            template.gameObject.SetActive(was);
            row.name = "Dog stats";
            foreach (MonoBehaviour script in row.GetComponentsInChildren<MonoBehaviour>(true))
                if (script is HealthParametersPanel || script is HealthParameterPanel) script.enabled = false;
            var panel = row.GetComponent<HealthParametersPanel>();
            foreach (string field in Unused)
                if (AccessTools.Field(typeof(HealthParametersPanel), field)?.GetValue(panel) is Component part) part.gameObject.SetActive(false);
            health = StatOf(panel, "_overallHealth"); hydration = StatOf(panel, "_hydration"); energy = StatOf(panel, "_energy"); weight = StatOf(panel, "_weight");
            var rect = (RectTransform)row.transform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0, 1);
            rect.anchoredPosition = new Vector2(LeftX + 6, StatTop) * s;
            row.SetActive(true);
            LayOut(rect, s, weight, health, hydration, energy);
            // Under each number, how full it is and what it is (the game's icons are for your own body: the bolt is hunger here).
            var stats = new[] { (weight, "LOAD"), (health, "HEALTH"), (hydration, "THIRST"), (energy, "HUNGER") };
            for (int i = 0; i < stats.Length; i++)
            {
                if (stats[i].Item1 == null) continue;
                float x = LeftX + 6 + i * StatStep;
                // Clear of the game's numbers, which keep their own size; the name under the bar with a little room.
                float bar = StatTop - 48 / s;
                stats[i].Item1.Level = MakeBar(new Vector2(x, bar), StatStep - 14, 4);
                Text(stats[i].Item2, 10.5f, FontStyles.Bold, Soft, new Vector2(x, bar - 11), new Vector2(StatStep - 14, 14));
            }
        }

        // The gear screen's own script spaces these out, and it's off in the copy (it would show your numbers), so they
        // would all sit on top of each other: put them side by side, left to right, directly in the row.
        private static void LayOut(RectTransform row, float scale, params Stat[] stats)
        {
            foreach (var group in row.GetComponentsInChildren<LayoutGroup>(true)) group.enabled = false;
            foreach (var fitter in row.GetComponentsInChildren<ContentSizeFitter>(true)) fitter.enabled = false;
            float x = 0;
            foreach (Stat stat in stats)
            {
                if (stat?.Box == null) continue;
                RectTransform box = stat.Box;
                box.SetParent(row, false);
                box.anchorMin = box.anchorMax = box.pivot = new Vector2(0, 1);
                box.anchoredPosition = new Vector2(x, 0);
                x += StatStep * scale;
            }
        }

        private static Stat StatOf(HealthParametersPanel panel, string field)
        {
            if (!(AccessTools.Field(typeof(HealthParametersPanel), field)?.GetValue(panel) is HealthParameterPanel part)) return null;
            if (part is BuffableHealthParameterPanel buffable)
                foreach (Component bit in new Component[] { buffable._upBuffArrow, buffable._downBuffArrow, buffable._buffValue })
                    if (bit != null) bit.gameObject.SetActive(false);   // the rate arrows describe your body, not the dog
            var stat = new Stat { Value = Current?.GetValue(part) as TMP_Text, Max = Maximum?.GetValue(part) as TMP_Text, Box = part.transform as RectTransform };
            if (stat.Value != null) stat.Normal = stat.Value.color;
            return stat;
        }

        private void SetStat(Stat stat, float value, float max, bool warn, int decimals, Color level)
        {
            if (stat?.Value == null) return;
            SetBar(stat.Level, max > 0 ? value / max : 0, warn ? Danger : level);
            stat.Value.text = decimals > 0 ? value.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) : Mathf.CeilToInt(value).ToString();
            stat.Value.color = warn ? new Color(.86f, .3f, .25f) : stat.Normal;
            if (stat.Max != null) stat.Max.text = "/" + Mathf.RoundToInt(max);
        }

        private (Button, TextMeshProUGUI) ButtonWithLabel(string text, Vector2 topLeft, Action click, Vector2? size = null)
        {
            Vector2 box = size ?? new Vector2(250, 30);
            RectTransform rect = Child("Button", topLeft, box);
            var back = rect.gameObject.AddComponent<Image>(); back.color = new Color(.1f, .11f, .11f, .92f);
            rect.gameObject.AddComponent<Outline>().effectColor = new Color(.45f, .47f, .46f, .55f);
            var button = rect.gameObject.AddComponent<Button>();
            var colours = button.colors;
            colours.normalColor = Color.white; colours.highlightedColor = new Color(1.6f, 1.6f, 1.55f); colours.pressedColor = new Color(.7f, .7f, .7f);
            colours.disabledColor = new Color(.5f, .5f, .5f, .6f); colours.colorMultiplier = 1;
            button.colors = colours; button.targetGraphic = back;
            button.onClick.AddListener(() => { click(); Refresh(); });
            var label = Text(text, 14, FontStyles.Bold, new Color(.88f, .88f, .84f), Vector2.zero, box, rect);
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one;
            label.rectTransform.offsetMin = label.rectTransform.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Center;
            return (button, label);
        }

        private static Sprite Outline()
        {
            if (outlineSprite != null) return outlineSprite;
            string path = Path.Combine(Plugin.AssetFolder, "dog_outline.png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Dog outline" };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true)) return null;
            outlineSprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100);
            return outlineSprite;
        }
    }

    /// <summary>
    /// The DOG tab's kennel management: the dog's condition, its gear, the supplies you have for it and the kennel, with
    /// the care actions underneath. Everything uses your real items through the game's own inventory transactions (feed,
    /// water, heal, repair and the bed take an item and use it up; presets move jackets; lockers open as containers) and
    /// changes the one dog the rest of the mod uses: its needs file and its K9 state on the server.
    /// </summary>
    internal sealed partial class DogScreen
    {
        private TextMeshProUGUI statusLine, kennelLevel, kennelNext;
        private readonly TextMeshProUGUI[] facts = new TextMeshProUGUI[6];
        private sealed class CareAction
        {
            public Button Button; public TextMeshProUGUI Title, Detail;
            public Func<string> Text, Info, Locked; public Action Click; public bool InRaid;
        }
        private readonly List<CareAction> actions = new List<CareAction>();
        private bool busy;
        private int foodCount, waterCount, kitCount, repairCount;
        private float nextCount;

        private static readonly string[] BedNames = { "Plain bed", "Fleece-lined bed", "Thermal bedding" };

        private InventoryController Controller => ControllerField?.GetValue(screen) as InventoryController;
        private bool AtHome => !DogVet.InRaid;
        private int KennelLevel => K9.Kennel(profile);

        private void BuildKennel()
        {
            // Armour, under the jacket's slot.
            float cardTop = -(JacketTop * s + SlotHeader + SlotBox + 8) / s;
            armourLine = Text("", 12, FontStyles.Normal, Ink, new Vector2(RightX + 2, cardTop), new Vector2(SlotBox / s + 2, 142));
            armourLine.richText = true;
            armourBar = MakeBar(new Vector2(RightX + 2, cardTop - 36), SlotBox / s - 6, 4);
            AddAction(new Vector2(RightX, -(JacketBottom - 44)), new Vector2(SlotBox / s, 44), () => "REPAIR", () => repairCount + " materials",
                () => NeedsDog() ?? (KennelLevel < 2 ? "Working Dog Station" : null), Repair);

            // The dog's daily care, under its condition.
            float width = (LeftW - 3 * 8) / 4, y = CareTop;
            AddAction(new Vector2(LeftX, y), new Vector2(width, 46), () => "FEED DOG", () => foodCount + " in stock", NeedsDog, () => Feed(true));
            AddAction(new Vector2(LeftX + (width + 8), y), new Vector2(width, 46), () => "GIVE WATER", () => waterCount + " in stock", NeedsDog, () => Feed(false));
            AddAction(new Vector2(LeftX + 2 * (width + 8), y), new Vector2(width, 46), () => "HEAL DOG", () => kitCount + (kitCount == 1 ? " vet kit" : " vet kits"), NeedsDog, Heal, true);
            AddAction(new Vector2(LeftX + 3 * (width + 8), y), new Vector2(width, 46), () => "FILL BOWLS",
                () => { DogNeeds n = NeedsStore.Current; return $"food {Mathf.RoundToInt(n?.FoodDish ?? 0)}%  water {Mathf.RoundToInt(n?.WaterDish ?? 0)}%"; },
                () => NeedsDog() ?? (KennelLevel < 1 ? "Makeshift Kennel" : null), FillBowls);

            // The kennel.
            // Where it was before the Behaviour panel grew a row (1.2.0): the presets end level with the panel's old bottom.
            const float presetTop = BehaviourTop - 140 + 88, gap = 8;
            float top = -(JacketBottom + 8), inner = RightW - 20, cell = inner / 3, button = (inner - 3 * gap) / 4;
            float kennelHeight = top - presetTop - 12;
            Panel("Kennel", new Vector2(RightX, top), new Vector2(RightW, kennelHeight));
            kennelLevel = Text("", 13, FontStyles.Bold, Ink, new Vector2(RightX + 76, top - 5), new Vector2(RightW - 86, 18));
            string[] names = { "RECOVERY", "NEEDS DROP", "VET STATION", "BED", "TRAINING", "SMELLS LOOT WITHIN" };
            for (int i = 0; i < facts.Length; i++)
            {
                Vector2 at = new Vector2(RightX + 10 + (i % 3) * cell, top - 30 - (i / 3) * 40);
                Text(names[i], 10.5f, FontStyles.Bold, Soft, at, new Vector2(cell - 8, 14));
                facts[i] = Text("", 15, FontStyles.Normal, Ink, at + new Vector2(0, -14), new Vector2(cell - 8, 22));
                facts[i].enableWordWrapping = false;
            }
            kennelNext = Text("", 12, FontStyles.Italic, new Color(.9f, .75f, .5f), new Vector2(RightX + 10, top - 112), new Vector2(inner, 34));
            kennelNext.richText = true;
            float row = top - kennelHeight + 56;
            AddAction(new Vector2(RightX + 10, row), new Vector2(button, 46), () => "UPGRADE BED", () => K9.Bed >= 2 ? "fully upgraded" : "to " + BedNames[K9.Bed + 1].ToLowerInvariant(), BedLocked, UpgradeBed);
            AddAction(new Vector2(RightX + 10 + (button + gap), row), new Vector2(button, 46), () => "TRAIN DOG", () => "level " + K9.Training + " of 3", TrainLocked, Train);
            AddAction(new Vector2(RightX + 10 + 2 * (button + gap), row), new Vector2(button, 46), () => "ARMOUR LOCKER", () => "open", () => NeedsDog() ?? (KennelLevel < 2 ? "Working Dog Station" : null), () => OpenLocker(DogGear.ArmourLocker));
            AddAction(new Vector2(RightX + 10 + 3 * (button + gap), row), new Vector2(button, 46), () => "PREMIUM STORAGE", () => "open", () => NeedsDog() ?? (KennelLevel < 3 ? "Tactical K9 Facility" : null), () => OpenLocker(DogGear.PremiumLocker));

            // Presets.
            Panel("Jacket presets", new Vector2(RightX, presetTop), new Vector2(RightW, 88));
            var how = Text("click to wear  ·  shift-click to save the jacket on the dog", 11, FontStyles.Italic, Soft, new Vector2(RightX + 140, presetTop - 6), new Vector2(RightW - 150, 16));
            how.alignment = TextAlignmentOptions.TopRight;
            for (int i = 0; i < 4; i++)
            {
                int slot = i;
                AddAction(new Vector2(RightX + 10 + i * (button + gap), presetTop - 30), new Vector2(button, 46), () => "PRESET " + (slot + 1), () => PresetName(slot), () => PresetLocked(slot), () => Preset(slot));
            }

            // What just happened.
            statusLine = Text("", 14, FontStyles.Italic, new Color(.9f, .75f, .5f), new Vector2(LeftX + 2, BehaviourTop - BehaviourHeight - 12), new Vector2(RightX + RightW - LeftX, 22));
            var rename = ButtonWithLabel("RENAME", new Vector2(LeftX + LeftW - 92, -10), () => { if (NeedsDog() == null) RenameDialog.OpenFromMenu(); }, new Vector2(92, 24));
            rename.Item2.fontSize = 11.5f * s;
            RefreshKennel();
        }

        // A button that says what it does and, under that, what it has to work with or why it can't be used yet.
        private void AddAction(Vector2 at, Vector2 size, Func<string> text, Func<string> info, Func<string> locked, Action click, bool inRaid = false)
        {
            var (button, title) = ButtonWithLabel("", at, () => Run(locked, click, inRaid), size);
            title.rectTransform.anchorMin = new Vector2(0, .46f); title.rectTransform.anchorMax = Vector2.one;
            title.rectTransform.offsetMin = new Vector2(4, 0); title.rectTransform.offsetMax = new Vector2(-4, -2);
            title.fontSize = 12.5f * s; title.enableWordWrapping = false;
            title.alignment = TextAlignmentOptions.Bottom;
            TextMeshProUGUI detail = Text("", 10.5f, FontStyles.Normal, Soft, Vector2.zero, size, (RectTransform)button.transform);
            detail.rectTransform.anchorMin = Vector2.zero; detail.rectTransform.anchorMax = new Vector2(1, .46f);
            detail.rectTransform.offsetMin = new Vector2(4, 2); detail.rectTransform.offsetMax = new Vector2(-4, 0);
            detail.alignment = TextAlignmentOptions.Top; detail.enableWordWrapping = false;
            detail.enableAutoSizing = true; detail.fontSizeMin = 8 * s; detail.fontSizeMax = 10.5f * s;
            actions.Add(new CareAction { Button = button, Title = title, Detail = detail, Text = text, Info = info, Locked = locked, Click = click, InRaid = inRaid });
        }

        private void Run(Func<string> locked, Action click, bool inRaid)
        {
            if (busy) { Status("One thing at a time."); return; }
            string why = !AtHome && !inRaid ? "Kennel care is for out of raids." : locked();
            if (why != null) { Status(char.ToUpperInvariant(why[0]) + why.Substring(1) + "."); return; }
            try { click(); }
            catch (Exception e) { Status("That didn't work."); Plugin.Log.LogError("DOG tab action failed: " + e); }
        }

        private void Status(string text) { if (statusLine != null) statusLine.text = Lang.T(text); statusUntil = Time.unscaledTime + 8; }
        private float statusUntil;

        private string NeedsDog() => K9.Owned ? null : "you have no dog yet";

        // ---- what's shown -------------------------------------------------------------------------------------------

        private void RefreshKennel()
        {
            if (kennelLevel == null) return;
            if (Time.unscaledTime >= nextCount) { nextCount = Time.unscaledTime + 1; CountSupplies(); }
            foreach (CareAction a in actions)
            {
                string locked = AtHome || a.InRaid ? a.Locked() : "out of raids only";
                a.Title.text = Lang.T(a.Text());
                a.Detail.text = Lang.T(locked ?? a.Info());
                a.Title.color = locked == null && !busy ? Ink : Soft;
                a.Detail.color = locked == null ? Soft : new Color(.72f, .5f, .38f);
                a.Button.interactable = locked == null && !busy;
            }
            if (statusLine != null && Time.unscaledTime > statusUntil) statusLine.text = "";
            RefreshArmour();
            int level = KennelLevel;
            kennelLevel.text = level == 0 ? Lang.T("NOT BUILT") : Lang.T("LEVEL " + level) + "  ·  " + Lang.T(K9.KennelNames[level - 1]).ToUpperInvariant();
            facts[0].text = Lang.T($"+{K9.RecoveryPerHour(level, K9.Bed):0}% an hour");
            facts[1].text = Lang.T($"{K9.RestPace(level) * 100:0}% as fast");
            facts[2].text = Lang.T(level >= 3 ? "kits go 50% further" : "none yet");
            facts[3].text = Lang.T(BedNames[Mathf.Clamp(K9.Bed, 0, 2)]);
            facts[4].text = Lang.T(K9.Training + " of 3");
            facts[5].text = (10 + 2 * Mathf.Clamp(K9.Training, 0, 3)) + " m";
            if (level >= 3) { kennelNext.text = ""; return; }
            string next = K9.KennelNames[level], building = Building(level + 1), held = KennelUi.Locked(level + 1, profile);
            kennelNext.text = Lang.T(building != null ? $"Building the {next}: {building}" : held != null ? $"Next: {next}  ·  {held}" : $"Next: {next}  ·  ready to build in your Rest Space");
        }

        // What it wears, under the jacket's slot.
        private void RefreshArmour()
        {
            if (armourLine == null) return;
            CompoundItem jacket = AtHome ? DogGear.Worn(profile) : DogBag.FindInRaid(GamePlayerOwner.MyPlayer);
            var b = new StringBuilder();
            float wearLeft = 0;
            if (jacket == null) b.Append("<color=#9a9a94>No jacket</color>");
            else
            {
                DogArmour.Tier tier = DogArmour.Of(jacket);
                if (tier != null && tier.Class > 0)
                {
                    K9.Armour.TryGetValue(jacket.Id.ToString(), out K9.Wear wear);
                    float now = wear?.Now ?? tier.Durability, max = wear?.Max > 0 ? wear.Max : tier.Durability;
                    wearLeft = tier.Durability > 0 ? now / tier.Durability : 0;
                    b.AppendLine($"<b>Class {tier.Class}</b>");
                    b.AppendLine($"Durability {now:0}/{max:0}");
                    b.AppendLine();
                    b.AppendLine($"Speed {tier.Speed * 100:0}%");
                }
                else b.AppendLine("No armour").AppendLine("Full speed");
                b.Append($"{jacket.GetAllItems().Count() - 1} items inside");
            }
            armourLine.text = Lang.T(b.ToString());
            armourBar.Track.enabled = armourBar.Colour.enabled = wearLeft > 0;
            SetBar(armourBar, wearLeft, wearLeft < .25f ? Danger : wearLeft < .6f ? Caution : Good);
        }

        private void CountSupplies()
        {
            IEnumerable<Item> items = AtHome ? DogPantry.AtHome(profile) : DogPantry.Sources(GamePlayerOwner.MyPlayer, false, false);
            foodCount = waterCount = kitCount = repairCount = 0;
            foreach (Item i in items)
            {
                string id = i.TemplateId.ToString();
                int n = Mathf.Max(1, i.StackObjectsCount);
                if (DogNeeds.Value(id, true, 1) > 0) foodCount += n;
                if (DogNeeds.Value(id, false, 1) > 0) waterCount += n;
                if (DogVet.IsKit(i)) kitCount += n;
                if (DogArmour.RepairMaterials.ContainsKey(id)) repairCount += n;
            }
        }

        private string Building(int level)
        {
            var built = profile?.Hideout?.Improvements;
            if (built == null || !built.TryGetValue(K9.KennelIds[level - 1], out var i) || i.Completed) return null;
            long left = i.CompleteTimestamp - DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return left <= 0 ? null : $"{left / 3600}h {left % 3600 / 60:00}m left";
        }

        // ---- care -----------------------------------------------------------------------------------------------

        private void Use(Item item, Action done, string doing)
        {
            string name = item.ShortName.Localized();
            busy = true;
            DogPantry.Use(Controller, item, ok =>
            {
                busy = false;
                if (!ok) { Status("Couldn't use the " + name + "."); return; }
                done();
                NeedsStore.Save();
                Status(doing.Replace("{item}", name));
                nextCount = 0;
                RefreshKennel();
            });
        }

        private void Feed(bool food)
        {
            DogNeeds needs = NeedsStore.Current;
            float now = food ? needs.Hunger : needs.Thirst;
            if (now > 95) { Status(Plugin.DogName.Value + (food ? " isn't hungry." : " isn't thirsty.")); return; }
            Item item = DogPantry.BestAtHome(profile, i => DogPantry.ValueOf(i, food), 100 - now);
            if (item == null) { Status(food ? "No dog food or meat in your stash, lockers or gear." : "No drinking water in your stash, lockers or gear."); return; }
            float value = DogPantry.ValueOf(item, food);
            Use(item, () => { if (food) needs.Hunger = Mathf.Min(100, needs.Hunger + value); else needs.Thirst = Mathf.Min(100, needs.Thirst + value); },
                Plugin.DogName.Value + (food ? " ate the {item}." : " drank the {item}."));
        }

        private void FillBowls()
        {
            DogNeeds needs = NeedsStore.Current;
            bool food = needs.FoodDish <= needs.WaterDish;
            float dish = food ? needs.FoodDish : needs.WaterDish;
            if (dish > 90) { Status("Both bowls are full."); return; }
            Item item = DogPantry.BestAtHome(profile, i => DogPantry.ValueOf(i, food), 100 - dish);
            if (item == null) { Status(food ? "No dog food or meat to fill the bowl with." : "No water to fill the bowl with."); return; }
            float value = DogPantry.ValueOf(item, food);
            Use(item, () => { if (food) needs.FoodDish = Mathf.Min(100, needs.FoodDish + value); else needs.WaterDish = Mathf.Min(100, needs.WaterDish + value); },
                (food ? "Food" : "Water") + " bowl filled with the {item}.");
        }

        // Only the K9 veterinary kit heals the dog. In a raid you go and do it, beside the dog; at home it's done at once.
        private void Heal()
        {
            string name = Plugin.DogName.Value;
            if (DogVet.InRaid)
            {
                Companion dog = Companion.Raid;
                if (dog == null) { Status("Your dog isn't with you."); return; }
                Item kit = DogPantry.BestKit(GamePlayerOwner.MyPlayer, dog.HealthMax > 0 ? (1 - dog.HealthNow / dog.HealthMax) * 100 : 0);
                if (kit == null) { Status("No K9 veterinary kit on you or in " + name + "'s jacket."); return; }
                string problem = dog.Treat(kit);
                if (problem != null) Status(problem);
                return;
            }
            DogNeeds needs = NeedsStore.Current;
            if (needs.Health >= 99.5f) { Status(name + " is in good health."); return; }
            float multiplier = KennelLevel >= 3 ? VetMath.StationBonus : 1f;   // the Tactical K9 Facility's veterinary station
            Item item = DogPantry.BestAtHome(profile, i => DogVet.IsKit(i) ? DogVet.Holds(i, multiplier) : 0, 100 - needs.Health);
            if (item == null) { Status("No K9 veterinary kit in your stash, lockers or gear (Dr. Yang sells them)."); return; }
            busy = true;
            DogVet.UseAtHome(item, Controller, said => { busy = false; Status(said); nextCount = 0; RefreshKennel(); });
        }

        private void Repair()
        {
            CompoundItem jacket = DogGear.Worn(profile);
            DogArmour.Tier tier = DogArmour.Of(jacket);
            if (jacket == null || tier == null || tier.Class == 0) { Status("The dog isn't wearing an armoured jacket."); return; }
            K9.Wear wear = K9.WearOf(jacket.Id.ToString(), tier.Durability);
            if (wear.Now >= wear.Max - .5f) { Status("The jacket is in full repair."); return; }
            Item material = DogPantry.BestAtHome(profile, i => DogArmour.Repairs(tier, i.TemplateId.ToString()) ? DogArmour.RepairMaterials[i.TemplateId.ToString()] : 0, wear.Max - wear.Now);
            if (material == null) { Status(tier.Class <= 3 ? "Needs aramid, Cordura or ripstop fabric, or KEKTAPE." : "Armoured jackets need aramid fiber or Cordura fabric."); return; }
            float put = Mathf.Min(DogArmour.RepairMaterials[material.TemplateId.ToString()], wear.Max - wear.Now);
            Use(material, () =>
            {
                wear.Now += put;
                wear.Max = Mathf.Max(wear.Now, wear.Max - put * DogArmour.RepairWear);
                K9.Save();
            }, $"Jacket repaired with the {{item}} (+{put:0}).");
        }

        private string BedLocked()
        {
            if (NeedsDog() != null) return NeedsDog();
            if (KennelLevel < 1) return "Makeshift Kennel";
            if (!K9.BedUnlocked && !K9.QuestDone(profile, K9.TrustIsEarned)) return "quest Trust Is Earned";
            return K9.Bed >= 2 ? "fully upgraded" : null;
        }

        // Fleece lining: a K9 fleece bed liner, or 1 fleece fabric. Thermal bedding: a K9 thermal dog bed, or 2 ripstop
        // fabric and 1 paracord. (The bed items are Dr. Yang's, LL2.)
        private void UpgradeBed()
        {
            Item bedItem = DogPantry.AtHome(profile).FirstOrDefault(i => i.TemplateId.ToString() == (K9.Bed == 0 ? "6a5d0c0c4fb9a3c5e1d60013" : "6a5d0c0c4fb9a3c5e1d60014"));
            var needed = bedItem != null ? new List<(string Template, int Count)>()
                : K9.Bed == 0
                ? new List<(string Template, int Count)> { ("5e2af47786f7746d404f3aaa", 1) }
                : new List<(string, int)> { ("5e2af4a786f7746d3f3c3400", 2), ("5c12688486f77426843c7d32", 1) };
            var items = new List<Item>();
            if (bedItem != null) items.Add(bedItem);
            foreach (var (template, count) in needed)
            {
                var found = DogPantry.AtHome(profile).Where(i => i.TemplateId.ToString() == template).Take(count).ToList();
                if (found.Count < count) { Status(K9.Bed == 0 ? "Lining the bed takes a K9 fleece bed liner or 1 fleece fabric." : "Thermal bedding takes a K9 thermal dog bed, or 2 ripstop fabric and 1 paracord."); return; }
                items.AddRange(found);
            }
            UseAll(items, () =>
            {
                K9.Bed++;
                K9.Save();
                Status("The dog's bed is now a " + BedNames[K9.Bed] + ": it rests better.");
            });
        }

        private void UseAll(List<Item> items, Action done)
        {
            if (items.Count == 0) { busy = false; done(); RefreshKennel(); return; }
            busy = true;
            Item next = items[0]; items.RemoveAt(0);
            DogPantry.Use(Controller, next, ok =>
            {
                if (!ok) { busy = false; Status("Couldn't use the " + next.ShortName.Localized() + "."); return; }
                UseAll(items, done);
            });
        }

        private string TrainLocked()
        {
            if (NeedsDog() != null) return NeedsDog();
            if (KennelLevel < 1) return "Makeshift Kennel";
            return K9.Training >= 3 ? "fully trained" : null;
        }

        // One K9 training kit (Dr. Yang, LL2) per level.
        private void Train()
        {
            Item kit = DogPantry.AtHome(profile).FirstOrDefault(i => i.TemplateId.ToString() == "6a5d0c0c4fb9a3c5e1d60015");
            if (kit == null) { Status("Training takes a K9 training kit (Dr. Yang, loyalty level 2)."); return; }
            Use(kit, () => { K9.Training++; K9.Save(); },
                Plugin.DogName.Value + " trained with the {item}: training level " + (K9.Training + 1) + ", smells valuables up to " + (10 + 2 * (K9.Training + 1)) + " m away.");
        }

        private void OpenLocker(string template)
        {
            CompoundItem locker = DogGear.Locker(profile, template);
            if (locker == null) { Status(SetUpLockers()); return; }
            ItemUiContext.Instance?.OpenItem(locker, ContextOf(locker));
        }

        // The locker isn't in the profile the game loaded: the server mod sets the lockers up (or puts a stray one back) now
        // and says what it did. The game reads the profile only at start, so a locker made now opens after a restart.
        private static string SetUpLockers()
        {
            string reply = null;
            try { reply = RequestHandler.PostJson("/k9companion/k9/lockers", "{}"); }
            catch (Exception e) { Plugin.Log.LogWarning("Kennel lockers: " + e.Message); }
            JObject answer = null;
            try { if (!string.IsNullOrEmpty(reply)) answer = JObject.Parse(reply); }
            catch (Exception) { }
            Plugin.Log.LogInfo("Kennel lockers missing from the profile; server says: " + (reply ?? "nothing"));
            if (answer == null || (answer["ok"] == null && answer["problem"] == null))
                return "The server mod didn't answer: update K9 Companion's server mod and restart the SPT server.";
            if (answer["ok"]?.Value<bool>() == true) return "The kennel lockers are ready: restart the game to open them.";
            return "The kennel lockers couldn't be set up (" + (answer["problem"]?.Value<string>() ?? "unknown") + "). The SPT server window says more.";
        }

        // A window on an item goes by the item's own context, as when you open it from your stash.
        private readonly List<ItemContext> opened = new List<ItemContext>();
        private ItemContext ContextOf(Item item)
        {
            ItemContext child = context.CreateChild(item);
            opened.Add(child);
            return child;
        }

        // ---- presets ------------------------------------------------------------------------------------------------

        private int PresetSlots => KennelLevel >= 3 ? 4 : KennelLevel >= 2 ? 2 : 0;

        private string PresetLocked(int slot)
        {
            if (NeedsDog() != null) return NeedsDog();
            if (slot >= PresetSlots) return slot < 2 ? "Working Dog Station" : "Tactical K9 Facility";
            return null;
        }

        private string PresetName(int slot)
        {
            string id = K9.Presets[slot];
            if (string.IsNullOrEmpty(id)) return "empty";
            Item jacket = Find(id);
            if (jacket == null) return "gone";
            return jacket.ShortName.Localized() + (DogGear.Worn(profile) == jacket ? " · worn" : "");
        }

        private Item Find(string id)
        {
            var roots = new Item[] { profile?.Inventory?.Stash, DogGear.Locker(profile, DogGear.ArmourLocker), DogGear.Locker(profile, DogGear.PremiumLocker),
                                     DogGear.Holder(profile), profile?.Inventory?.Equipment };
            return roots.Where(r => r != null).SelectMany(r => r.GetAllItems()).FirstOrDefault(i => i.Id.ToString() == id);
        }

        // Click: put the preset's jacket on the dog (the one it wears goes to the armour locker, or your stash).
        // Shift-click, or an empty preset: remember the jacket the dog wears now.
        private void Preset(int slot)
        {
            CompoundItem worn = DogGear.Worn(profile);
            string id = K9.Presets[slot];
            if (string.IsNullOrEmpty(id) || Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift))
            {
                if (worn == null) { Status("Put a jacket on the dog first, then save it here."); return; }
                K9.Presets[slot] = worn.Id.ToString(); K9.Save();
                Status($"Preset {slot + 1}: {worn.ShortName.Localized()}.");
                return;
            }
            Item jacket = Find(id);
            if (jacket == null) { K9.Presets[slot] = ""; K9.Save(); Status("That jacket is gone; the preset is cleared."); return; }
            if (jacket == worn) { Status("The dog is already wearing it."); return; }
            Slot dogSlot = DogGear.JacketSlot(profile);
            if (dogSlot == null) { Status("The dog's gear isn't set up yet."); return; }
            if (worn == null) { Move(jacket, dogSlot.CreateItemAddress(), () => Status("Preset " + (slot + 1) + " on.")); return; }
            ItemAddress away = SpotFor(worn, jacket);
            if (away == null) { Status("No room to put the current jacket away."); return; }
            Move(worn, away, () => Move(jacket, dogSlot.CreateItemAddress(), () => Status("Preset " + (slot + 1) + " on.")));
        }

        // Where the jacket coming off goes: the armour locker if built, otherwise the stash.
        private ItemAddress SpotFor(Item jacket, Item avoid)
        {
            var places = new List<CompoundItem>();
            if (KennelLevel >= 2) places.Add(DogGear.Locker(profile, DogGear.ArmourLocker));
            places.Add(profile.Inventory.Stash);
            foreach (CompoundItem place in places.Where(p => p != null && p != avoid))
                foreach (var grid in place.Grids)
                {
                    var spot = grid.FindLocationForItem(jacket);
                    if (spot != null) return spot;
                }
            return null;
        }

        private void Move(Item item, ItemAddress to, Action done)
        {
            InventoryController controller = Controller;
            var move = ItemManipulator.Move(item, to, controller, simulate: true);
            if (controller == null || move.Failed) { Status("That jacket can't be moved there."); return; }
            busy = true;
            controller.TryRunNetworkTransaction(move, result =>
            {
                busy = false;
                if (!result.Succeed) { Status("The jacket couldn't be moved."); return; }
                done();
                RefreshKennel();
            });
        }
    }

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
            // Last: other mods add their own tabs in the same place, and the DOG tab goes to the right of all of them.
            // A finalizer rather than a postfix: it runs even if the screen or another mod's postfix throws, so the tab
            // hidden at the start never stays hidden.
            harmony.Patch(show, prefix: new HarmonyMethod(typeof(DogTab), nameof(HideTab)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(DogTab), nameof(AddTab)) { priority = Priority.Last });
            // Leaving the screen (TRADERS, FLEA MARKET ... at the bottom) looks the selected tab up by its index among the
            // game's own tabs; the DOG tab isn't one of them, so that threw and the screen never closed.
            MethodInfo leave = AccessTools.Method(typeof(InventoryScreen.InventoryScreenController), "CloseAction")
                ?? throw new MissingMethodException(nameof(InventoryScreen.InventoryScreenController), "CloseAction");
            harmony.Patch(leave, prefix: new HarmonyMethod(typeof(DogTab), nameof(BeforeLeaving)));
        }

        // The screen goes back to GEAR, as if the DOG tab had been closed first.
        private static void BeforeLeaving(InventoryScreen.InventoryScreenController __instance)
        {
            try
            {
                InventoryScreen screen = __instance.Screen;
                if (screen == null) return;
                var group = (TabGroup)TabsField.GetValue(screen);
                Tab dog = group?._selectedTab;
                if (dog == null || dog.name != DogTabName || !screen._tabDictionary.TryGetValue(EInventoryTab.Gear, out Tab gear)) return;
                (ControllerOf(dog) as DogTabController)?.PutBack();
                dog.UpdateVisual(selected: false);
                group._selectedTab = gear;
            }
            catch (Exception e) { Plugin.Log.LogError("Leaving the DOG tab: " + e); }
        }

        private const string DogTabName = "K9 DOG tab";
        private static Tab shownTab;

        // Out of sight while other mods place their tabs, which they put right of the rightmost they see: with the DOG
        // tab there each time, theirs and ours would keep stepping past each other, a place further right every opening.
        private static void HideTab()
        {
            if (shownTab != null) shownTab.gameObject.SetActive(false);
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
                Tab dog = bar.Find(DogTabName)?.GetComponent<Tab>();
                if (dog == null)
                {
                    dog = UnityEngine.Object.Instantiate(template, bar);
                    dog.name = DogTabName;
                    dog.gameObject.AddComponent<DogTabSpot>();
                    foreach (var localized in dog.GetComponentsInChildren<LocalizedText>(true)) localized.enabled = false;
                    foreach (var label in dog.GetComponentsInChildren<TMPro.TMP_Text>(true)) label.text = Lang.T("DOG");
                }
                shownTab = dog;
                dog.gameObject.SetActive(true);
                Place(dog, template, tabs, __instance._backButton != null ? __instance._backButton.transform as RectTransform : null);
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

        /// <summary>The character screen's tab group while it's open (the DOG tab is hooked into it).</summary>
        internal static TabGroup Group => hookedGroup;

        // The tab bar places its tabs by hand (no layout group), so a copy lands exactly on the tab it was copied from and
        // hides underneath it. It goes one place right of the rightmost tab, by the same spacing as the game's last two;
        // other mods add tabs to the same bar (Weekend Drops Continued's daily quests, wrist accessories...), and
        // DogTabSpot keeps it clear of those. However many there are, the bar scrolls (TabScroll), so none is ever off the
        // screen or under the back button.
        private static void Place(Tab dog, Tab template, IReadOnlyDictionary<EInventoryTab, Tab> tabs, RectTransform back)
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
            // After the game's own tabs, never after another mod's: measured from those, it lands in the same place each time.
            var game = tabs.Values.Where(t => t != null && t.transform.parent == bar && t.gameObject.activeSelf)
                .Select(t => (RectTransform)t.transform).OrderBy(t => t.anchoredPosition.x).ToList();
            RectTransform own = game.LastOrDefault() ?? from;
            var spot = dog.GetComponent<DogTabSpot>() ?? dog.gameObject.AddComponent<DogTabSpot>();
            spot.Step = step;
            spot.After = own;
            spot.MoveToFree();
            try { TabScroll.Fit(bar, game.FirstOrDefault() ?? from, step, back); }
            catch (Exception e) { Plugin.Log.LogWarning("The tab bar couldn't be made to scroll (the tabs stay as they are): " + e.Message); }
            Report(dog, $"at {to.anchoredPosition}, the first free place right of {own.name}");
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
            PutBack();
            return gear == null || await gear.TryHide();
        }

        /// <summary>The gear view as the game left it: the dog's panel gone, the equipment columns back.</summary>
        internal void PutBack()
        {
            DogScreen.Close();
            foreach (GameObject part in hidden) if (part != null) part.SetActive(true);
            hidden.Clear();
        }
    }

    /// <summary>
    /// Keeps the DOG tab clear of tabs other mods put on the same bar, whenever they add them: it takes the first free
    /// place right of the game's own tabs. The bar scrolls (TabScroll), so it never has to squeeze in anywhere else.
    /// </summary>
    internal sealed class DogTabSpot : MonoBehaviour
    {
        internal float Step;
        internal RectTransform After;
        private float nextCheck;
        private static readonly Vector3[] Corners = new Vector3[4];

        // In screen pixels, so tabs that aren't anchored like the game's compare too.
        internal static Rect WorldRect(RectTransform t)
        {
            t.GetWorldCorners(Corners);
            Camera cam = CameraOf(t);
            Vector2 a = RectTransformUtility.WorldToScreenPoint(cam, Corners[0]), b = RectTransformUtility.WorldToScreenPoint(cam, Corners[2]);
            return Rect.MinMaxRect(Mathf.Min(a.x, b.x), Mathf.Min(a.y, b.y), Mathf.Max(a.x, b.x), Mathf.Max(a.y, b.y));
        }

        internal static Camera CameraOf(Transform t)
        {
            Canvas canvas = t.GetComponentInParent<Canvas>();
            return canvas == null || canvas.rootCanvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.rootCanvas.worldCamera;
        }

        /// <summary>The tab on the same bar the DOG tab is on top of, or null.</summary>
        internal Transform Blocker()
        {
            Rect mine = WorldRect((RectTransform)transform);
            // A little in from its edges, so neighbours that only touch it don't count.
            Rect inner = Rect.MinMaxRect(mine.xMin + mine.width * .15f, mine.yMin + mine.height * .15f, mine.xMax - mine.width * .15f, mine.yMax - mine.height * .15f);
            Transform bar = transform.parent;
            for (int i = 0; i < bar.childCount; i++)
            {
                if (!(bar.GetChild(i) is RectTransform other) || other == transform || !other.gameObject.activeInHierarchy) continue;
                Rect theirs = WorldRect(other);
                // Only things about the size of a tab: not a background or a decoration.
                if (theirs.width > inner.width * 4 || theirs.height > inner.height * 4 || theirs.width < 1 || theirs.height < 1) continue;
                if (theirs.Overlaps(inner)) return other;
            }
            return null;
        }

        internal void MoveToFree()
        {
            if (Step < 1 || After == null || transform.parent == null) return;
            var me = (RectTransform)transform;
            for (int i = 1; i <= 24; i++)
            {
                me.anchoredPosition = new Vector2(After.anchoredPosition.x + i * Step, After.anchoredPosition.y);
                if (Blocker() == null) return;
            }
            // Nowhere free: right after the game's tabs, where it always used to go.
            me.anchoredPosition = new Vector2(After.anchoredPosition.x + Step, After.anchoredPosition.y);
        }

        private void LateUpdate()
        {
            if (Step < 1 || After == null || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + .25f;
            Transform other = Blocker();
            if (other == null) return;
            MoveToFree();
            Plugin.Log.LogInfo($"DOG tab moved to {((RectTransform)transform).anchoredPosition}: {other.name} was on top of it.");
        }
    }

    /// <summary>
    /// The character screen's tab bar, scrolling sideways when it holds more tabs than fit (other mods add their own:
    /// wrist accessories, weekend drops and so on). The tabs go into a strip inside the bar that ends short of the
    /// screen's back button and shows only what's inside it; the mouse wheel over the tabs, or the arrows at its right end
    /// when there's more than fits, slides them along. The bar itself, and every tab, stay as they are.
    /// </summary>
    internal sealed class TabScroll : MonoBehaviour, IScrollHandler
    {
        private const string ViewName = "K9 tab scroll", ContentName = "K9 tab strip";
        private RectTransform view, content, bar, first, back;
        private float offset, step = 100, nextCheck, contentLeft = float.MaxValue, contentRight;
        private Vector2 home;
        private GameObject arrows;
        private Tab revealed;
        private static readonly Vector3[] Corners = new Vector3[4];

        /// <summary>
        /// Puts the tabs in the scrolling strip (the first time) and fits it to the screen as it is now. 'tabs' is what the
        /// tabs are in: the bar the first time, the strip after that.
        /// </summary>
        internal static void Fit(Transform tabs, RectTransform firstTab, float step, RectTransform backButton)
        {
            TabScroll scroll = tabs.name == ContentName ? tabs.parent.GetComponent<TabScroll>() : null;
            if (scroll == null)
            {
                var bar = (RectTransform)tabs;
                var viewObject = new GameObject(ViewName, typeof(RectTransform));
                var view = (RectTransform)viewObject.transform;
                view.SetParent(bar, false);
                view.SetSiblingIndex(firstTab.GetSiblingIndex());
                view.anchorMin = view.anchorMax = view.pivot = Vector2.zero;
                viewObject.AddComponent<LayoutElement>().ignoreLayout = true;
                viewObject.AddComponent<RectMask2D>();
                // Catches the mouse wheel between and around the tabs (and, being clear, shows nothing).
                var catcher = viewObject.AddComponent<Image>(); catcher.color = Color.clear;
                scroll = viewObject.AddComponent<TabScroll>();
                scroll.view = view; scroll.bar = bar;
                scroll.FitView(firstTab);
                // The strip is the bar's double (its size and place), so every tab keeps its numbers in it.
                var contentObject = new GameObject(ContentName, typeof(RectTransform));
                var content = (RectTransform)contentObject.transform;
                content.SetParent(view, false);
                contentObject.AddComponent<LayoutElement>().ignoreLayout = true;
                content.anchorMin = content.anchorMax = Vector2.zero; content.pivot = bar.pivot; content.sizeDelta = bar.rect.size;
                content.position = bar.position; content.localScale = Vector3.one; content.localRotation = Quaternion.identity;
                scroll.content = content;
                foreach (Tab tab in bar.GetComponentsInChildren<Tab>(true).Where(t => t.transform.parent == bar).ToList())
                    tab.transform.SetParent(content, true);
                scroll.home = content.anchoredPosition;
                scroll.MakeArrows(firstTab);
                Plugin.Log.LogInfo($"Tab bar '{bar.name}' scrolls now: {content.childCount} tab(s) in a strip {view.rect.width:0} wide.");
            }
            scroll.first = firstTab; scroll.step = step; scroll.back = backButton; scroll.revealed = null;
            scroll.Refit(0);
        }

        // The strip's window: from the first tab to the first button right of it on its row (the back button), or the
        // screen's edge.
        private void FitView(RectTransform firstTab)
        {
            Rect row = DogTabSpot.WorldRect(firstTab);
            float right = Screen.width - 4;
            // The screen's BACK button is the game's own kind of button, not a Unity one: it's measured by name.
            if (back != null && back.gameObject.activeInHierarchy) right = Stop(back, row, right, true);
            foreach (Selectable button in Selectable.allSelectablesArray)
                if (button != null && button.isActiveAndEnabled && !button.transform.IsChildOf(bar) && button.transform is RectTransform rect) right = Stop(rect, row, right, false);
            Camera cam = DogTabSpot.CameraOf(bar);
            float pad = row.height * .3f;
            RectTransformUtility.ScreenPointToLocalPointInRectangle(bar, new Vector2(row.xMin - 2, row.yMin - pad), cam, out Vector2 low);
            RectTransformUtility.ScreenPointToLocalPointInRectangle(bar, new Vector2(Mathf.Max(right, row.xMax), row.yMax + pad), cam, out Vector2 high);
            // The tabs stay where they are on screen while their window moves.
            Vector3 keep = content != null ? content.position : Vector3.zero;
            view.anchoredPosition = low - bar.rect.min;
            view.sizeDelta = high - low;
            if (content != null) content.position = keep;
        }

        // Where the window has to end for something on the tabs' row, right of the first tab (and not part of the bar).
        private float Stop(RectTransform thing, Rect row, float right, bool withLabel)
        {
            Rect r = DogTabSpot.WorldRect(thing);
            // Its visible part too: a button's label can stick out of its own rect (BACK's does).
            if (withLabel) foreach (Graphic g in thing.GetComponentsInChildren<Graphic>())
                if (g.isActiveAndEnabled && g.color.a > .05f) { Rect gr = DogTabSpot.WorldRect(g.rectTransform); if (gr.width > 0 && gr.width < row.width * 4) r = Rect.MinMaxRect(Mathf.Min(r.xMin, gr.xMin), Mathf.Min(r.yMin, gr.yMin), Mathf.Max(r.xMax, gr.xMax), Mathf.Max(r.yMax, gr.yMax)); }
            if (r.width < 1 || r.height < 1 || r.width > row.width * 4 || r.yMax < row.yMin + row.height * .25f || r.yMin > row.yMax - row.height * .25f) return right;
            return r.xMin > row.xMin + row.width * .5f ? Mathf.Min(right, r.xMin - 6) : right;
        }

        // Fits the window again (the screen may have changed, or still be sliding in), then scrolls back to 'keep'.
        private void Refit(float keep)
        {
            if (view == null || content == null || first == null) return;
            content.anchoredPosition = home;
            offset = 0;
            FitView(first);
            home = content.anchoredPosition;
            Measure();
            Scroll(keep);
        }

        // Where the tabs reach, left and right, in the window's own units as if not scrolled; the arrows show when they
        // don't all fit.
        private void Measure()
        {
            contentLeft = float.MaxValue; contentRight = float.MinValue;
            for (int i = 0; i < content.childCount; i++)
            {
                if (!(content.GetChild(i) is RectTransform tab) || !tab.gameObject.activeSelf) continue;
                tab.GetWorldCorners(Corners);
                float a = view.InverseTransformPoint(Corners[0]).x - offset, b = view.InverseTransformPoint(Corners[2]).x - offset;
                contentLeft = Mathf.Min(contentLeft, Mathf.Min(a, b)); contentRight = Mathf.Max(contentRight, Mathf.Max(a, b));
            }
            bool more = contentLeft < float.MaxValue && contentRight - contentLeft > view.rect.width + 1;
            if (arrows != null && arrows.activeSelf != more) arrows.SetActive(more);
            Scroll(0);
        }

        private float ArrowsWidth => arrows != null && arrows.activeSelf ? ((RectTransform)arrows.transform).rect.width + 2 : 0;

        // Slides the tabs by 'by' (negative brings in the ones on the right), never past either end.
        private void Scroll(float by)
        {
            if (contentLeft == float.MaxValue) return;
            float least = Mathf.Min(0, view.rect.width - ArrowsWidth - contentRight), most = Mathf.Max(0, -contentLeft);
            offset = Mathf.Clamp(offset + by, least, most);
            content.anchoredPosition = home + new Vector2(offset, 0);
        }

        public void OnScroll(PointerEventData eventData)
        {
            Vector2 wheel = eventData.scrollDelta;
            float by = Mathf.Abs(wheel.y) >= Mathf.Abs(wheel.x) ? wheel.y : -wheel.x;
            if (Mathf.Abs(by) > .01f) Scroll(Mathf.Sign(by) * step);
        }

        // Two small arrows at the window's right end, over the tabs: shown only when there's more than fits.
        private void MakeArrows(RectTransform like)
        {
            arrows = new GameObject("K9 tab arrows", typeof(RectTransform));
            var box = (RectTransform)arrows.transform;
            box.SetParent(view, false);
            float h = view.rect.height, w = Mathf.Max(18, like.rect.height * .55f);
            box.anchorMin = box.anchorMax = box.pivot = new Vector2(1, .5f);
            box.anchoredPosition = Vector2.zero;
            box.sizeDelta = new Vector2(w * 2 + 2, h);
            TMP_FontAsset font = like.GetComponentInChildren<TMP_Text>(true)?.font;
            Arrow(box, "◀", 0, w, h, font, () => Scroll(step));
            Arrow(box, "▶", w + 2, w, h, font, () => Scroll(-step));
            arrows.SetActive(false);
        }

        private static void Arrow(RectTransform box, string glyph, float x, float w, float h, TMP_FontAsset font, Action click)
        {
            var go = new GameObject("K9 tab arrow", typeof(RectTransform));
            var r = (RectTransform)go.transform;
            r.SetParent(box, false);
            r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, .5f);
            r.anchoredPosition = new Vector2(x, 0); r.sizeDelta = new Vector2(w, h);
            var back = go.AddComponent<Image>(); back.color = new Color(.07f, .07f, .07f, .92f);
            var button = go.AddComponent<Button>(); button.targetGraphic = back;
            button.onClick.AddListener(() => click());
            var label = new GameObject("Label", typeof(RectTransform)).AddComponent<TextMeshProUGUI>();
            label.rectTransform.SetParent(r, false);
            label.rectTransform.anchorMin = Vector2.zero; label.rectTransform.anchorMax = Vector2.one; label.rectTransform.sizeDelta = Vector2.zero;
            if (font != null) label.font = font;
            label.text = glyph; label.fontSize = Mathf.Max(10, h * .34f); label.alignment = TextAlignmentOptions.Center;
            label.color = new Color(.78f, .78f, .74f); label.raycastTarget = false;
        }

        private void LateUpdate()
        {
            if (content == null || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + .3f;
            // Tabs other mods add a moment later; the screen settling after it opens.
            Refit(offset);
            // The tab you're on is always in view.
            Tab selected = DogTab.Group?._selectedTab;
            if (selected != null && selected != revealed && selected.transform.parent == content) { revealed = selected; Reveal((RectTransform)selected.transform); }
        }

        private void Reveal(RectTransform tab)
        {
            tab.GetWorldCorners(Corners);
            float a = view.InverseTransformPoint(Corners[0]).x, b = view.InverseTransformPoint(Corners[2]).x;
            float left = Mathf.Min(a, b), right = Mathf.Max(a, b), room = view.rect.width - ArrowsWidth;
            if (left < 0) Scroll(-left);
            else if (right > room) Scroll(room - right);
        }
    }
}
