using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Common.Models.Logging;
using SPTarkov.Server.Core.Utils.Cloners;

namespace K9Companion.Server;

/// <summary>
/// The dog's gear. Its jacket is a container (it carries what the old saddlebag did): one of five harnesses (a chest rig
/// and four vests); Dr. Yang sells the vests only ready plated (also on the flea market, through his offers). Goggles go on its head, and a clip-on
/// action camera on its jacket. What the dog is wearing sits in the slots of a hidden "dog" item kept in the profile's
/// quest stash, which the DOG tab shows like the gear screen shows your own slots.
/// </summary>
public static class DogGear
{
    // The black jacket keeps the old saddlebag's id, so saddlebags already in a stash become black harnesses.
    public static readonly MongoId Black = new("6a5d0c0c4fb9a3c5e1d0d061");

    /// <summary>One jacket: a tier of protection with its own look (the client's gear_&lt;look&gt; model and icon_&lt;look&gt;
    /// icon). Armour class 0 = none; the client applies the armour and the speed penalty to the dog (DogArmour.cs there,
    /// same table). Plates fitted in it take a round first where they cover; its own armour is the rest.
    /// Plates: how many armour plates it takes (0, 2 front and back, 4 with the sides), the game's own plates.</summary>
    public sealed record Jacket(MongoId Id, string Look, string Kind, string Short, int ArmourClass, int Width, int Height,
                                int GridWidth, int GridHeight, double Weight, int Price, int Durability = 0, int Speed = 100, int Plates = 0);

    private const int Base = 12000;
    public static readonly Jacket[] Jackets =
    {
        // Durability and speed (%) are the client's DogArmour table; they're repeated here for the item descriptions.
        // Each keeps the id (and so the quests, offers and saved gear) of the jacket it replaced in 0.20.
        // Sized by how big each is: the stash space it takes, and the load it carries, grow with it.
        // Named "K9 <Kind> (<colour>)", short "K9 <Short>": one pattern for all the dog's gear (head gear and camera too).
        new(Black, "harness_chestrig", "Chest Rig", "Rig", 0, 2, 2, 4, 4, 0.8, Base),
        new(new("6a5d0c0c4fb9a3c5e1d0d073"), "harness_pocket", "Pocket Vest", "Pocket", 3, 2, 3, 5, 4, 2.6, Base * 4, 40, 95, 2),
        new(new("6a5d0c0c4fb9a3c5e1d0d071"), "harness_tactical", "Tactical Vest", "Tactical", 4, 3, 3, 5, 5, 4.2, Base * 7, 50, 91, 2),
        // The Last Handler's reward: class 5, light for it, tough, and the biggest of them, with the biggest load.
        new(K9Ids.EliteHarness, "harness_assault", "Assault Vest", "Assault", 5, 3, 4, 6, 6, 3.8, Base * 18, 75, 93, 4),
        // Full length, with its own camera on top (so the clip-on camera isn't needed with it): dearer than a tactical vest
        // and an action camera bought apart.
        new(K9Ids.CameraVest, "harness_camera", "Camera Vest", "CamVest", 4, 3, 4, 6, 5, 4.6, Base * 15, 55, 92, 2),
    };
    private static readonly Dictionary<string, string> LookNames = new()
    { ["harness_chestrig"] = "Black", ["harness_pocket"] = "Black", ["harness_tactical"] = "Olive", ["harness_assault"] = "Camo", ["harness_camera"] = "Coyote" };
    public static string Name(Jacket j) => $"K9 {j.Kind} ({LookNames[j.Look]})";

