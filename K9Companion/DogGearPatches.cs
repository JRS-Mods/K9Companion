using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Two small hooks that make the dog's gear behave like any other gear:
    ///  - The game keeps non-quest items out of its quest containers. The dog's holder lives in the quest stash, and its
    ///    jacket rides in the raid quest container, so for places inside the dog's gear (and only there) that one rule
    ///    doesn't apply; every other check (slot filters, space, locks) still does.
    ///  - The jackets are copies of an armoured rig (plates go in them as in yours; your own rig slot won't take one),
    ///    the head gear and camera of a flash storage module; their stash icons are pictures of the actual gear.
    /// </summary>
    internal static class DogGearPatches
    {
        private static readonly MethodInfo ChildOf = AccessTools.Method(typeof(ItemExtensions), nameof(ItemExtensions.IsChildOf),
            new[] { typeof(ItemAddress), typeof(Item), typeof(bool) });
        private static readonly Dictionary<string, Sprite> icons = new Dictionary<string, Sprite>();

        internal static void Patch(Harmony harmony)
        {
            MethodInfo add = AccessTools.Method(typeof(ItemManipulator), nameof(ItemManipulator.Add),
                new[] { typeof(Item), typeof(ItemAddress), typeof(ItemController), typeof(bool), typeof(bool) })
                ?? throw new MissingMethodException(nameof(ItemManipulator), "Add(5 args)");
            if (ChildOf == null) throw new MissingMethodException(nameof(ItemExtensions), nameof(ItemExtensions.IsChildOf));
            harmony.Patch(add, transpiler: new HarmonyMethod(typeof(DogGearPatches), nameof(QuestRule)));

            harmony.Patch(AccessTools.Method(typeof(Slot), nameof(Slot.CheckCompatibility)),
                prefix: new HarmonyMethod(typeof(DogGearPatches), nameof(NotForYou)));
            harmony.Patch(AccessTools.Method(typeof(ItemViewFactory), nameof(ItemViewFactory.LoadItemIcon)),
                prefix: new HarmonyMethod(typeof(DogGearPatches), nameof(Icon)));
            harmony.Patch(AccessTools.Method(typeof(ItemViewFactory), nameof(ItemViewFactory.GetItemSpriteAsync)),
                prefix: new HarmonyMethod(typeof(DogGearPatches), nameof(IconAsync)));
            harmony.Patch(AccessTools.Method(typeof(Slot), nameof(Slot.CheckConditions)),
                postfix: new HarmonyMethod(typeof(DogGearPatches), nameof(Refused)));
            harmony.Patch(AccessTools.Method(typeof(ModSlotView), nameof(ModSlotView.Show),
                    new[] { typeof(Slot), typeof(ItemContext), typeof(ItemController), typeof(ItemUiContext) }),
                postfix: new HarmonyMethod(typeof(DogGearPatches), nameof(CameraSlotPicture)));
        }

        // Why the game won't put something in one of a jacket's slots (plates, the camera): logged once per slot and item.
        private static readonly HashSet<string> refusals = new HashSet<string>();
        private static void Refused(Slot __instance, Item item, Diz.LanguageExtensions.Option<bool> __result)
        {
            if (!__result.Failed || item == null || !DogGear.IsJacket(__instance?.ParentItem)) return;
            if (refusals.Add(__instance.ID + item.TemplateId))
                Plugin.Log.LogInfo($"Dog jacket slot {__instance.ID} refused {item.TemplateId}: {__result.Error}");
        }

        // A jacket's camera slot (in its inspect window) pictures the action camera; the game has no picture for it.
        private static void CameraSlotPicture(ModSlotView __instance, Slot slot)
        {
            if (slot?.ID != DogGear.CameraSlot || __instance.SlotBackImage == null) return;
            Sprite sprite = Silhouette("camera_action", 56f);
            if (sprite != null) __instance.SetSlotBackImage(sprite);
        }

        // The dog's jackets are rigs to the game (so plates go in them as they do in yours), but your own rig slot won't
        // take one: they're cut for a dog.
        private static bool NotForYou(Slot __instance, Item item, ref bool __result)
        {
            if (!DogGear.IsJacket(item) || __instance.ID != "TacticalVest") return true;
            __result = false;
            return false;
        }

        // Swaps the quest-container test in ItemManipulator.Add for one that leaves the dog's gear out of it.
        private static IEnumerable<HarmonyLib.CodeInstruction> QuestRule(IEnumerable<HarmonyLib.CodeInstruction> code)
        {
            int swapped = 0;
            foreach (var instruction in code)
            {
                if (instruction.Calls(ChildOf)) { instruction.operand = AccessTools.Method(typeof(DogGearPatches), nameof(InQuestContainer)); swapped++; }
                yield return instruction;
            }
            if (swapped == 0) Plugin.Log.LogWarning("Dog gear: the quest-container check wasn't found; items may not go into the dog's jacket.");
        }

        public static bool InQuestContainer(ItemAddress to, Item container, bool notMergedWithThisItem)
            => to.IsChildOf(container, notMergedWithThisItem) && !DogGear.Inside(to);

        private static bool Icon(Item item, ref ItemIcon __result)
        {
            Sprite sprite = SpriteFor(item);
            if (sprite == null) return true;
            __result = new ItemIcon(item.TemplateId.GetHashCode()) { Sprite = sprite };
            return false;
        }

        private static bool IconAsync(Item item, ref Task<Sprite> __result)
        {
            Sprite sprite = SpriteFor(item);
            if (sprite == null) return true;
            __result = Task.FromResult(sprite);
            return false;
        }

        // A gear picture to stand for an empty slot: about 110 px wide in the DOG tab, as the game's own slot pictures are, smaller in an inspect window.
        private static readonly Dictionary<string, Sprite> silhouettes = new Dictionary<string, Sprite>();
        internal static Sprite Silhouette(string look, float size = 110f)
        {
            string key = look + "@" + size;
            if (silhouettes.TryGetValue(key, out Sprite cached) && cached != null) return cached;
            string path = Path.Combine(Plugin.AssetFolder, "icon_" + look + ".png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Dog slot " + look };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) return null;
            // A flat grey shape, as the game's own empty slots show (in colour it looked like gear already in the slot).
            Color32[] pixels = texture.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32(205, 208, 205, (byte)(pixels[i].a * .3f));
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            float width = Mathf.Min(size, size * texture.width / Mathf.Max(texture.height, 1));
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f * texture.width / width);
            return silhouettes[key] = sprite;
        }

        // The dog gear's own picture (for the dog's loadout on the deploying screen), or null for anything else.
        internal static Sprite IconFor(Item item) => SpriteFor(item);

        private static Sprite SpriteFor(Item item)
        {
            string look = DogGear.AnyLook(item);
            if (look == null) return null;
            // Items of different sizes can share a look (the 2x1 vet kit and the 1x1 vet supply case): one picture per size,
            // icon_<look>_<w>x<h>.png where there is one, else icon_<look>.png.
            var cells = item.CalculateCellSize();
            string key = $"{look}@{cells.X}x{cells.Y}";
            if (icons.TryGetValue(key, out Sprite cached) && cached != null) return cached;
            string path = Path.Combine(Plugin.AssetFolder, $"icon_{look}_{cells.X}x{cells.Y}.png");
            if (!File.Exists(path)) path = Path.Combine(Plugin.AssetFolder, "icon_" + look + ".png");
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { name = "Dog gear icon " + look };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), true)) return null;
            texture.wrapMode = TextureWrapMode.Clamp;
            // Item views size icons natively: each picture is drawn at twice its item's cell size (63 px a cell), so it
            // counts double pixels per unit to land exactly on the cells and stay sharp.
            Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f), 100f * texture.width / (cells.X * 63 + 1));
            sprite.name = texture.name;
            icons[key] = sprite;
            return sprite;
        }
    }
}
