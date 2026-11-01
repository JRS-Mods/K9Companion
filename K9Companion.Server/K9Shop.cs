using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Services.Modding.Custom;
using SPTarkov.Server.Core.Utils.Cloners;

namespace K9Companion.Server;

/// <summary>
/// The rest of Dr. Yang's shelves: the game's own care items and Asian-styled gear, and his Asian import weapons.
/// The game has no Chinese or Korean firearms of its own, so the weapons are the licence-built copies China and North
/// Korea made of Soviet designs the game does have (Type 56 = AKM, SKS and RPD, Type 54 = TT, ...): each is a clone of
/// that weapon, with its model, parts and magazines, its own name, the original's presets (so the flea market can offer
/// builds of it) and the original's weapon mastery. Every server start Dr. Yang customises his builds a little: grips,
/// muzzle devices, stocks and handguards are swapped at random for parts that fit and don't conflict.
/// Everything is priced from the game's handbook, as vanilla traders are.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 5)]
public sealed class K9Shop(TradersTable traders, TemplateTable templates, GlobalTable globals, CustomItemService items, ICloner cloner,
    LocaleTable locales, ISptLogger<K9Shop> logger) : IOnLoad
{
    private sealed record Import(string Original, string Name, string Short, string Origin, int Level, string About);

    private static readonly Import[] Imports =
    {
        new("59d6088586f774275f37482f", "Norinco Type 56 7.62x39 assault rifle", "Type 56", "China", 3, "China's licence-built AKM pattern, the PLA's rifle for a generation"),
        new("59ff346386f77477562ff5e2", "Type 68 7.62x39 assault rifle", "Type 68", "North Korea", 4, "North Korea's copy of the folding-stock AKM"),
        new("574d967124597745970e7c94", "Norinco Type 56 7.62x39 carbine", "Type 56 SKS", "China", 3, "China's copy of the SKS, made by the million"),
        new("6513ef33e06849f06c0957ca", "Type 56 7.62x39 light machine gun", "Type 56 LMG", "China", 4, "China's copy of the RPD light machine gun"),
        new("64637076203536ad5600c990", "Type 80 7.62x54R general-purpose machine gun", "Type 80", "China", 4, "China's copy of the PKM"),
        new("571a12c42459771f627b58a0", "Norinco Type 54 7.62x25 pistol", "Type 54", "China", 3, "China's copy of the TT-33 service pistol"),
        new("5448bd6b4bdc2dfc2f8b4569", "Type 59 9x18PM pistol", "Type 59", "China", 3, "China's copy of the Makarov PM"),
        new("5ea03f7400685063ec28bfa8", "Type 50 7.62x25 submachine gun", "Type 50", "China", 3, "China's copy of the PPSh-41, used through the Korean War"),
        new("5bfd297f0db834001a669119", "Type 53 7.62x54R bolt-action rifle", "Type 53", "China", 3, "China's copy of the Mosin-Nagant carbine"),
        new("5c46fbd72e2216398b5a8c9c", "Norinco NDM-86 7.62x54R marksman rifle", "NDM-86", "China", 4, "Norinco's Dragunov-pattern marksman rifle (here on the SVDS)"),
    };

    // The game's own items Dr. Yang stocks: template, stock, loyalty level, offer number.
    private static readonly (string Template, int Stock, int Level, int Offer)[] Goods =
    {
        ("5751a25924597722c463c472", 10, 1, 10),   // Army bandage            basic care
        ("544fb25a4bdc2dfb738b4567", 10, 1, 11),   // Aseptic bandage
        ("590c661e86f7741e566b646a", 4, 2, 15),    // Car first aid kit       basic veterinary supplies
        ("544fb45d4bdc2dee738b4568", 3, 2, 16),    // Salewa
        ("590c678286f77426c9660122", 3, 3, 17),    // IFAK                    veterinary medical kits
        ("60098ad7c2240c0fe85c570a", 2, 3, 18),    // AFAK
        ("5e2af29386f7746d4159f077", 3, 3, 19),    // KEKTAPE                 specialist (repairs)
        ("64be7110bf597ba84a0a41ea", 2, 3, 20),    // Type 56 Chicom chest harness
        ("68bee22e79c8186398098e6d", 1, 3, 21),    // DevTac Samurai Menpo masks
        ("68bee238a48c3c320808abc4", 1, 3, 22),
        ("68bee23f6b0736bebc012add", 1, 3, 23),
        ("68bee246ede5c8489f08e1b3", 1, 3, 24),
        ("5a718f958dc32e00094b97e7", 2, 3, 25),    // SGM Tactical 50-round Glock drum (made in Korea)
        ("590c657e86f77412b013051d", 2, 4, 27),    // Grizzly                 advanced veterinary equipment
        ("5d02797c86f774203f38e30a", 1, 4, 28),    // Surv12 field surgical kit
        ("66bdc28a0b603c26902b2011", 1, 4, 30),    // DevTac Ronin ballistic helmet (with its plates)
        ("5b4329f05acfc47a86086aa1", 1, 4, 31),    // DevTac Ronin Respirator

        // Ammunition and magazines for the imports.
        ("59e4d3d286f774176a36250a", 300, 1, 0x60), // 7.62x39mm HP
        ("5656d7c34bdc2d9d198b4587", 300, 2, 0x61), // 7.62x39mm PS gzh
        ("59e0d99486f7744a32234762", 120, 4, 0x62), // 7.62x39mm BP gzh
        ("573602322459776445391df1", 300, 1, 0x63), // 7.62x25mm TT LRNPC
        ("573603562459776430731618", 200, 2, 0x64), // 7.62x25mm TT Pst gzh
        ("573719762459775a626ccbc1", 300, 1, 0x65), // 9x18mm PM P gzh
        ("573719df2459775a626ccbc2", 120, 3, 0x66), // 9x18mm PM PBM gzh
        ("5e023cf8186a883be655e54f", 120, 2, 0x67), // 7.62x54mm R T-46M gzh
        ("5887431f2459777e1612938f", 120, 3, 0x68), // 7.62x54mm R LPS gzh
        ("59e77a2386f7742ee578960a", 60, 4, 0x69),  // 7.62x54mm R PS gzh
        ("59e5d83b86f7745aed03d262", 4, 1, 0x6a),   // AK 7.62x39 ribbed metal 10-round magazine
        ("59d625f086f774661516605d", 6, 1, 0x6b),   // AK 7.62x39 30-round magazine
        ("5a01c29586f77474660c694c", 4, 2, 0x6c),   // AK 7.62x39 6L10 bakelite 30-round magazine
        ("571a29dc2459771fb2755a6a", 6, 1, 0x6d),   // TT-105 8-round magazine
        ("5448c12b4bdc2d02308b456f", 6, 1, 0x6e),   // PM 8-round magazine
        ("5ea034eb5aad6446a939737b", 4, 2, 0x6f),   // PPSh-41 35-round magazine
        ("5ea034f65aad6446a939737e", 2, 3, 0x70),   // PPSh-41 71-round drum
        ("5ae0973a5acfc4001562206c", 4, 2, 0x71),   // Mosin 5-round magazine
        ("5c471c442e221602b542a6f8", 3, 3, 0x72),   // SVD 10-round magazine
        ("6513f0a194c72326990a3868", 2, 4, 0x73),   // RPD Buben 100-round box
        ("646372518610c40fc20204e8", 2, 4, 0x74),   // PK 100-round box

        // More medical and veterinary supplies.
        ("5e831507ea0a7c419c2f9bd9", 6, 1, 0x80),   // Esmarch tourniquet
        ("544fb37f4bdc2dee738b4567", 5, 1, 0x81),   // Analgin painkillers
        ("5d1b3f2d86f774253763b735", 5, 1, 0x82),   // Disposable syringe
        ("6389c6c7dbfd5e4b95197e68", 4, 1, 0x83),   // Aquapeps water purification tablets
        ("590c695186f7741e566b64a2", 4, 2, 0x84),   // Augmentin antibiotic pills
        ("5af0548586f7743a532b7e99", 3, 2, 0x85),   // Ibuprofen painkillers
        ("60098af40accd37ef2175f27", 4, 2, 0x86),   // CAT hemostatic tourniquet
        ("59e3606886f77417674759a5", 3, 2, 0x87),   // Bottle of saline solution
        ("59e361e886f774176c10a2a5", 3, 2, 0x88),   // Bottle of hydrogen peroxide
        ("5c0e533786f7747fa23f4d47", 2, 3, 0x89),   // Zagustin hemostatic injector
        ("5d02778e86f774203e7dedbe", 2, 3, 0x8a),   // CMS surgical kit
        ("619cc01e0a7c3a1a2731940c", 2, 3, 0x8b),   // Medical tools
        ("5b4335ba86f7744d2837a264", 2, 3, 0x8c),   // Medical bloodset
        ("544fb3f34bdc2d03748b456a", 2, 4, 0x8d),   // Morphine injector
        ("5af0534a86f7743b6f354284", 1, 4, 0x8e),   // Ophthalmoscope
    };

    private const double Markup = 1.15;
    private static readonly HashSet<string> Swappable = new() { "mod_pistol_grip", "mod_muzzle", "mod_stock", "mod_handguard" };
    private static readonly MongoId Yang = new("6a5d0c0c4fb9a3c5e1d0d0a0");
    private readonly Random random = new();
    // Each copied preset's id and the original preset it came from: a build's name suffix ("Short", "(AKMB)") is
    // looked up by preset id, so the copies need the originals' text or the game shows the bare id.
    private readonly Dictionary<string, string> presetNames = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Trader? yang = traders.GetTrader(Yang);
        if (yang?.Assort?.Items == null) { logger.Warning("[K9Companion] Dr. Yang isn't loaded; his import stock is skipped."); return Task.CompletedTask; }
        int weapons = 0, goods = 0;
        for (int n = 0; n < Imports.Length; n++)
            if (CloneWeapon(n, out List<Item>? build)) { Add(yang.Assort, K9Ids.Offer(0x40 + n), build!, 2, Imports[n].Level); weapons++; }
        foreach (var (template, stock, level, offer) in Goods)
        {
            var tpl = new MongoId(template);
            if (!templates.Items.ContainsKey(tpl)) { logger.Warning($"[K9Companion] Dr. Yang can't stock {template}: not in this game version."); continue; }
            Add(yang.Assort, K9Ids.Offer(offer), WithPreset(tpl), stock, level);
            goods++;
        }
        foreach (var language in locales.Global.Values)
            language.AddTransformer(data =>
            {
                if (data != null) foreach (var (copy, original) in presetNames) data[copy] = data.TryGetValue(original, out string? text) ? text : "";
                return data;
            });
        logger.Info($"[K9Companion] Dr. Yang's imports: {weapons} Asian weapons (custom builds), {goods} care, gear, ammunition and magazine offers.");
        return Task.CompletedTask;
    }

    // ---- the weapons -----------------------------------------------------------------------------------------

    private bool CloneWeapon(int n, out List<Item>? build)
    {
        build = null;
        Import w = Imports[n];
        var original = new MongoId(w.Original);
        MongoId id = K9Ids.Weapon(n);
        if (!templates.Items.TryGetValue(original, out TemplateItem? source)) { logger.Warning($"[K9Companion] {w.Name}: its original {w.Original} isn't in this game version."); return false; }
        string handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == original)?.ParentId.ToString() ?? "5b5f78e986f77447ed5636b1";
        var result = items.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = original, NewId = id, ParentId = source.Parent, NewItemName = "k9companion_" + w.Short.ToLowerInvariant().Replace(' ', '_'),
            HandbookParentId = handbook, HandbookPriceRoubles = (int)Price(original), FleaPriceRoubles = (int)Price(original),
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = w.Name, ShortName = w.Short,
                    Description = $"{w.About} ({w.Origin}). It uses the original's model, parts and magazines. From Dr. Yang's Asian import stock." }
            }
        });
        if (result.Success != true) { logger.Error($"[K9Companion] Couldn't create {w.Name}: " + string.Join("; ", result.Errors ?? new List<string>())); return false; }

        // The original's mastery counts for the copy too.
        foreach (var mastering in globals.Configuration.Mastering.Where(m => m.Templates.Contains(original)))
            mastering.Templates = mastering.Templates.Append(id).ToList();

        // The original's presets, as the copy's (the default one shows in the handbook; all of them feed the flea market).
        var presets = globals.ItemPresets.Values.Where(p => p.Items?.FirstOrDefault()?.Template == original).Take(5).ToList();
        for (int k = 0; k < presets.Count; k++)
        {
            Preset p = presets[k];
            var map = p.Items.Take(40).Select((item, i) => (item.Id, New: new MongoId(K9Ids.Make(9, n, k * 40 + i)))).ToDictionary(x => x.Id, x => x.New);
            var copy = p.Items.Take(40).Select(item =>
            {
                Item c = cloner.Clone(item)!;
                c.Id = map[item.Id];
                if (item.ParentId != null && map.TryGetValue(new MongoId(item.ParentId), out MongoId parent)) c.ParentId = parent.ToString();
                return c;
            }).ToList();
            copy[0].Template = id;
            var presetId = new MongoId(K9Ids.Make(9, 0xf0 + n, k));
            presetNames[presetId.ToString()] = p.Id.ToString();
            globals.ItemPresets[presetId] = new Preset
            {
                Id = presetId, Type = "Preset", Name = $"{w.Short} {k + 1}", ChangeWeaponName = p.ChangeWeaponName,
                Parent = copy[0].Id, Items = copy, Encyclopedia = p.Encyclopedia == original ? id : (MongoId?)null
            };
        }
        if (presets.Count == 0) { logger.Warning($"[K9Companion] {w.Name} has no preset to build from; not stocked."); return true; }
        build = Customise(presets[random.Next(presets.Count)].Items.Take(40).ToList(), id);
        return true;
    }

    // A copy of a preset with fresh ids, its root on the given template, and (for the imports) a few parts swapped at random.
    private List<Item> Customise(List<Item> preset, MongoId root, bool swap = true)
    {
        var map = preset.ToDictionary(i => i.Id, _ => new MongoId());
        var build = preset.Select(item =>
        {
            Item c = cloner.Clone(item)!;
            c.Id = map[item.Id];
            if (item.ParentId != null && map.TryGetValue(new MongoId(item.ParentId), out MongoId parent)) c.ParentId = parent.ToString();
            return c;
        }).ToList();
        build[0].Template = root;
        if (!swap) return build;
        foreach (Item part in build.Skip(1).Where(p => Swappable.Contains(p.SlotId ?? "")).ToList())
        {
            if (random.NextDouble() > .6 || build.Any(i => i.ParentId == part.Id.ToString())) continue;   // only bare parts
            Item? holder = build.FirstOrDefault(i => i.Id.ToString() == part.ParentId);
            var slot = holder == null ? null : templates.Items.GetValueOrDefault(holder.Template)?.Properties?.Slots?.FirstOrDefault(s => s.Name == part.SlotId);
            var others = build.Where(i => i != part).Select(i => i.Template).ToHashSet();
            var fits = slot?.Properties?.Filters?.FirstOrDefault()?.Filter?.Where(t => t != part.Template && Fits(t, others)).ToList();
            if (fits == null || fits.Count == 0) continue;
            part.Template = fits[random.Next(fits.Count)];
        }
        return build;
    }

    // A part that can stand on its own (needs nothing fitted to it), is a real model, and doesn't clash with the rest.
    private bool Fits(MongoId template, HashSet<MongoId> others)
    {
        if (!templates.Items.TryGetValue(template, out TemplateItem? t) || t.Properties == null) return false;
        if (t.Properties.QuestItem == true || string.IsNullOrEmpty(t.Properties.Prefab?.Path)) return false;
        if (t.Properties.Slots?.Any(s => s.Required == true) == true) return false;
        if (t.Properties.ConflictingItems?.Overlaps(others) == true) return false;
        return !others.Any(o => templates.Items.GetValueOrDefault(o)?.Properties?.ConflictingItems?.Contains(template) == true);
    }

    // ---- offers -------------------------------------------------------------------------------------------

    // An item with what it comes with (a helmet's plates, a gun's parts): the game's default preset as it is, if it has one.
    private List<Item> WithPreset(MongoId template)
    {
        Preset? preset = globals.ItemPresets.Values.FirstOrDefault(p => p.Encyclopedia == template && p.Items?.FirstOrDefault()?.Template == template);
        return preset != null ? Customise(preset.Items.ToList(), template, swap: false) : new List<Item> { new() { Id = new MongoId(), Template = template } };
    }

    private void Add(TraderAssort assort, MongoId offer, List<Item> build, int stock, int level)
    {
        assort.Items.RemoveAll(i => i.Id == offer);
        MongoId oldRoot = build[0].Id;
        foreach (Item i in build) if (i.ParentId == oldRoot.ToString()) i.ParentId = offer.ToString();
        build[0].Id = offer; build[0].ParentId = "hideout"; build[0].SlotId = "hideout"; build[0].Location = null;
        Upd upd = build[0].Upd ??= new Upd();
        upd.UnlimitedCount = false; upd.StackObjectsCount = stock;
        assort.Items.AddRange(build);
        double price = build.Sum(i => Price(i.Template)) * Markup;
        assort.BarterScheme[offer] = [[new BarterScheme { Count = Math.Max(100, Math.Round(price / 100) * 100), Template = K9Ids.Roubles }]];
        assort.LoyalLevelItems[offer] = level;
    }

    // The handbook price, as traders use (flea prices of single weapon parts run far above what a built gun is worth).
    private double Price(MongoId template)
    {
        double? handbook = templates.Handbook.Items.FirstOrDefault(h => h.Id == template)?.Price;
        if (handbook is > 0) return handbook.Value;
        return templates.Prices.TryGetValue(template, out double flea) && flea > 0 ? flea : 1000;
    }
}
