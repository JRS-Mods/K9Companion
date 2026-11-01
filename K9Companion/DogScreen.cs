using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.Health;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

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
        private const float StatTop = -482, CareTop = -576, BehaviourTop = -640, BehaviourHeight = 140;
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
            behaviourLine = Text("", 11.5f, FontStyles.Italic, Soft, new Vector2(LeftX + 10, top - 103), new Vector2(LeftW - 20, 18));
            approachLine = Text("", 11.5f, FontStyles.Italic, Soft, new Vector2(LeftX + 10, top - 120), new Vector2(LeftW - 20, 18));
        }

        private void Row(string title, float y, (string Text, Func<bool> Chosen, Action Click)[] options, Func<bool> open)
        {
            const float labelWidth = 118, height = 23, gap = 4;
            var label = Text(title, 11.5f, FontStyles.Bold, Soft, new Vector2(LeftX + 10, y - 4), new Vector2(labelWidth, 18));
            label.enableWordWrapping = false;
            float width = (LeftW - labelWidth - 20 - gap * (options.Length - 1)) / options.Length;
            for (int i = 0; i < options.Length; i++)
            {
                var (text, chosen, click) = options[i];
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
            bool server = ServerRules.CombatMode != null || ServerRules.Aggression != null || ServerRules.Approach != null;
            behaviourLine.text = does + (server ? "  (Some of this is set by the server.)" : "");
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
            Text("Drag a K9 jacket, goggles or collar into its slot; plates and an action camera onto the jacket. Jacket and goggles go into PMC raids with the dog and are lost if it or you die; the collar is never lost.",
                 12, FontStyles.Italic, Soft, new Vector2(LeftX + 2, BehaviourTop - BehaviourHeight - 34), new Vector2(RightX + RightW - LeftX, 20));
        }

        // In a raid the jacket rides in the raid container: show what's in it and open it next to the dog.
        private void ShowRaidJacket()
        {
            Player player = GamePlayerOwner.MyPlayer;
            CompoundItem jacket = DogBag.FindInRaid(player);
            Panel("Jacket", new Vector2(RightX, -JacketTop), new Vector2(RightW, JacketBottom - JacketTop));
            jacketNote = Text("", 18, FontStyles.Normal, Ink, new Vector2(RightX + 10, -34), new Vector2(RightW - 20, 60));
            if (jacket == null) { jacketNote.text = "No jacket this raid."; return; }
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
            if (headerLine != null) headerLine.text = "K9 dog  ·  " + state + (where.Length > 0 ? "  ·  " + where : "");
            RefreshBehaviour();
            if (jacketNote != null && inRaid)
            {
                CompoundItem jacket = DogBag.FindInRaid(GamePlayerOwner.MyPlayer);
                bool near = dog != null && dog.DistanceTo(GamePlayerOwner.MyPlayer) <= 3f;
                if (jacket != null) jacketNote.text = $"{jacket.LocalizedName()}: {jacket.GetAllItems().Count() - 1} item(s)." + (near ? "" : "\nStand next to the dog to open it.");
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
            label.text = text; label.fontSize = size * s; label.fontStyle = style; label.color = colour;
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
            if (title != null) Text(title.ToUpperInvariant(), 13, FontStyles.Bold, Accent, topLeft + new Vector2(10, -5), new Vector2(size.x - 20, 18));
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
}
