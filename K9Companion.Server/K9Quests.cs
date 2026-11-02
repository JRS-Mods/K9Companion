using System.Reflection;
using System.Text.Json.Nodes;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Server;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace K9Companion.Server;

/// <summary>
/// One objective that only the dog can complete. It is a SellItemToTrader condition aimed at the dog's hidden gear
/// holder, which can never be sold, so the game never moves it on its own: the client shows its progress from the
/// profile's counter like any other objective, and K9Keeper advances that counter when the event happens.
/// </summary>
public sealed record K9Counter(int Quest, int Index, string Event, int Value)
{
    public string Id => K9Ids.Condition(Quest, Index);
    public MongoId QuestId => K9Ids.Quest(Quest);
}

/// <summary>
/// Dr. Yang's K9 questline (five quests in order) and his five repeatable contracts, written as ordinary quest data:
/// hand-ins, finds, the game's own map zone for the ZB-016 bunker on Woods and extractions are all checked by the game
/// itself. Only the dog's own objectives use K9Counter.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 4)]
public sealed class K9Quests(TemplateTable templates, TradersTable traders, LocaleTable locales, ImageRouter images, ModHelper mods, JsonUtil json, GlobalTable globals,
    ISptLogger<K9Quests> logger) : IOnLoad
{
    public static readonly K9Counter[] Counters =
    {
        new(K9Ids.TeethAndSteel, 0, "armour", 1), new(K9Ids.TeethAndSteel, 1, "extract", 3), new(K9Ids.TeethAndSteel, 2, "kill", 5),
        new(K9Ids.TeethAndSteel, 3, "follow", 1), new(K9Ids.TeethAndSteel, 4, "sit", 1), new(K9Ids.TeethAndSteel, 5, "come", 1),
        new(K9Ids.LastHandler, 2, "extract", 5),
        new(K9Ids.SafeReturn, 0, "extract", 2), new(K9Ids.WorkingPartnership, 0, "extract", 5),
    };

    /// <summary>
    /// 0.30's five-quest story: where each retired quest's objectives went (old objective number -> objective number in
    /// the quest it became part of). Working Dog's (the loot search) went nowhere, nor since 0.37 did what was on Reserve,
    /// Streets and the far side of Woods. K9Keeper.MigrateQuestline moves progress across.
    /// </summary>
    public static readonly (int Old, int Into, Dictionary<int, int> Objectives)[] Merged =
    {
        (K9Ids.StraysOfTarkov, K9Ids.MissingUnit, new() { [3] = 3, [4] = 4 }),     // the K9 tag (the Customs visits went)
        (K9Ids.FamiliarScent, K9Ids.MissingUnit, new()),
        (K9Ids.LearningTheRopes, K9Ids.TeethAndSteel, new() { [0] = 3, [1] = 4, [2] = 5 }),   // follow, sit, come
        (K9Ids.WorkingDog, K9Ids.TeethAndSteel, new()),
        (K9Ids.NoDogLeftBehind, K9Ids.LastHandler, new() { [1] = 4, [2] = 5, [3] = 6 }),   // the vet case and medical kits (the clinic went)
    };

    /// <summary>Contracts: how long after completing one it can be taken again.</summary>
    public static readonly Dictionary<int, TimeSpan> ContractPeriod = new()
    {
        [K9Ids.DailyRations] = TimeSpan.FromHours(24), [K9Ids.SafeReturn] = TimeSpan.FromHours(24), [K9Ids.SupplyRun] = TimeSpan.FromHours(24),
        [K9Ids.WorkingPartnership] = TimeSpan.FromDays(7), [K9Ids.ThePack] = TimeSpan.FromDays(7),
    };

    private const string Yang = "6a5d0c0c4fb9a3c5e1d0d0a0";
    private static readonly string[] DogFood = { "6a5d0c0c4fb9a3c5e1d0d062", "6a5d0c0c4fb9a3c5e1d0d064" };
    private static readonly string[] DrinkingWater = { K9Ids.Water06, K9Ids.Aquamari, K9Ids.WaterRation };
    private static readonly string[] MedKits = { K9Ids.Ai2, K9Ids.CarKit, K9Ids.Salewa, K9Ids.Ifak, K9Ids.Afak, K9Ids.Grizzly };
    private static readonly string[] VetSupplies = { K9Ids.Bloodset, K9Ids.Saline, K9Ids.MedTools, K9Ids.Syringe, K9Ids.Ophthalmoscope };

    private readonly Dictionary<string, string> text = new();
    private int quest, index;
    private JsonArray finish = new(), start = new(), rewards = new();

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        if (templates.Quests.ContainsKey(K9Ids.Quest(1))) { logger.Warning("[K9Companion] K9 quests already present; not adding them again."); return Task.CompletedTask; }
        res = System.IO.Path.Combine(mods.GetAbsolutePathToModFolder(Assembly.GetExecutingAssembly()), "res");
        int added = 0, pictures = 0;
        foreach (JsonObject q in Build())
        {
            Quest? parsed = json.Deserialize<Quest>(q.ToJsonString());
            if (parsed == null) { logger.Error($"[K9Companion] Quest {q["QuestName"]} couldn't be read."); continue; }
            templates.Quests[parsed.Id] = parsed;
            string picture = Picture(parsed.Id);
            if (File.Exists(picture))
            {
                // The address is the image field without ".jpg" (it carries the picture's fingerprint, see ImageFor).
                images.AddRoute(parsed.Image![..^4], picture);
                if (!picture.EndsWith("dryang.jpg")) pictures++;
            }
            added++;
        }
        logger.Info($"[K9Companion] {pictures} quest pictures; the rest show Dr. Yang's portrait.");
        foreach (var language in locales.Global.Values)
            language.AddTransformer(data =>
            {
                if (data != null) foreach (var (key, value) in text) data[key] = value;
                return data;
            });
        // Offers that a quest unlocks at Dr. Yang's: listed under "success" in his quest assort, as for vanilla traders.
        Trader? yang = traders.GetTrader(new MongoId(Yang));
        if (yang?.QuestAssort != null && yang.QuestAssort.TryGetValue("success", out var success))
        {
            foreach (var (offer, unlockedBy) in unlocks) success[offer] = unlockedBy;
            // The rest of the dog's supplies (bedding, treats, training, the collar) likewise wait for the dog.
            foreach (MongoId offer in dogOnly) success.TryAdd(offer, K9Ids.Quest(K9Ids.TrustIsEarned));
        }
        else logger.Warning("[K9Companion] Dr. Yang isn't loaded; quest unlocks at his shop are skipped.");
        logger.Info($"[K9Companion] Dr. Yang's K9 questline: {added} quests ({K9Ids.Story.Length} story, {K9Ids.Contracts.Length} contracts).");
        return Task.CompletedTask;
    }

    private IEnumerable<JsonObject> Build()
    {
        // ---- 1 ------------------------------------------------------------------------------------------------
        Begin(K9Ids.ManBestFriend, "A Man's Best Friend", null, 1);
        Handover("Hand over found in raid cans of dog food or packs of dog meat", DogFood, 3, true);
        Handover("Hand over found in raid drinking water", DrinkingWater, 2, true);
        Handover("Hand over a found in raid medical kit", MedKits, 1, true);
        Pay(2500, 18000, .02);
        GiveWeapon("5448bd6b4bdc2dfc2f8b4569");
        Give(new MongoId("5448c12b4bdc2d02308b456f"), 1);
        Give(new MongoId("5448c12b4bdc2d02308b456f"), 1);
        GiveAmmo("573719762459775a626ccbc1", 48);
        yield return End("Completion", K9Ids.AnyMap,
            "Since the evacuation half of Tarkov's pets and working dogs have gone feral, and the other half are starving in back yards, waiting for owners who aren't coming back. I stayed for them. You don't have to understand that, only help.\n\n" +
            "Before I can take in a single animal I need supplies: three tins of dog food, two bottles of clean water and a proper medical kit. Nothing fancy. Just enough that the next dog brought to my door doesn't die on the table. " +
            "Find them out there, not in a shop: dog food turns up wherever tinned beef does, and scavs carry it.",
            "Bring them to the clinic. And don't open the tins on the way, I know what soldiers' rations taste like.",
            "That will keep a dog alive for a week. You have no idea how rare that is in this city. I'll remember it.");

        // ---- 2: The Missing Unit (with what was Strays of Tarkov) ---------------------------------------------------
        // Only the Customs tag is left, under the numbers Strays of Tarkov's tag came in with (3-4). The rest stay empty so
        // a count from before can't tick something else: 0-1 the Reserve records, 2 the Scav kills, 5-6 the Woods jacket.
        Begin(K9Ids.MissingUnit, "The Missing Unit", K9Ids.ManBestFriend, 5);
        Unused();
        Unused();
        Unused();
        string tag = Find("Find a K9 identification tag in raid (on scavs and PMCs, or in scav corpses, jackets and drawers on Customs)", K9Ids.K9Tag);
        Handover("Hand over the K9 identification tag", new[] { K9Ids.K9Tag.ToString() }, 1, true, tag);
        Pay(16500, 102000, .12);
        GiveWeapon("571a12c42459771f627b58a0");
        Give(new MongoId("571a29dc2459771fb2755a6a"), 1);
        Give(new MongoId("571a29dc2459771fb2755a6a"), 1);
        GiveAmmo("573603562459776430731618", 64);
        GiveWeapon("5ea03f7400685063ec28bfa8");
        Give(new MongoId("5ea034eb5aad6446a939737b"), 1);
        Give(new MongoId("5ea034eb5aad6446a939737b"), 1);
        GiveAmmo("573602322459776445391df1", 140);
        GiveWeapon("574d967124597745970e7c94");
        GiveAmmo("5656d7c34bdc2d9d198b4587", 60);
        yield return End("Completion", K9Ids.AnyMap,
            "Before the conflict the city's police kennel boarded its dogs with us. When the shooting started, the staff ran and the dogs scattered. One of them, Rex of unit seven, had a handler who went looking for him and never came back. " +
            "I want to know where those dogs went.\n\n" +
            "Start with Rex's identification tag. Scavs pick up anything that shines, and PMCs take whatever the scavs had, so search everyone you put down, on any map. On Customs they also turn up in the scav corpses lying about, in jacket pockets and at the back of drawers. Find it yourself; I don't want something somebody sold you.",
            "Rex's tag. Search every scav and PMC you put down.",
            "Rex's tag. Scratched, but you can still read it, and his unit number is on my old boarding list: six dogs on the last truck, bound for a 'temporary kennel' in the Woods. Temporary. The army's favourite word. " +
            "Find me that dog and I'll sell you proper protective jackets for her. And take these: the army left more than dogs behind.");

        // ---- 3 ------------------------------------------------------------------------------------------------
        Begin(K9Ids.TrustIsEarned, "Trust Is Earned", K9Ids.MissingUnit, 12);
        string food = Leave("Stash found in raid dog food in the ZB-016 bunker on Woods", DogFood, "huntsman_005_1", 20);
        string water = Leave("Stash found in raid drinking water in the ZB-016 bunker on Woods", DrinkingWater, "huntsman_005_1", 20);
        Survive("Survive and extract from Woods", "Woods", food, water);
        Pay(8500, 50000, .06);
        GiveWeapon("59d6088586f774275f37482f");
        Give(new MongoId("59d625f086f774661516605d"), 1);
        Give(new MongoId("59d625f086f774661516605d"), 1);
        Give(new MongoId("59d625f086f774661516605d"), 1);
        GiveAmmo("5656d7c34bdc2d9d198b4587", 120);
        // The dog's own gear goes on sale at Dr. Yang's once you have a dog.
        foreach (var k in DogGear.Kits) Unlock(new MongoId(k.Offer), k.Id.ToString(), k.Level);
        // She comes with the coyote training collar on (BagKeeper); this one is hers to change into.
        Give(K9Ids.BlackCollar, 1);
        foreach (int offer in DogSupplyOffers) dogOnly.Add(K9Ids.Offer(offer));
        dogOnly.AddRange(DrYang.PlatedOffers);
        yield return End("Completion", K9Ids.Woods,
            "One of them made it. A hunter saw a dog living in the ZB-016 bunker on the Woods: thin, wary, still wearing what's left of a service harness. She won't come to a stranger, and she will bite.\n\n" +
            "Don't try to catch her. Leave food and water in the bunker, then get out of there alive. She'll follow the scent, and if she trusts you she'll be at my door by the time you get back. " +
            "If you have no dog of your own, she will be yours. If you already have one, she'll stay with me; I always have room for one more. Either way you'll need somewhere proper to keep a dog.",
            "Food and water in the bunker, then leave. Don't wait for her. Don't shoot anything near that bunker if you can help it.",
            "She came in at dawn, limping, with her nose full of your scent. She let me look at her paw. That is trust; don't waste it. " +
            "Now build her a kennel. I've marked out a corner of your rest area for it: you'll find the plans under the Rest Space in your hideout. Bring the materials and build it properly. " +
            "And here's a black collar for her, better than that rag: put it in her collar slot in the DOG tab.");

        // ---- 4: Teeth and Steel (with what was Learning the Ropes) --------------------------------------------
        // Its own objectives keep their numbers (0-2); the commands from Learning the Ropes follow (3-5).
        Begin(K9Ids.TeethAndSteel, "Teeth and Steel", K9Ids.TrustIsEarned, 12);
        Counter("Equip your dog with a protective jacket (armour class 2 or higher)", K9Ids.TeethAndSteel, 0);
        Counter("Survive raids with your dog alive", K9Ids.TeethAndSteel, 1);
        Counter("Eliminate enemies while your dog is with you", K9Ids.TeethAndSteel, 2);
        Counter("Give your dog the Follow command", K9Ids.TeethAndSteel, 3);
        Counter("Give your dog the Sit command", K9Ids.TeethAndSteel, 4);
        Counter("Call your dog with Come here", K9Ids.TeethAndSteel, 5);
        Pay(28500, 185000, .18);
        GiveWeapon("5bfd297f0db834001a669119");
        GiveAmmo("5887431f2459777e1612938f", 40);
        GiveWeapon("5c46fbd72e2216398b5a8c9c");
        Give(new MongoId("5c471c442e221602b542a6f8"), 1);
        Give(new MongoId("5c471c442e221602b542a6f8"), 1);
        GiveAmmo("5887431f2459777e1612938f", 60);
        GiveWeapon("59ff346386f77477562ff5e2");
        Give(new MongoId("5a01c29586f77474660c694c"), 1);
        Give(new MongoId("5a01c29586f77474660c694c"), 1);
        Give(new MongoId("5a01c29586f77474660c694c"), 1);
        GiveAmmo("59e0d99486f7744a32234762", 120);
        Unlock(K9Ids.Offer(3), K9Ids.Fleece, 1);
        Unlock(K9Ids.Offer(7), K9Ids.Cordura, 3);
        Unlock(K9Ids.Offer(8), K9Ids.Aramid, 3);
        Give(K9Ids.TacticalCollar, 1);
        yield return End("Elimination", K9Ids.AnyMap,
            "A dog that won't come back when you call is a liability, not a partner. Work with yours: make it follow you, sit when it's told and come when it's called. You'll find the commands under DOG in your gesture menu.\n\n" +
            "Then, out there, a dog in the open is a target. Put armour on her, a proper protective jacket, class two or better, and take her into the fights she was trained for. Combat mode, your people call it. " +
            "Survive three raids together. And when someone comes at you, let her do what she does: five enemies down with your dog at your side.",
            "Short words, the same words every time. Then armour, then the rest.",
            "She watches your hands now, not the gunfire, and her jacket stopped a round that would have gone through her lungs. I dug it out; keep it if you want a souvenir. " +
            "She's earned a proper collar: a tactical one, with a buckle that won't give (it's in your reward; her collar slot is in the DOG tab). I've found fleece lining for her bed too, and the cloth to keep her jacket in one piece.");

        // ---- 5: The Last Handler (with what was No Dog Left Behind) -------------------------------------------
        // Its own objectives keep their numbers (0-2), and what came from No Dog Left Behind its own (4-6). 3 stays empty:
        // it was the clinic on Streets. The insignia comes from scavs, PMCs and the Dorms, the vet kit (a supply case before
        // 0.38) from the Customs crack house (K9Items).
        Begin(K9Ids.LastHandler, "The Last Handler", K9Ids.TeethAndSteel, 20);
        string badge = Find("Find the K9 unit insignia in raid (on scavs and PMCs, or in the Dorms on Customs)", K9Ids.K9Insignia);
        Handover("Hand over the K9 unit insignia", new[] { K9Ids.K9Insignia.ToString() }, 1, true, badge);
        Counter("Complete raids with your dog alive", K9Ids.LastHandler, 2);
        Unused();
        string vet = Find("Find a K9 veterinary kit in raid (in the crack house on Customs)", K9Ids.VetKit);
        Handover("Hand over the K9 veterinary kit", new[] { K9Ids.VetKit.ToString() }, 1, true, vet);
        Handover("Hand over found in raid medical kits", MedKits, 2, true);
        Pay(40000, 260000, .18);
        GiveWeapon("6513ef33e06849f06c0957ca");
        Give(new MongoId("6513f0a194c72326990a3868"), 1);
        GiveAmmo("5656d7c34bdc2d9d198b4587", 200);
        GiveWeapon("64637076203536ad5600c990");
        Give(new MongoId("646372518610c40fc20204e8"), 1);
        GiveAmmo("59e77a2386f7742ee578960a", 200);
        GivePlated(K9Ids.EliteHarness, "655746010177119f4a097ff7", "6557458f83942d705f0c4962");   // SAPI III+ and sides
        yield return End("Completion", K9Ids.AnyMap,
            "The base had a canine unit; the unit had an insignia. Its last handler wore it until the day he didn't come back. Since then those badges have gone from hand to hand: scavs and PMCs carry them for luck, and there's said to be one lying around the dorms on Customs. " +
            "I'd like one back where it belongs: on the wall of my clinic, above the kennels.\n\n" +
            "And the strays keep coming in worse every week. One of my K9 veterinary kits went missing with a runner, and it turned up in that house on Customs your people call the crack house: there'll be more of them there. Bring me one you find yourself, not one you bought from me, and two medical kits besides. " +
            "Then I want to see the two of you work: five raids together, both of you coming home. After that I'll give you what that handler's dog wore: the unit's elite harness.",
            "The insignia, a vet kit from the crack house, then five raids. Bring her home every time.",
            "There. It's back where it belongs, and the kit is back in my cupboard; I'll keep making more: come and buy them when you need them. You will. " +
            "And this is yours: the elite harness, plated. Light, tough and armoured like a tank. She'll look like she owns the place, which, frankly, she does. Come by any time; there's always work for a handler and a good dog.");

        // ---- contracts ----------------------------------------------------------------------------------------
        Begin(K9Ids.DailyRations, "Daily Rations", K9Ids.LastHandler, 1);
        Handover("Hand over found in raid cans of dog food or packs of dog meat", DogFood, 5, true);
        Pay(3000, 20000, .01);
        GiveAmmo("5656d7c34bdc2d9d198b4587", 60);
        yield return End("Completion", K9Ids.AnyMap,
            "The strays eat every day. So do I, nearly. Five tins of dog food, please. (Contract: available again 24 hours after you complete it.)",
            "Five tins.", "Thank you. They'll eat tonight.");

        Begin(K9Ids.SafeReturn, "Safe Return", K9Ids.LastHandler, 1);
        Counter("Extract with your dog alive", K9Ids.SafeReturn, 0);
        Pay(5000, 35000, .01);
        GiveAmmo("59e0d99486f7744a32234762", 60);
        yield return End("Completion", K9Ids.AnyMap,
            "Take your dog out and bring her back. Twice. That's all I ask. (Contract: available again 24 hours after you complete it.)",
            "Both of you, both times.", "Good. That's the job, really. Everything else is detail.");

        Begin(K9Ids.SupplyRun, "Supply Run", K9Ids.LastHandler, 1);
        Handover("Hand over found in raid medical supplies (bloodsets, saline, medical tools, syringes or ophthalmoscopes)", VetSupplies, 3, true);
        Pay(2000, 0, .01);
        Give(K9Ids.VetKit, 1);
        yield return End("Completion", K9Ids.AnyMap,
            "I'm out of supplies again. Three items: bloodsets, saline, medical tools, syringes, whatever you find. I'll make you a K9 field kit from them. (Contract: available again 24 hours after you complete it.)",
            "Three items. Sealed if you can.", "Here's your kit. Use it before she needs it, not after.");

        Begin(K9Ids.WorkingPartnership, "Working Partnership", K9Ids.LastHandler, 1);
        Counter("Complete raids with your dog alive", K9Ids.WorkingPartnership, 0);
        Pay(12000, 100000, .02);
        GiveAmmo("59e77a2386f7742ee578960a", 60);
        GiveAmmo("59e0d99486f7744a32234762", 60);
        yield return End("Completion", K9Ids.AnyMap,
            "Five successful raids with your dog this week. I pay for steady work, not heroics. (Contract: available again 7 days after you complete it.)",
            "Five raids. Steady.", "A week of good work. Here's a week of pay.");

        Begin(K9Ids.ThePack, "The Pack", K9Ids.LastHandler, 1);
        Handover("Hand over found in raid cans of dog food or packs of dog meat", DogFood, 10, true);
        Pay(4000, 0, .02);
        GivePlated(new MongoId("6a5d0c0c4fb9a3c5e1d0d071"), "656fa25e94b480b8a500c0e0");   // K9 Tactical Vest (Olive), KA Titan plates
        yield return End("Completion", K9Ids.AnyMap,
            "I have a whole pack now. Ten tins of dog food, and I'll give you something the army left behind: a tactical armour jacket, never worn, plates and all. (Contract: available again 7 days after you complete it.)",
            "Ten tins. They eat like wolves.", "Never worn. Make sure it gets dirty.");
    }

    // ---- quest pictures ---------------------------------------------------------------------------------------

    private string res = "";

    // res/quests/<quest number>.jpg (tools/quest_images.py makes them), or Dr. Yang's portrait.
    private string Picture(MongoId questId)
    {
        int n = K9Ids.Story.Concat(K9Ids.Contracts).FirstOrDefault(q => K9Ids.Quest(q) == questId);
        string own = System.IO.Path.Combine(res, "quests", n + ".jpg");
        return File.Exists(own) ? own : System.IO.Path.Combine(res, "dryang.jpg");
    }

    // The game keeps quest pictures by address, so the address carries a fingerprint of the picture: a new picture
    // gets a new address and is downloaded instead of the old copy being shown.
    private string ImageFor(string questId)
    {
        string picture = Picture(new MongoId(questId)), stamp = "0";
        if (File.Exists(picture))
            using (var sha = System.Security.Cryptography.SHA1.Create())
                stamp = Convert.ToHexString(sha.ComputeHash(File.ReadAllBytes(picture)))[..8].ToLowerInvariant();
        return $"/files/quest/icon/{questId}_{stamp}.jpg";
    }

    // ---- quest building ----------------------------------------------------------------------------------------

    private string id = "";
    private string? title;

    private void Begin(int n, string name, int? after, int level)
    {
        quest = n; index = 0; reward = 0; weaponParts = 0; platedParts = 0; title = name; id = K9Ids.Quest(n).ToString();
        finish = new JsonArray(); start = new JsonArray(); rewards = new JsonArray();
        start.Add(Base("Level", K9Ids.Condition(n, 0x80), 0, new() { ["compareMethod"] = ">=", ["value"] = level }));
        if (after is int prior)
            start.Add(Base("Quest", K9Ids.Condition(n, 0x81), 1, new()
            {
                ["availableAfter"] = 0, ["dispersion"] = 0, ["status"] = new JsonArray(4), ["target"] = K9Ids.Quest(prior).ToString()
            }));
    }

    private JsonObject End(string type, string location, string description, string started, string success)
    {
        foreach (string key in new[] { "acceptPlayerMessage", "declinePlayerMessage", "completePlayerMessage", "changeQuestMessageText", "note", "failMessageText" })
            text[$"{id} {key}"] = "";
        text[$"{id} name"] = title!;
        text[$"{id} description"] = description;
        text[$"{id} startedMessageText"] = started;
        text[$"{id} successMessageText"] = success;
        var q = new JsonObject
        {
            ["QuestName"] = title, ["_id"] = id, ["traderId"] = Yang, ["location"] = location, ["type"] = type, ["side"] = "Pmc",
            ["image"] = ImageFor(id), ["status"] = 0,
            ["acceptanceAndFinishingSource"] = "eft", ["progressSource"] = "eft", ["arenaLocations"] = new JsonArray(),
            ["gameModes"] = new JsonArray(), ["rankingModes"] = new JsonArray(), ["canShowNotificationsInGame"] = true,
            ["instantComplete"] = false, ["isKey"] = false, ["restartable"] = false, ["secretQuest"] = false,
            ["conditions"] = new JsonObject { ["AvailableForFinish"] = finish, ["AvailableForStart"] = start, ["Fail"] = new JsonArray() },
            ["rewards"] = new JsonObject { ["Started"] = new JsonArray(), ["Success"] = rewards, ["Fail"] = new JsonArray() }
        };
        foreach (string key in new[] { "name", "description", "note", "successMessageText", "failMessageText", "startedMessageText",
                     "acceptPlayerMessage", "declinePlayerMessage", "completePlayerMessage", "changeQuestMessageText" })
            q[key] = $"{id} {key}";
        return q;
    }

    private static JsonObject Base(string type, string conditionId, int at, JsonObject extra)
    {
        var c = new JsonObject
        {
            ["conditionType"] = type, ["id"] = conditionId, ["index"] = at, ["dynamicLocale"] = false,
            ["globalQuestCounterId"] = "", ["parentId"] = "", ["visibilityConditions"] = new JsonArray()
        };
        foreach (var (k, v) in extra.ToList()) { extra.Remove(k); c[k] = v; }
        return c;
    }

    // An objective number left empty: what a merged quest had there is gone, and a count saved for it must not tick
    // the objective that would otherwise take its number.
    private void Unused() => index++;

    private string Objective(string type, string description, JsonObject extra)
    {
        string conditionId = K9Ids.Condition(quest, index);
        finish.Add(Base(type, conditionId, index, extra));
        text[conditionId] = description;
        index++;
        return conditionId;
    }

    private static JsonObject ItemFields(IEnumerable<string> targets, int count, bool foundInRaid) => new()
    {
        ["target"] = new JsonArray(targets.Select(t => (JsonNode)t).ToArray()), ["value"] = count, ["onlyFoundInRaid"] = foundInRaid,
        ["dogtagLevel"] = 0, ["isEncoded"] = false, ["maxDurability"] = 100, ["minDurability"] = 0
    };

    private string Handover(string description, string[] targets, int count, bool foundInRaid, string? after = null)
    {
        string c = Objective("HandoverItem", description, ItemFields(targets, count, foundInRaid));
        if (after != null) VisibleAfter(c, after);
        return c;
    }

    private string Find(string description, MongoId item)
    {
        var fields = ItemFields(new[] { item.ToString() }, 1, true);
        fields["countInRaid"] = false;
        return Objective("FindItem", description, fields);
    }

    private string Leave(string description, string[] targets, string zone, int seconds)
    {
        var fields = ItemFields(targets, 1, true);
        fields["maxDurability"] = 0; fields["zoneId"] = zone; fields["plantTime"] = seconds;
        return Objective("LeaveItemAtLocation", description, fields);
    }

    private string Counter(string description, int questNumber, int at)
    {
        K9Counter k = Counters.Single(c => c.Quest == questNumber && c.Index == at);
        if (index != at) throw new InvalidOperationException($"K9 counter {k.Id} is listed at index {at} but written at {index}.");
        var fields = ItemFields(new[] { DogGear.Holder.ToString() }, k.Value, false);
        fields["traderId"] = Yang;
        return Objective("SellItemToTrader", description, fields);
    }

    private string Kills(string description, string target, string map, int count) => CounterCreator(description, "Elimination", count, false,
        new JsonObject
        {
            ["conditionType"] = "Kills", ["target"] = target, ["value"] = 1, ["compareMethod"] = ">=", ["bodyPart"] = new JsonArray(),
            ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 }, ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 },
            ["enemyEquipmentExclusive"] = new JsonArray(), ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(),
            ["resetOnSessionEnd"] = false, ["savageRole"] = new JsonArray(), ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
            ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray()
        },
        new JsonObject { ["conditionType"] = "Location", ["target"] = new JsonArray(map) });

    private void Survive(string description, string map, params string[] after)
    {
        string c = CounterCreator(description, "Completion", 1, true,
            new JsonObject { ["conditionType"] = "ExitStatus", ["status"] = new JsonArray("Survived", "Runner", "Transit") },
            new JsonObject { ["conditionType"] = "Location", ["target"] = new JsonArray(map) });
        foreach (string a in after) VisibleAfter(c, a);
    }

    private string CounterCreator(string description, string type, int count, bool oneSession, params JsonObject[] parts)
    {
        var list = new JsonArray();
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i]["id"] = K9Ids.CounterPart(quest, index * 16 + i);
            parts[i]["dynamicLocale"] = false;
            list.Add(parts[i]);
        }
        return Objective("CounterCreator", description, new JsonObject
        {
            ["type"] = type, ["value"] = count, ["oneSessionOnly"] = oneSession, ["completeInSeconds"] = 0,
            ["doNotResetIfCounterCompleted"] = false, ["isNecessary"] = false, ["isResetOnConditionFailed"] = false,
            ["counter"] = new JsonObject { ["id"] = K9Ids.CounterPart(quest, index * 16 + 15), ["conditions"] = list }
        });
    }

    // Shown (and counted) only once another objective of the same quest is done, as the game's own quests do.
    private void VisibleAfter(string conditionId, string after)
    {
        var c = finish.OfType<JsonObject>().First(o => o["id"]!.GetValue<string>() == conditionId);
        var list = (JsonArray)c["visibilityConditions"]!;
        list.Add(new JsonObject
        {
            ["conditionType"] = "CompleteCondition", ["id"] = K9Ids.Make(2, quest, 0x40 + list.Count + 8 * finish.IndexOf(c)), ["target"] = after
        });
    }

    // ---- rewards -----------------------------------------------------------------------------------------------

    private int reward;

    private JsonObject Reward(string type, JsonObject extra)
    {
        var r = new JsonObject
        {
            ["id"] = K9Ids.Reward(quest, reward), ["type"] = type, ["availableInGameEditions"] = new JsonArray(),
            ["gameMode"] = new JsonArray("regular", "pve"), ["isHidden"] = false, ["unknown"] = false
        };
        foreach (var (k, v) in extra.ToList()) { extra.Remove(k); r[k] = v; }
        reward++;
        rewards.Add(r);
        return r;
    }

    private void Pay(int experience, int roubles, double standing)
    {
        Reward("Experience", new JsonObject { ["value"] = experience });
        if (roubles > 0) Give(K9Ids.Roubles, roubles);
        Reward("TraderStanding", new JsonObject { ["target"] = Yang, ["value"] = standing });
    }

    private void Give(MongoId template, int count)
    {
        string item = K9Ids.RewardItem(quest, reward);
        var upd = new JsonObject { ["StackObjectsCount"] = count };
        Reward("Item", new JsonObject
        {
            ["value"] = count, ["target"] = item, ["findInRaid"] = false, ["isEncoded"] = false,
            ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = template.ToString(), ["upd"] = upd })
        });
    }

    // What the quest unlocks at Dr. Yang's: shown in the quest's rewards, and tied to the quest in his quest assort.
    private void Unlock(MongoId offer, string template, int loyalty)
    {
        string item = K9Ids.RewardItem(quest, reward);
        Reward("AssortmentUnlock", new JsonObject
        {
            ["target"] = item, ["traderId"] = Yang, ["loyaltyLevel"] = loyalty,
            ["items"] = new JsonArray(new JsonObject { ["_id"] = item, ["_tpl"] = template })
        });
        unlocks.Add((offer, K9Ids.Quest(quest)));
    }

    private readonly List<(MongoId Offer, MongoId Quest)> unlocks = new();
    private readonly List<MongoId> dogOnly = new();
    // Dr. Yang's dog supplies that make no sense before you have a dog: treats, bed liner, thermal bed, training kit,
    // premium food, the collars (dog food stays on sale: the first quests ask for it).
    // (The veterinary kit, 5, is the only thing that heals the dog: it has to be there from the day you have one.)
    private static readonly int[] DogSupplyOffers = { 5, 9, 12, 13, 14, 26, 29, 0x35, 0x36 };

    // One of the game's weapons, built as its default preset.
    private void GiveWeapon(string original)
    {
        var originalId = new MongoId(original);
        Preset? preset = globals.ItemPresets.Values.FirstOrDefault(p => p.Encyclopedia == originalId && p.Items?.FirstOrDefault()?.Template == originalId)
                         ?? globals.ItemPresets.Values.FirstOrDefault(p => p.Items?.FirstOrDefault()?.Template == originalId);
        string root = K9Ids.RewardItem(quest, reward);
        // Parts are numbered on through the quest (a merged quest gives several weapons; Begin starts the count).
        var parts = preset?.Items?.Take(40).ToList() ?? new List<Item> { new() { Id = originalId, Template = originalId } };
        var ids = new Dictionary<MongoId, string> { [parts[0].Id] = root };
        foreach (Item part in parts.Skip(1)) ids[part.Id] = K9Ids.Make(0xb, quest, weaponParts++);
        var items = new JsonArray();
        foreach (Item part in parts)
        {
            var o = new JsonObject { ["_id"] = ids[part.Id], ["_tpl"] = part.Template.ToString() };
            if (part != parts[0] && part.ParentId != null && ids.TryGetValue(new MongoId(part.ParentId), out string? parent)) { o["parentId"] = parent; o["slotId"] = part.SlotId; }
            items.Add(o);
        }
        Reward("Item", new JsonObject { ["value"] = 1, ["target"] = root, ["findInRaid"] = false, ["isEncoded"] = false, ["items"] = items });
    }
    private int weaponParts;

    // One of the dog's vests with armour plates fitted (Dr. Yang hands out no bare vests): front and back, and the
    // sides when given.
    private void GivePlated(MongoId vest, string plate, string? side = null)
    {
        string root = K9Ids.RewardItem(quest, reward);
        var items = new JsonArray(new JsonObject { ["_id"] = root, ["_tpl"] = vest.ToString() });
        var slots = new List<(string Slot, string Plate)> { ("Front_plate", plate), ("Back_plate", plate) };
        if (side != null) { slots.Add(("Left_side_plate", side)); slots.Add(("Right_side_plate", side)); }
        foreach (var (slot, tpl) in slots)
            items.Add(new JsonObject { ["_id"] = K9Ids.Make(0xd, quest, platedParts++), ["_tpl"] = tpl, ["parentId"] = root, ["slotId"] = slot });
        Reward("Item", new JsonObject { ["value"] = 1, ["target"] = root, ["findInRaid"] = false, ["isEncoded"] = false, ["items"] = items });
    }
    private int platedParts;

    // Rounds, in stacks no bigger than the game allows.
    private void GiveAmmo(string template, int count)
    {
        var id = new MongoId(template);
        int stack = templates.Items.TryGetValue(id, out var t) && t.Properties?.StackMaxSize is int max and > 0 ? max : 60;
        for (; count > 0; count -= stack) Give(id, Math.Min(stack, count));
    }
}
