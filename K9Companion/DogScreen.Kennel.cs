using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace K9Companion
{
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
            const float presetTop = BehaviourTop - BehaviourHeight + 88, gap = 8;
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

        private void Status(string text) { if (statusLine != null) statusLine.text = text; statusUntil = Time.unscaledTime + 8; }
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
                a.Title.text = a.Text();
                a.Detail.text = locked ?? a.Info();
                a.Title.color = locked == null && !busy ? Ink : Soft;
                a.Detail.color = locked == null ? Soft : new Color(.72f, .5f, .38f);
                a.Button.interactable = locked == null && !busy;
            }
            if (statusLine != null && Time.unscaledTime > statusUntil) statusLine.text = "";
            RefreshArmour();
            int level = KennelLevel;
            kennelLevel.text = level == 0 ? "NOT BUILT" : "LEVEL " + level + "  ·  " + K9.KennelNames[level - 1].ToUpperInvariant();
            facts[0].text = $"+{K9.RecoveryPerHour(level, K9.Bed):0}% an hour";
            facts[1].text = $"{K9.RestPace(level) * 100:0}% as fast";
            facts[2].text = level >= 3 ? "kits go 50% further" : "none yet";
            facts[3].text = BedNames[Mathf.Clamp(K9.Bed, 0, 2)];
            facts[4].text = K9.Training + " of 3";
            facts[5].text = (10 + 2 * Mathf.Clamp(K9.Training, 0, 3)) + " m";
            if (level >= 3) { kennelNext.text = ""; return; }
            string next = K9.KennelNames[level], building = Building(level + 1), held = KennelUi.Locked(level + 1, profile);
            kennelNext.text = building != null ? $"Building the {next}: {building}" : held != null ? $"Next: {next}  ·  {held}" : $"Next: {next}  ·  ready to build in your Rest Space";
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
            armourLine.text = b.ToString();
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
            if (locker == null) { Status("The locker is set up the next time you start the game."); return; }
            ItemUiContext.Instance?.OpenItem(locker, ContextOf(locker));
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
}