    /// <summary>
    /// Jackets taken out in 0.20 (the hi-vis basic jacket and the coyote heavy armour). Their items still exist, so a
    /// profile that has one loads, but nothing sells or rewards them: any in a profile become the jacket next to them
    /// (same id, so whatever's inside stays) when the game starts (BagKeeper).
    /// </summary>
    public static readonly Jacket[] Retired =
    {
        new(new("6a5d0c0c4fb9a3c5e1d0d074"), "harness_pocket", "basic protective jacket (old)", "Old jacket", 2, 2, 2, 4, 4, 1.4, Base * 2, 30, 98),
        new(new("6a5d0c0c4fb9a3c5e1d0d072"), "harness_assault", "heavy armour (old)", "Old armour", 5, 3, 4, 6, 5, 6.5, Base * 12, 60, 86),
    };
    public static readonly Dictionary<MongoId, MongoId> ReplacedBy = new()
    {
        [new("6a5d0c0c4fb9a3c5e1d0d074")] = new("6a5d0c0c4fb9a3c5e1d0d073"),
        [new("6a5d0c0c4fb9a3c5e1d0d072")] = K9Ids.EliteHarness,
    };
    public static readonly MongoId Holder = new("6a5d0c0c4fb9a3c5e1d0d080");
    public static readonly MongoId HolderSlotId = new("6a5d0c0c4fb9a3c5e1d0d081"), HeadSlotId = new("6a5d0c0c4fb9a3c5e1d0d082"),
        CameraSlotId = new("6a5d0c0c4fb9a3c5e1d0d083"), CollarSlotId = new("6a5d0c0c4fb9a3c5e1d0d084");
    public const string Slot = "DogJacket", HeadSlot = "DogHead", CameraSlot = "DogCamera", CollarSlot = "DogCollar";

    /// <summary>
    /// The collars, by the style the client draws (its gear_collar_&lt;style&gt; model). Items since 0.31 (before, the DOG
    /// tab picked from the ones the quests had earned): the dog wears the one in its collar slot, into any raid, and
    /// never loses it there. K9Items registers them; BagKeeper hands existing dogs the ones they had earned.
    /// </summary>
    public static readonly Dictionary<MongoId, string> Collars = new()
    {
        [K9Ids.TrainingCollar] = "training", [K9Ids.BlackCollar] = "black", [K9Ids.TacticalCollar] = "buckle",
    };
    public static MongoId CollarFor(string? style)
    {
        foreach (var (id, s) in Collars) if (s == style) return id;
        return K9Ids.TrainingCollar;
    }

    /// <summary>A piece of gear that isn't a jacket: head gear or the clip-on camera (the client's gear_&lt;look&gt; model
    /// and icon_&lt;look&gt; icon). The dog wears it into PMC raids; it's lost if the dog or you die (BagKeeper).</summary>
    public sealed record Kit(MongoId Id, string Look, string Slot, string Name, string Short, int Width, int Height, double Weight,
                             int Price, int Level, string Offer, string Description);
    public static readonly Kit[] Kits =
    {
        new(K9Ids.GogglesTan, "head_goggles_tan", HeadSlot, "K9 Ballistic Goggles (Tan)", "K9 Goggles", 2, 1, 0.2, 38000, 2, K9Ids.Offer(0x31).ToString(),
            "Strap-on ballistic goggles cut for a dog's head: tinted lens over the eyes, strap behind the ears. Put them on your dog's head in the DOG tab. Lost if the dog or you die in a raid."),
        new(K9Ids.GogglesBlack, "head_goggles_black", HeadSlot, "K9 Ballistic Goggles (Black)", "K9 Goggles", 2, 1, 0.2, 42000, 2, K9Ids.Offer(0x32).ToString(),
            "Strap-on ballistic goggles cut for a dog's head: smoked lens over the eyes, strap behind the ears. Put them on your dog's head in the DOG tab. Lost if the dog or you die in a raid."),
        new(K9Ids.ActionCamera, "camera_action", CameraSlot, "K9 Action Camera (Black)", "K9 Cam", 1, 1, 0.15, 60000, 3, K9Ids.Offer(0x34).ToString(),
            "A rugged action camera on a harness mount. It clips onto the back of whatever K9 jacket your dog wears, facing forward (the camera vest has its own), so you can watch what your dog sees in a raid. Fit it in the DOG tab. Lost if the dog or you die in a raid."),
    };
    public static HashSet<MongoId> KitIds => Kits.Select(k => k.Id).ToHashSet();

