using System.Collections.Generic;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;
using System;
using EFT.InventoryLogic;
using EFT.UI.DragAndDrop;
using EFT.UI.WeaponModding;
using EFT.UI;
using EFT;
using HarmonyLib;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The dog's gear, registered by the server mod: a jacket (a container: a chest rig or one of four vests), head gear
    /// (goggles) and a clip-on action camera, worn in the slots of a hidden "dog" item that lives in
    /// the profile's quest stash. The DOG tab shows those slots like the gear screen shows yours. All of it goes into PMC
    /// raids with the dog: the jacket rides in the raid container (DogBag); the head gear and camera stay in their slots
    /// (the dog wears their looks, taken just before the raid) and the server removes them if the dog or you die.
    /// </summary>
    internal static class DogGear
    {
        // Each jacket's look (its gear_<look> model on the dog and icon_<look> icon), shared with the server mod (its gear list
        // there). The chest rig keeps the old saddlebag's id; the two jackets taken out in 0.20 look like what replaced them.
        internal static readonly Dictionary<string, string> Jackets = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d0d061"] = "harness_chestrig",
            ["6a5d0c0c4fb9a3c5e1d0d073"] = "harness_pocket",
            ["6a5d0c0c4fb9a3c5e1d0d071"] = "harness_tactical",
            ["6a5d0c0c4fb9a3c5e1d60020"] = "harness_assault",   // The Last Handler's reward
            ["6a5d0c0c4fb9a3c5e1d60021"] = "harness_camera",   // camera vest (has its own camera)
            ["6a5d0c0c4fb9a3c5e1d0d074"] = "harness_pocket",  // old basic jacket (becomes the pocket vest at game start)
            ["6a5d0c0c4fb9a3c5e1d0d072"] = "harness_assault",   // old heavy armour (becomes the assault vest)
        };
        // Head gear and the clip-on camera, by look (gear_<look> model, icon_<look> icon), as the server mod's DogGear.Kits.
        internal static readonly Dictionary<string, string> Kits = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d60022"] = "head_goggles_tan",
            ["6a5d0c0c4fb9a3c5e1d60023"] = "head_goggles_black",
            ["6a5d0c0c4fb9a3c5e1d60024"] = "head_headset",   // taken out in 0.29.1 (the server makes any into action cameras)
            ["6a5d0c0c4fb9a3c5e1d60025"] = "camera_action",
        };
        // The collars, by style (gear_collar_<style> on the dog, collar_<style> as an item), as the server mod's DogGear.Collars.
        internal static readonly Dictionary<string, string> Collars = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d60017"] = "training",
            ["6a5d0c0c4fb9a3c5e1d60018"] = "black",
            ["6a5d0c0c4fb9a3c5e1d60016"] = "buckle",   // K9 tactical collar
        };
        // The dog's supplies and the K9 story items made from their own models (tools/convert_dog_items.py:
        // gear_item_<key> model, icon_item_<key> icon), by the item they stand for. Shown only as items, never on the dog.
        internal static readonly Dictionary<string, string> Supplies = new Dictionary<string, string>
        {
            ["6a5d0c0c4fb9a3c5e1d0d062"] = "item_can",      // Can of dog food
            ["6a5d0c0c4fb9a3c5e1d60012"] = "item_can",      // K9 premium dog food
            ["6a5d0c0c4fb9a3c5e1d0d064"] = "item_meat",     // Pack of dog meat
            ["6a5d0c0c4fb9a3c5e1d60015"] = "item_duffel",   // K9 training kit
            ["6a5d0c0c4fb9a3c5e1d60010"] = "item_vetkit",   // K9 veterinary kit
            ["6a5d0c0c4fb9a3c5e1d60005"] = "item_vetkit",   // Veterinary supply case (quest item)
            ["6a5d0c0c4fb9a3c5e1d60001"] = "item_tag",      // K9 identification tag (quest item)
        };
        internal const string HolderTemplate = "6a5d0c0c4fb9a3c5e1d0d080", Slot = "DogJacket", HeadSlot = "DogHead", CameraSlot = "DogCamera",
            CollarSlot = "DogCollar";
        // Jackets that come with a camera of their own: the clip-on one isn't drawn on them.
        internal static readonly HashSet<string> BuiltInCamera = new HashSet<string> { "harness_camera" };
        // The kennel's lockers (hidden containers in the quest stash, opened from the DOG tab at kennel level 2 and 3).
        internal const string ArmourLocker = "6a5d0c0c4fb9a3c5e1d60030", PremiumLocker = "6a5d0c0c4fb9a3c5e1d60031";

        internal static bool IsJacket(Item item) => item != null && Jackets.ContainsKey(item.TemplateId.ToString());
        internal static bool IsHolder(Item item) => item != null && item.TemplateId.ToString() == HolderTemplate;
        internal static bool IsLocker(Item item) => item != null && (item.TemplateId.ToString() == ArmourLocker || item.TemplateId.ToString() == PremiumLocker);

        internal static CompoundItem Locker(Profile profile, string template)
            => profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(i => i.TemplateId.ToString() == template) as CompoundItem;
        internal static string Look(Item jacket) => jacket != null && Jackets.TryGetValue(jacket.TemplateId.ToString(), out string c) ? c : null;
        // The look of any of the dog's gear (jacket, head gear, camera, collar) or supplies: its model as an item and its stash icon.
        internal static string AnyLook(Item item)
        {
            if (item == null) return null;
            string id = item.TemplateId.ToString();
            return Look(item) ?? (Kits.TryGetValue(id, out string k) ? k : Collars.TryGetValue(id, out string c) ? "collar_" + c
                : Supplies.TryGetValue(id, out string s) ? s : null);
        }
        // The collar in the dog's collar slot (null = none; an older server mod has no slot).
        internal static string CollarStyle(Profile profile)
            => SlotOf(profile, CollarSlot)?.ContainedItem is Item c && Collars.TryGetValue(c.TemplateId.ToString(), out string style) ? style : null;
        // The collar the dog wears into this raid (taken just before it, as the head gear is).
        internal static string RaidCollar;
        // What the dog takes into this raid, item by item, as it was at raid start (null = nothing in that place): the
        // deploying screen's loadout window shows these, since by then the jacket is in the raid container and the raid's
        // copy of your profile has no quest stash (so no dog holder) to look in.
        internal static Item RaidJacket, RaidHeadItem, RaidCameraItem, RaidCollarItem;
        internal static bool RaidLoadoutTaken;

        // The hidden "dog" item (out of raid only: the game drops the quest stash from the profile for a raid).
        internal static CompoundItem Holder(Profile profile)
            => profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(IsHolder) as CompoundItem;

        internal static Slot JacketSlot(Profile profile) => SlotOf(profile, Slot);
        internal static Slot SlotOf(Profile profile, string name) => Holder(profile)?.Slots?.FirstOrDefault(s => s.ID == name);

        // What the dog wears on its head and its jacket's camera mount at home (null = nothing; the server mod adds those
        // slots, so an older one has neither).
        internal static string HeadLook(Profile profile) => AnyLook(SlotOf(profile, HeadSlot)?.ContainedItem);
        // The clip-on camera rides on the jacket (since 0.25); an old one may still be in the dog's own Camera slot.
        internal static string CameraLook(Profile profile) => AnyLook(CameraOn(Worn(profile)) ?? SlotOf(profile, CameraSlot)?.ContainedItem);
        internal static Item CameraOn(Item jacket) => (jacket as CompoundItem)?.Slots?.FirstOrDefault(s => s.ID == CameraSlot)?.ContainedItem;
        internal static Item CameraWorn(Profile profile) => CameraOn(Worn(profile)) ?? SlotOf(profile, CameraSlot)?.ContainedItem;

        // The plate a jacket has where a round comes in: front or back by the round's way, else the side it came from.
        internal static Item PlateFor(Item jacket, Vector3 travel, Transform dog)
        {
            var slots = (jacket as CompoundItem)?.Slots;
            if (slots == null || dog == null) return null;
            float along = Vector3.Dot(travel.normalized, dog.forward);
            string name = along < -.5f ? "Front_plate" : along > .5f ? "Back_plate"
                : Vector3.Dot(travel, dog.right) > 0 ? "Left_side_plate" : "Right_side_plate";
            return slots.FirstOrDefault(s => string.Equals(s.ID, name, StringComparison.OrdinalIgnoreCase))?.ContainedItem;
        }

        // The head gear (and an old-style camera) the dog has on, sent to the server at raid start: they're at risk in
        // the raid. A camera on the jacket goes and comes back with the jacket.
        internal static IEnumerable<Item> KitsWorn(Profile profile)
            => new[] { SlotOf(profile, HeadSlot)?.ContainedItem, SlotOf(profile, CameraSlot)?.ContainedItem }.Where(i => i != null);

        // The same, taken just before a raid (DogBag), when the quest stash is still there to look in.
        internal static string RaidHead, RaidCamera;

        // The jacket the dog is wearing at home.
        internal static CompoundItem Worn(Profile profile) => JacketSlot(profile)?.ContainedItem as CompoundItem;

        // True for any place inside the dog's gear: the holder, a jacket, a kennel locker, or anything in them.
        internal static bool Inside(ItemAddress address)
        {
            Item at = address?.Container?.ParentItem;
            for (int depth = 0; at != null && depth < 16; depth++)
            {
                if (IsHolder(at) || IsJacket(at) || IsLocker(at)) return true;
                at = at.Parent?.Container?.ParentItem;
            }
            return false;
        }
    }

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

    /// <summary>
    /// The dog's gear and supplies as themselves wherever the game shows an item in 3D: the inspect window, and lying on
    /// the ground. The items are copies of game items (an armoured rig, a flash storage module, a ring for the collars, a
    /// tin, dried meat, paracord, a Grizzly medkit, medical tools, a badge), so the game builds their models; once it has, those
    /// are hidden and our own model stands in, at its real size where the game's model was, so loot lies as it would.
    /// For gear that's the model as it was made (gear_&lt;look&gt;_item.mesh, tools/convert_item_models.py), not the copy
    /// stretched round the dog, which looks odd off it; the supplies are gear_item_&lt;key&gt; (tools/convert_dog_items.py). In an inspect window it is turned to face the camera. A
    /// jacket with the action camera on it shows the camera on top. The game reuses item models from a pool: one built for the
    /// dog's gear gets its own back when it's put away, or when the game builds one of its own items with it.
    /// </summary>
    internal static class GearVisuals
    {
        // The game items the dog's gear and supplies are copied from: their models may come back from the pool still dressed.
        private static readonly HashSet<string> Sources = new HashSet<string>
        {
            "628cd624459354321c4b7fa2", "5c05300686f7746dce784e5d",   // plate carrier (jackets), flash storage module (kits)
            "5d235a5986f77443f6329bc6",                               // skull ring (collars)
            "57347d7224597744596b4e72", "57347da92459774491567cf5",   // small and large tin of beef stew (dog food)
            "65815f0e647e3d7246384e14", "5c12688486f77426843c7d32",   // dried meat (dog meat), paracord (training kit)
            "590c657e86f77412b013051d", "619cc01e0a7c3a1a2731940c",   // Grizzly (vet kit), medical tools (vet supply case)
            "5bc9bdb8d4351e003562b8a1",                               // silver badge (K9 tag)
        };
        private static readonly Dictionary<string, (Mesh mesh, Material material)> models = new Dictionary<string, (Mesh, Material)>();

        internal static void Patch(Harmony harmony)
        {
            var create = AccessTools.Method(typeof(ObjectsFactory), nameof(ObjectsFactory.CreateItemAsync))
                         ?? throw new MissingMethodException(nameof(ObjectsFactory), nameof(ObjectsFactory.CreateItemAsync));
            harmony.Patch(create, postfix: new HarmonyMethod(typeof(GearVisuals), nameof(Created)));
            var position = AccessTools.Method(typeof(WeaponPreview), nameof(WeaponPreview.PositionGameObject))
                           ?? throw new MissingMethodException(nameof(WeaponPreview), nameof(WeaponPreview.PositionGameObject));
            harmony.Patch(position, postfix: new HarmonyMethod(typeof(GearVisuals), nameof(Previewed)));
        }

        private static void Created(Item item, ref Task<GameObject> __result)
        {
            if (item == null || __result == null) return;
            string look = DogGear.AnyLook(item);
            // The jacket's durability as the dog has worn it, for the inspect window and tooltips.
            if (DogGear.IsJacket(item)) DogArmour.ShowWear(item);
            if (look != null) __result = Dress(__result, item, look);
            else if (Sources.Contains(item.TemplateId.ToString())) __result = Undress(__result);
        }

        // In an inspect window: turn ours to the camera once the game has put the item under its preview pivot.
        private static void Previewed(GameObject itemGameObject)
        {
            var visual = itemGameObject != null ? itemGameObject.GetComponent<DogGearVisual>() : null;
            if (visual != null) visual.FacePreview(itemGameObject.transform.parent, itemGameObject.GetComponent<PreviewPivot>());
        }

        private static async Task<GameObject> Dress(Task<GameObject> made, Item item, string look)
        {
            GameObject model = await made;
            try
            {
                if (model == null) return model;
                var visual = model.GetComponent<DogGearVisual>() ?? model.AddComponent<DogGearVisual>();
                var parts = new List<string> { Off(look) };
                // The action camera on a jacket is drawn with it (the game's rig has no place for it).
                if (DogGear.IsJacket(item) && DogGear.CameraOn(item) != null) parts.Add(Off("camera_action"));
                visual.Show(parts, look);
            }
            catch (Exception e) { Plugin.Log.LogWarning($"The {look} model couldn't be shown: {e.Message}"); }
            return model;
        }

        private static async Task<GameObject> Undress(Task<GameObject> made)
        {
            GameObject model = await made;
            if (model != null) model.GetComponent<DogGearVisual>()?.Restore();
            return model;
        }

        // The model of a piece of gear off the dog, or (if there isn't one) as worn.
        private static string Off(string look) => File.Exists(Path.Combine(Plugin.AssetFolder, $"gear_{look}_item.mesh")) ? look + "_item" : look;

        /// <summary>A gear model, read once: its mesh (as worn, or off the dog for an _item one), and its texture.</summary>
        internal static (Mesh mesh, Material material) Model(string part, string texture)
        {
            if (models.TryGetValue(part, out var cached) && cached.mesh != null) return cached;
            string meshPath = Path.Combine(Plugin.AssetFolder, "gear_" + part + ".mesh"), texturePath = Path.Combine(Plugin.AssetFolder, "gear_" + texture + ".jpg");
            if (!File.Exists(meshPath)) return (null, null);
            var mesh = new Mesh { name = "K9 gear " + part, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            using (var r = new BinaryReader(File.OpenRead(meshPath)))
            {
                if (r.ReadInt32() != 0x31544b4a) throw new InvalidDataException("Not a gear mesh: " + part);
                r.ReadBytes(r.ReadInt32());
                int count = r.ReadInt32();
                var positions = new Vector3[count]; var normals = new Vector3[count]; var uvs = new Vector2[count];
                for (int i = 0; i < count; i++)
                {
                    positions[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    normals[i] = new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
                    uvs[i] = new Vector2(r.ReadSingle(), r.ReadSingle());
                    r.ReadBytes(32);   // bone weights: not needed off the dog
                }
                int blocks = r.ReadInt32();
                var triangles = new List<int>();
                for (int b = 0; b < blocks; b++) { int n = r.ReadInt32(); for (int i = 0; i < n; i++) triangles.Add(r.ReadInt32()); }
                mesh.vertices = positions; mesh.normals = normals; mesh.uv = uvs; mesh.SetTriangles(triangles, 0);
                mesh.RecalculateBounds();
            }
            var material = new Material(Shader.Find("Standard") ?? Shader.Find("Legacy Shaders/Diffuse")) { name = "K9 gear " + texture };
            if (material.HasProperty("_Glossiness")) material.SetFloat("_Glossiness", .3f);
            if (File.Exists(texturePath))
            {
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, true);
                if (ImageConversion.LoadImage(tex, File.ReadAllBytes(texturePath), false)) { tex.Compress(true); tex.Apply(true, true); material.mainTexture = tex; }
            }
            // The supplies' normal map (the relief their full-detail model had) and metal/smoothness map, as the kennel
            // props have them (PropModel): gear_<texture>_n.png and _ms.png, where the converter wrote them.
            string stem = Path.Combine(Plugin.AssetFolder, "gear_" + texture);
            Texture2D normal = DataTexture(stem + "_n.png", false), metalSmooth = DataTexture(stem + "_ms.png", true);
            if (normal != null && material.HasProperty("_BumpMap"))
            {
                mesh.RecalculateTangents();
                material.SetTexture("_BumpMap", normal); material.SetFloat("_BumpScale", 1f); material.EnableKeyword("_NORMALMAP");
            }
            if (metalSmooth != null && material.HasProperty("_MetallicGlossMap"))
            {
                material.SetTexture("_MetallicGlossMap", metalSmooth); material.EnableKeyword("_METALLICGLOSSMAP");
                if (material.HasProperty("_GlossMapScale")) material.SetFloat("_GlossMapScale", 1f);
            }
            return models[part] = (mesh, material);
        }

        // A map holding data rather than colour (normals, metal/smoothness): read as it is, without the colour correction,
        // and kept only on the graphics card. The normal map stays uncompressed (block compression shows on its relief).
        private static Texture2D DataTexture(string path, bool compress)
        {
            if (!File.Exists(path)) return null;
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, true) { name = Path.GetFileName(path) };
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)) return null;
            texture.anisoLevel = 4;
            if (compress) texture.Compress(true);
            texture.Apply(true, true);
            return texture;
        }
    }

    /// <summary>
    /// The gear's own model over a game item model (whose renderers it hides), until it's put away. It is drawn at its
    /// real size (the meshes are in metres, as worn), centred where the game's model was. On the ground it lies on its
    /// underside like the item it stands in for; in an inspect window it is turned to face the camera three-quarters on
    /// (the game turns its own model by that item's icon angle, which means nothing for ours).
    /// </summary>
    internal sealed class DogGearVisual : MonoBehaviour
    {
        // How the gear faces the inspect camera (which looks along +z): jackets side-on with the head end towards you,
        // head gear, the camera and the supplies nearly front-on; all tipped a little so the top shows.
        private static readonly Quaternion JacketView = Quaternion.Euler(-20, 0, 0) * Quaternion.Euler(0, 127, 0);
        private static readonly Quaternion SmallView = Quaternion.Euler(-15, 0, 0) * Quaternion.Euler(0, 155, 0);
        // A collar lies flat (its ring level): seen from well above, so the ring shows rather than its edge.
        private static readonly Quaternion CollarView = Quaternion.Euler(-60, 0, 0) * Quaternion.Euler(0, 155, 0);

        private readonly List<Renderer> hidden = new List<Renderer>();
        private readonly List<GameObject> shown = new List<GameObject>();
        private readonly List<Vector3> offsets = new List<Vector3>();   // each part's place on the first (the camera's on a jacket)
        private Bounds ours;
        private Vector3 centre;
        private bool jacket, collar;

        internal void Show(List<string> parts, string look)
        {
            Restore();
            jacket = look.StartsWith("harness_");
            collar = look.StartsWith("collar_");
            // Where the game's model is, in the item's own space (from the meshes, so it holds whether or not the game
            // has shown or hidden them yet). An item that's worn as well as dropped (a rig) has two models: the dropped one.
            DressItem dress = GetComponent<DressItem>();
            Transform model = dress != null && dress.LootPrefab != null ? dress.LootPrefab.transform : transform;
            Bounds? theirs = null;
            foreach (Renderer r in GetComponentsInChildren<Renderer>(true))
            {
                if (!(r is MeshRenderer || r is SkinnedMeshRenderer)) continue;   // meshes only, not effects
                if (r.enabled) { r.enabled = false; hidden.Add(r); }
                if (!r.transform.IsChildOf(model) || !(LocalBounds(r) is Bounds found)) continue;
                if (theirs is Bounds t) { t.Encapsulate(found); theirs = t; } else theirs = found;
            }
            var (first, _) = GearVisuals.Model(parts[0], look);
            if (first == null) { Restore(); return; }
            ours = first.bounds;
            centre = theirs?.center ?? Vector3.zero;
            // Lying as the game's model lies: our up along its thinnest side (a rig lies flat on its back or front).
            Quaternion lying = Quaternion.identity;
            if (theirs is Bounds them)
            {
                Vector3 size = them.size;
                Vector3 thin = size.x <= size.y && size.x <= size.z ? Vector3.right : size.y <= size.z ? Vector3.up : Vector3.forward;
                lying = Quaternion.FromToRotation(Vector3.up, thin);
            }
            foreach (string part in parts)
            {
                bool camera = part.StartsWith("camera_action");
                var (mesh, material) = GearVisuals.Model(part, camera ? "camera_action" : look);
                if (mesh == null) continue;
                var go = new GameObject("K9 gear model " + part);
                go.layer = gameObject.layer;
                go.transform.SetParent(transform, false);
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterial = material;
                // The camera off the dog stands on top of the jacket, a little forward of the middle, lens to the front.
                offsets.Add(camera && part.EndsWith("_item")
                    ? new Vector3(0, ours.max.y + mesh.bounds.extents.y, ours.center.z + ours.extents.z * .3f) - mesh.bounds.center
                    : Vector3.zero);
                shown.Add(go);
            }
            Place(lying, Vector3.one, centre);
        }

        /// <summary>
        /// In an inspect window the game has put the item under its preview pivot (turned by the item's icon angle,
        /// scaled, and shifted so the pivot point is in the middle); ours faces the camera from there, at its real size.
        /// </summary>
        internal void FacePreview(Transform pivot, PreviewPivot settings)
        {
            if (shown.Count == 0 || pivot == null) return;
            Vector3 scale = settings != null ? settings.scale : Vector3.one;
            var undo = new Vector3(1 / Mathf.Max(scale.x, 1e-4f), 1 / Mathf.Max(scale.y, 1e-4f), 1 / Mathf.Max(scale.z, 1e-4f));
            Quaternion turn = Quaternion.Inverse(pivot.localRotation) * (jacket ? JacketView : collar ? CollarView : SmallView);
            Place(turn, undo, settings != null ? settings.pivotPosition : centre);
        }

        // Ours turned and scaled as given, its middle on the given point of the item's own space.
        private void Place(Quaternion rotation, Vector3 scale, Vector3 at)
        {
            for (int i = 0; i < shown.Count; i++)
            {
                GameObject go = shown[i];
                if (go == null) continue;
                go.transform.localRotation = rotation;
                go.transform.localScale = scale;
                go.transform.localPosition = at - rotation * Vector3.Scale(ours.center - offsets[i], scale);
            }
        }

        // A renderer's mesh bounds in the item's own space.
        private Bounds? LocalBounds(Renderer r)
        {
            Bounds mesh;
            if (r is SkinnedMeshRenderer skinned) mesh = skinned.localBounds;
            else if (r.GetComponent<MeshFilter>() is MeshFilter filter && filter.sharedMesh != null) mesh = filter.sharedMesh.bounds;
            else return null;
            if (mesh.extents == Vector3.zero) return null;
            Matrix4x4 toItem = transform.worldToLocalMatrix * r.transform.localToWorldMatrix;
            Vector3 min = Vector3.positiveInfinity, max = Vector3.negativeInfinity;
            for (int i = 0; i < 8; i++)
            {
                var corner = mesh.center + Vector3.Scale(mesh.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
                Vector3 p = toItem.MultiplyPoint3x4(corner);
                min = Vector3.Min(min, p); max = Vector3.Max(max, p);
            }
            return new Bounds((min + max) / 2, max - min);
        }

        internal void Restore()
        {
            foreach (Renderer r in hidden) if (r != null) r.enabled = true;
            hidden.Clear();
            foreach (GameObject go in shown) if (go != null) Destroy(go);
            shown.Clear(); offsets.Clear();
        }

        // Loose loot in a building is registered with the room's culling, which switches the game model's renderers back on
        // whenever the room comes into view (LootItem.RegisterInCullingObject): they'd show through ours, the dog meat as
        // a pack of Tarker dried meat. They're kept off.
        private void LateUpdate()
        {
            if (shown.Count == 0) return;
            for (int i = 0; i < hidden.Count; i++)
                if (hidden[i] != null && hidden[i].enabled) hidden[i].enabled = false;
        }

        // Put away (back into the game's pool): the game's model as it was.
        private void OnDisable() => Restore();
    }

    /// <summary>
    /// The dog's jacket on raid day. At home the dog wears it in the DOG tab (see DogGear); when a PMC raid starts it
    /// goes with the dog: the client tells the server first, then moves the jacket into the profile's raid quest-items
    /// container, which the game already saves when you extract and wipes when you die. After the raid the server puts it
    /// back on the dog. If the dog dies, whatever is still in its jacket at the end of the raid is lost, unless you take its
    /// gear off it (the jacket into your own gear): then it goes back on the dog if you get out.
    /// </summary>
    internal static class DogBag
    {
        private const string StartRoute = "/k9companion/bag/raid-start", DogDiedRoute = "/k9companion/bag/dog-died", DogRevivedRoute = "/k9companion/bag/dog-revived",
            GearTakenRoute = "/k9companion/bag/gear-taken";

        /// <summary>This raid, you've taken the killed dog's gear (Companion.TakeGear).</summary>
        internal static bool GearTaken { get; private set; }

        internal static void Patch(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate))
                         ?? throw new MissingMethodException(nameof(TarkovApplication), nameof(TarkovApplication.LocalGameCreate));
            // First: Fika replaces raid creation with its own (its prefix skips the original, and any prefix after it).
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(DogBag), nameof(BeforeRaid)) { priority = Priority.First });
        }

        // Runs just before the game drops the stash from the profile for the raid. Nothing moves unless the server
        // has recorded that the bag is going into this raid, so the two sides can't disagree.
        private static void BeforeRaid(TarkovApplication __instance)
        {
            try
            {
                Profile profile = __instance.Session?.Profile;
                RaidSettings raid = __instance.CurrentRaidSettings;
                if (profile?.Inventory == null || raid == null) return;
                bool pmcRaid = raid.Side == ESideType.Pmc;
                GearTaken = false;
                // Whether the dog comes along at all: you need a dog, and it must be fit enough (K9.NotDeployable).
                K9.Refresh();
                string stays = K9.NotDeployable(!pmcRaid);
                bool deployed = Plugin.Active.Value && stays == null;
                Deployed = deployed;
                if (Plugin.Active.Value && stays != null && K9.Owned) Plugin.Log.LogInfo("The dog stays home this raid: " + stays + ".");
                K9.RaidStart(deployed);
                // Head gear and the camera go with the dog into PMC raids (lost if it or you die). They stay in its slots
                // at home; the dog wears what's there now.
                bool kitsGo = pmcRaid && deployed;
                DogGear.RaidHead = kitsGo ? DogGear.HeadLook(profile) : null;
                DogGear.RaidCamera = kitsGo ? DogGear.CameraLook(profile) : null;
                // The collar goes into any raid with the dog and is never lost there (it stays in its slot at home).
                DogGear.RaidCollar = deployed ? DogGear.CollarStyle(profile) : null;
                DogGear.RaidJacket = pmcRaid && deployed ? DogGear.Worn(profile) : null;
                DogGear.RaidHeadItem = kitsGo ? DogGear.SlotOf(profile, DogGear.HeadSlot)?.ContainedItem : null;
                DogGear.RaidCameraItem = kitsGo ? DogGear.CameraWorn(profile) : null;
                DogGear.RaidCollarItem = deployed ? DogGear.SlotOf(profile, DogGear.CollarSlot)?.ContainedItem : null;
                DogGear.RaidLoadoutTaken = true;
                // The dog gets ready along with your character, while the loading screen is up.
                if (deployed) DogPreload.Begin(DogGear.Look(DogGear.RaidJacket), DogGear.RaidHead, DogGear.RaidCamera != null, DogGear.RaidCollar);
                else DogPreload.Release();
                string kits = kitsGo ? string.Join(",", DogGear.KitsWorn(profile).Select(i => "\"" + i.Id + "\"")) : "";
                // The jacket the dog wears (its slot in the DOG tab); none when the slot is empty.
                Item bag = pmcRaid && deployed ? DogGear.Worn(profile) : null;
                // Always report, so a record left by an earlier raid can never be applied to this one.
                string reply = RequestHandler.PostJson(StartRoute, "{\"bagId\":" + (bag != null ? "\"" + bag.Id + "\"" : "null") + ",\"kitIds\":[" + kits + "]}");
                if (bag == null) return;
                if (reply == null || !reply.Contains("\"ok\":true")) { Plugin.Log.LogWarning("Server mod didn't accept the dog's jacket; it stays home. Reply: " + reply); return; }
                if (MoveToRaidContainer(profile, bag)) Plugin.Log.LogInfo("Dog's jacket goes into the raid with " + Contents(bag) + " item(s).");
                else
                {
                    // The server expects the bag in the raid; tell it the bag stayed home.
                    RequestHandler.PostJson(StartRoute, "{\"bagId\":null,\"kitIds\":[" + kits + "]}");
                    Plugin.Log.LogWarning("No room to carry the dog's jacket into the raid; it stays home.");
                }
            }
            catch (Exception e) { Plugin.Log.LogError("Dog's jacket couldn't be prepared for the raid (it stays home): " + e); }
        }

        /// <summary>Decided just before each raid (null until then): whether the dog goes along.</summary>
        internal static bool? Deployed;

        internal static CompoundItem FindInRaid(Player player)
            => player?.Profile?.Inventory?.QuestRaidItems?.GetAllItems().FirstOrDefault(DogGear.IsJacket) as CompoundItem;

        private static int Contents(Item bag) => bag.GetAllItems().Count() - 1;

        private static bool MoveToRaidContainer(Profile profile, Item bag)
        {
            // From the dog's slot (or, for an old setup, the stash grid) into the raid container.
            Grid target = profile.Inventory.QuestRaidItems?.Grids?.FirstOrDefault();
            ItemAddress from = bag.CurrentAddress;
            if (target == null || from == null) return false;
            GridItemAddress spot = target.FindLocationForItem(bag);
            if (spot == null) return false;
            if (from.RemoveWithoutRestrictions(bag).Failed) return false;
            if (target.Add(bag, spot.LocationInGrid, false).Succeeded) return true;
            from.AddWithoutRestrictions(bag);   // put it back exactly where it was
            return false;
        }

        // Opens the jacket the way the game opens a backpack on the ground: your inventory with its grid beside it.
        internal static string Open(Player player)
        {
            CompoundItem bag = FindInRaid(player);
            if (bag == null) return "The dog isn't wearing a jacket this raid.";
            GamePlayerOwner owner = player.GetComponent<GamePlayerOwner>();
            if (owner == null) return "Can't open the jacket right now.";
            // As a plain container (like a backpack on the ground): its grid shows beside your gear and items move
            // with your own inventory controller. The "fake container" view would re-home the jacket into a throwaway
            // stash owned by another controller, which broke transfers and left the player "busy".
            owner.ShowInventoryScreenLoot(bag, () => { }, false);
            return null;
        }

        internal static void DogDied()
        {
            try { RequestHandler.PostJson(DogDiedRoute, "{}"); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server the dog died: " + e.Message); }
        }

        // You took the killed dog's gear: the server keeps it if you get out alive. In the background (the dog's death was
        // reported, and waited for, when it died).
        internal static void TookGear()
        {
            GearTaken = true;
            try
            {
                RequestHandler.PostJsonAsync(GearTakenRoute, "{}").ContinueWith(t =>
                {
                    if (t.IsFaulted || t.Result == null || !t.Result.Contains("\"ok\":true"))
                        Plugin.Log.LogWarning("The server mod didn't record that you took the dog's gear (an older server mod?): " + (t.Exception?.GetBaseException().Message ?? t.Result));
                });
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server you took the dog's gear: " + e.Message); }
        }

        // A downed dog was reported as dead (so a raid ending, or a crash, counts as just that), and has been brought back.
        internal static void DogRevived()
        {
            try { RequestHandler.PostJson(DogRevivedRoute, "{}"); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't tell the server the dog was revived: " + e.Message); }
        }
    }

    /// <summary>
    /// The compressed form of the dog's textures, kept on disk. Compressing a 2048 x 2048 picture takes a large part of a
    /// second on the main thread; with a dozen of them that was the freeze when a dog appeared. It's done once, the first
    /// time a picture is needed, and afterwards the result only has to be read back. A picture that changes (an update
    /// of the mod) is done again.
    /// </summary>
    internal static class TextureCache
    {
        private static string Folder => Path.Combine(Plugin.AssetFolder, "cache");

        internal static Texture2D Load(string path)
        {
            string name = Path.GetFileName(path);
            var source = new FileInfo(path);
            string stamp = (source.LastWriteTimeUtc.Ticks ^ source.Length).ToString("x");
            Texture2D texture = ReadBack(name, stamp);
            if (texture != null) return texture;
            texture = new Texture2D(2, 2, TextureFormat.RGBA32, true);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false))
            {
                UnityEngine.Object.Destroy(texture);
                throw new InvalidDataException("Cannot decode " + name);
            }
            texture.Compress(true);
            Keep(name, stamp, texture);
            // On the graphics card (about a sixth of the memory), with no copy kept in RAM.
            texture.Apply(false, true);
            return texture;
        }

        // <picture>.<stamp>.<width>.<height>.<format>.tex
        private static Texture2D ReadBack(string name, string stamp)
        {
            try
            {
                if (!Directory.Exists(Folder)) return null;
                string file = Directory.GetFiles(Folder, name + "." + stamp + ".*.tex").FirstOrDefault();
                if (file == null) return null;
                string[] parts = Path.GetFileName(file).Substring(name.Length + stamp.Length + 2).Split('.');
                var texture = new Texture2D(int.Parse(parts[0]), int.Parse(parts[1]), (TextureFormat)int.Parse(parts[2]), true);
                texture.LoadRawTextureData(File.ReadAllBytes(file));
                texture.Apply(false, true);
                return texture;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("A kept texture of " + name + " couldn't be read; it's made again: " + e.Message);
                return null;
            }
        }

        private static void Keep(string name, string stamp, Texture2D texture)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                foreach (string old in Directory.GetFiles(Folder, name + ".*.tex")) File.Delete(old);
                File.WriteAllBytes(Path.Combine(Folder, $"{name}.{stamp}.{texture.width}.{texture.height}.{(int)texture.format}.tex"), texture.GetRawTextureData());
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't keep the texture of " + name + ": " + e.Message); }
        }
    }

    internal sealed class PreparedDog
    {
        internal GameObject Dog;
        internal DogModel Model;
        internal CompanionDecor Decor;

        internal void Dispose()
        {
            Decor?.Dispose(); Model?.Dispose();
            if (Dog != null) UnityEngine.Object.Destroy(Dog);
        }
    }

    /// <summary>
    /// The raid's dog is made ready during the loading screen, as your character is, a piece at a time, so that when the raid
    /// starts it only has to be put down beside you. Loaded when it spawned, it froze the game just as the raid began.
    /// </summary>
    internal static class DogPreload
    {
        private static PreparedDog ready;
        private static Coroutine running;

        // Still being made: the raid's dog waits for it rather than loading a second one.
        internal static bool Busy => running != null;

        internal static void Begin(string vest, string head, bool camera, string collar)
        {
            Release();
            running = Plugin.Runner.StartCoroutine(Prepare(vest, head, camera, collar));
        }

        internal static PreparedDog Take()
        {
            PreparedDog dog = ready;
            ready = null;
            return dog;
        }

        internal static void Release()
        {
            if (running != null) Plugin.Runner.StopCoroutine(running);
            running = null;
            ready?.Dispose();
            ready = null;
        }

        private static IEnumerator Prepare(string vest, string head, bool camera, string collar)
        {
            var dog = new PreparedDog();
            var steps = new Action[]
            {
                () =>
                {
                    dog.Dog = new GameObject("K9 Companion dog");
                    dog.Dog.SetActive(false);
                    UnityEngine.Object.DontDestroyOnLoad(dog.Dog);
                    dog.Model = DogModel.Load(Plugin.AssetFolder, dog.Dog.transform, Plugin.Scale.Value);
                },
                () => dog.Decor = new CompanionDecor(dog.Dog.transform, dog.Model),
                () => dog.Decor.SetVest(vest),
                () => dog.Decor.SetHead(head),
                () => dog.Decor.SetCamera(camera),
                () => dog.Decor.SetCosmetics(collar)
                // Not the sounds: the game restarts its audio (AudioSettings.Reset) while a raid loads, after this has run, and
                // that silences clips made before it. They take no time to make when the dog comes out.
            };
            while (!DogModel.ClipsReady) yield return null;
            foreach (Action step in steps)
            {
                try { step(); }
                catch (Exception e)
                {
                    Plugin.Log.LogError("The dog couldn't be made ready for the raid (it's tried again when the raid starts): " + e);
                    dog.Dispose();
                    running = null;
                    yield break;
                }
                yield return null;
            }
            ready = dog;
            running = null;
            Plugin.Log.LogInfo("The dog is ready for the raid.");
        }
    }
}