    /// <summary>
    /// Gear taken out: the camera headset (0.29.1; the dog's camera is the action camera or the camera vest's). Its item
    /// still exists so a profile that has one loads, but nothing sells it or takes it, and any in a profile becomes an
    /// action camera when the game starts (BagKeeper).
    /// </summary>
    public static readonly Kit[] RetiredKits =
    {
        new(K9Ids.CameraHeadset, "head_headset", HeadSlot, "K9 Camera Headset (old)", "K9 Headset", 2, 2, 0.45, 60000, 4, K9Ids.Offer(0x33).ToString(),
            "No longer made: it becomes a K9 Action Camera."),
    };

    public static bool IsJacket(MongoId template) => Jackets.Any(j => j.Id == template);
    // Jackets with a camera of their own: no slot for the clip-on one.
    public static readonly HashSet<string> BuiltInCamera = new() { "harness_camera" };
    public static HashSet<MongoId> JacketIds => Jackets.Select(j => j.Id).ToHashSet();
}

/// <summary>Registers the jackets and the hidden holder item.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public sealed class GearItems(CustomItemService items, TemplateTable templates, LocaleTable locales, ICloner cloner, ISptLogger<GearItems> logger) : IOnLoad
{
    // The game's Item case: a plain container that is allowed to take items during a raid. Its one grid ("main") is the
    // jackets' load (they were item cases before 0.25, so what's inside them already sits in "main").
    private static readonly MongoId ItemCase = new("59fb042886f7746c5005a7b2");
    // The jackets are armoured rigs like the game's own: copies of the Tasmanian Tiger SK plate carrier (front and back
    // plate slots, nothing else to fill), the sides' slots from the ANA Tactical M2 for the vest that takes four.
    private static readonly MongoId Carrier = new("628cd624459354321c4b7fa2"), SideCarrier = new("5ab8dced86f774646209ec87");
    private const string ArmouredRigsHandbook = "5b5f6f8786f77447ed563642";
    private const string ContainersHandbook = "5b5f6fa186f77409407a7eb7", ElectronicsHandbook = "5b47574386f77428ca22b2ef";
    private static readonly MongoId FlashStorage = new("5c05300686f7746dce784e5d");   // VPX flash storage module

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        MongoId parent = templates.Items[ItemCase].Parent;
        MongoId rigParent = templates.Items[Carrier].Parent;
        int index = 0;
        foreach (var j in DogGear.Jackets.Concat(DogGear.Retired))
        {
            index++;
            bool retired = DogGear.Retired.Contains(j);
            bool camera = !retired && !DogGear.BuiltInCamera.Contains(j.Look);
            string plates = j.Plates == 0 ? "" : $" Takes {(j.Plates == 4 ? "front, back and side" : "front and back")} armour plates (any the game has; fit them in its inspect window or drag them onto it): a plate stops a round where it covers, before the vest's own armour.";
            string clip = camera ? " A K9 action camera clips onto it." : "";
            string repair = j.ArmourClass <= 3 ? "aramid, Cordura or ripstop fabric, or KEKTAPE" : "aramid fiber or Cordura fabric";
            string protection = j.ArmourClass == 0 ? "No armour: light and quick."
                : $"Armour class {j.ArmourClass} over the chest and back, durability {j.Durability}; the dog moves at {j.Speed}% speed in it. " +
                  $"Wear carries over between raids; repair it at a kennel with a Working Dog Station using {repair}.";
            if (!Clone(j.Id, rigParent, "k9companion_jacket_" + (retired ? "old_" + j.Id.ToString()[^3..] : j.Look), j.Price, new LocaleDetails
            {
                Name = DogGear.Name(j),
                ShortName = "K9 " + j.Short,
                Description = $"{protection}{plates}{clip} Carries a {j.GridWidth}x{j.GridHeight} load. Put it on your dog in the DOG tab and fill it like any container; " +
                              "it goes into PMC raids with the dog (look at the dog and press your interact key to open it) and comes home with you when you both make it out."
            }, Carrier, ArmouredRigsHandbook)) continue;
            var props = templates.Items[j.Id].Properties!;
            props.Width = j.Width; props.Height = j.Height; props.Weight = j.Weight;
            // Insurable like the game's own rigs: lost with you or the dog, the insurer may find it (BagKeeper.Insure).
            // Never listed on the flea market (the flea's made-up sellers would offer them bare): it shows only Dr. Yang's
            // plated offers, which the flea takes from his shelves regardless (ragfair.json traderItems is false).
            props.CanSellOnRagfair = false; props.InsuranceDisabled = retired;
            // One load grid, the item case's "main" (the carrier's own pouches would leave what's inside without a place).
            var main = cloner.Clone(templates.Items[ItemCase].Properties!.Grids!.First())!;
            main.Parent = j.Id;
            props.Grids = new List<Grid> { main };
            props.Slots = Slots(j, index, camera);
            // A plain grid: the carrier's pouch layout drawn over the one load grid jumbles it.
            props.RigLayoutName = "";
            // Its own armour, as the inspect window shows it (class, durability, chest and back): the client's DogArmour
            // tier is what actually stops rounds, and the durability shown follows the dog's wear (DogArmour.ShowWear).
            if (j.ArmourClass > 0)
            {
                props.ArmorClass = j.ArmourClass;
                props.Durability = props.MaxDurability = j.Durability;
                props.ArmorMaterial = j.ArmourClass <= 3 ? ArmorMaterial.Aramid : ArmorMaterial.UHMWPE;
                props.ArmorColliders = new List<string> { "RibcageUp", "RibcageLow", "SpineTop", "SpineDown" };
                // The rest of what the game reads on any armour (the carrier copied has none of it; a null crashes the
                // handbook while the profile loads). Values as the game's own soft armour.
                props.ArmorPlateColliders = new List<string>();
                props.Indestructibility = .9;
                props.DeafStrength = "None";
                props.BlindnessProtection = 0;
                props.MaterialType = "BodyArmor";
            }
            var grid = props.Grids?.FirstOrDefault()?.Properties;
            if (grid != null)
            {
                // Never smaller than the old 4x4 saddlebag, so nothing already inside ends up outside the grid.
                grid.CellsH = j.GridWidth; grid.CellsV = j.GridHeight;
                // No jacket inside a jacket.
                var filter = grid.Filters?.FirstOrDefault();
                if (filter != null) filter.ExcludedFilter = (filter.ExcludedFilter ?? new HashSet<MongoId>()).Union(DogGear.JacketIds.Append(DogGear.Holder)).ToHashSet();
            }
        }

        // Head gear and the camera: small items (copies of a flash storage module, which only shows if one is dropped on
        // the ground; the stash shows the client's own picture of the gear).
        foreach (var k in DogGear.Kits.Concat(DogGear.RetiredKits))
        {
            if (!Clone(k.Id, templates.Items[FlashStorage].Parent, "k9companion_" + k.Look, k.Price, new LocaleDetails
            { Name = k.Name, ShortName = k.Short, Description = k.Description }, FlashStorage, ElectronicsHandbook)) continue;
            var props = templates.Items[k.Id].Properties!;
            bool retired = DogGear.RetiredKits.Contains(k);
            props.Width = k.Width; props.Height = k.Height; props.Weight = k.Weight;
            props.CanSellOnRagfair = !retired; props.InsuranceDisabled = retired;
        }

        if (Clone(DogGear.Holder, parent, "k9companion_dog_gear", 1, new LocaleDetails
        {
            Name = "K9 dog", ShortName = "Dog", Description = "What your dog is wearing."
        }))
        {
            var props = templates.Items[DogGear.Holder].Properties!;
            props.Width = 1; props.Height = 1; props.Weight = 0;
            props.CanSellOnRagfair = false; props.InsuranceDisabled = true;
            props.Grids = new List<Grid>();
            props.Slots = new List<Slot>
            {
                new()
                {
                    Name = DogGear.Slot, Id = DogGear.HolderSlotId, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e",
                    Required = false, MergeSlotWithChildren = false,
                    Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.JacketIds } } }
                },
                KitSlot(DogGear.HeadSlot, DogGear.HeadSlotId),
                KitSlot(DogGear.CameraSlot, DogGear.CameraSlotId),
                new()
                {
                    Name = DogGear.CollarSlot, Id = DogGear.CollarSlotId, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e",
                    Required = false, MergeSlotWithChildren = false,
                    Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.Collars.Keys.ToHashSet() } } }
                },
            };
        }

        // The slot's heading in the DOG tab.
        foreach (var language in locales.Global.Values)
            language.AddTransformer(data =>
            {
                if (data != null) { data[DogGear.Slot] = "Jacket"; data[DogGear.HeadSlot] = "Head"; data[DogGear.CameraSlot] = "Camera"; data[DogGear.CollarSlot] = "Collar"; }
                return data;
            });
        logger.Info($"[K9Companion] {DogGear.Jackets.Length} dog jackets and {DogGear.Kits.Length} pieces of head and camera gear registered.");
        return Task.CompletedTask;
    }

    // A jacket's slots: its plates (copies of the carriers' own, so they take what those take) and the clip-on camera.
    private List<Slot> Slots(DogGear.Jacket j, int index, bool camera)
    {
        var slots = new List<Slot>();
        var names = new List<(MongoId from, string name)>();
        if (j.Plates >= 2) { names.Add((Carrier, "Front_plate")); names.Add((Carrier, "Back_plate")); }
        if (j.Plates >= 4) { names.Add((SideCarrier, "Left_side_plate")); names.Add((SideCarrier, "Right_side_plate")); }
        int n = 0;
        foreach (var (from, name) in names)
        {
            Slot? source = templates.Items[from].Properties?.Slots?.FirstOrDefault(s => s.Name == name);
            if (source == null) { logger.Warning($"[K9Companion] The game's {name} slot wasn't found; {DogGear.Name(j)} goes without it."); continue; }
            Slot copy = cloner.Clone(source)!;
            copy.Id = new MongoId(K9Ids.Make(0xa, index, ++n)); copy.Parent = j.Id; copy.Required = false;
            // No plate comes with it.
            foreach (var f in copy.Properties?.Filters ?? new List<SlotFilter>()) f.Plate = null;
            slots.Add(copy);
        }
        if (camera)
            slots.Add(new Slot
            {
                Name = DogGear.CameraSlot, Id = new MongoId(K9Ids.Make(0xa, index, 0xc)), Parent = j.Id, Prototype = "55d30c4c4bdc2db4468b457e",
                Required = false, MergeSlotWithChildren = false,
                Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = new HashSet<MongoId> { K9Ids.ActionCamera } } } }
            });
        return slots;
    }

    // One of the dog's slots for a kind of kit (head gear, the camera).
    private static Slot KitSlot(string name, MongoId id) => new()
    {
        Name = name, Id = id, Parent = DogGear.Holder, Prototype = "55d30c4c4bdc2db4468b457e", Required = false, MergeSlotWithChildren = false,
        Properties = new SlotProperties { Filters = new List<SlotFilter> { new() { Filter = DogGear.Kits.Where(k => k.Slot == name).Select(k => k.Id).ToHashSet() } } }
    };

    private bool Clone(MongoId id, MongoId parent, string name, int price, LocaleDetails text, MongoId? from = null, string handbook = ContainersHandbook)
    {
        var result = items.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = from ?? ItemCase, NewId = id, ParentId = parent, NewItemName = name,
            HandbookParentId = handbook, HandbookPriceRoubles = price, FleaPriceRoubles = price,
            Locales = new Dictionary<string, LocaleDetails> { ["en"] = text }
        });
        if (!result.Success) logger.Error($"[K9Companion] Couldn't create {name}: " + string.Join("; ", result.Errors ?? new List<string>()));
        return result.Success == true;
    }
}
