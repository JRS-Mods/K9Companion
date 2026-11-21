using System;
using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.InventoryLogic;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    /// <summary>One of your dogs, as the server mod's roster has it (K9Pack.cs there).</summary>
    internal sealed class PackDog
    {
        public string Id, BreedId, Name, Holder, Jacket, Collar;
        public int Coat = 1, Kennel, Building, Bed, Training, RaidsSurvived;
        public bool Active;
        // How grown it is, 0 (just adopted) to 1 (the server's PackKeeper.Growth: days you've had it, raids it came back from).
        public float Growth = 1;
        /// <summary>Its size against its breed's model as made: young dogs smaller, grown ones a little over a real dog's size.</summary>
        public float BodyScale => Breed.AdultScale * Mathf.Lerp(K9Companion.Growth.YoungSize, 1, Growth);
        /// <summary>Its health and bite against its breed's grown dog's.</summary>
        public float Strength => Mathf.Lerp(K9Companion.Growth.YoungStrength, 1, Growth);
        // Its needs as it left them, while it isn't the active dog (null for the active one, whose needs are NeedsStore's).
        public JObject Needs;
        public Breed Breed => Breed.Get(BreedId);
        public string Shown => string.IsNullOrEmpty(Name) ? Breed.Name : Name;
    }

    /// <summary>
    /// Your dogs. One is the active dog: everything the rest of the mod does with "the dog" (the raid, the DOG tab's gear and
    /// needs, the kennel's levels, its name) is that one's. The others live at their own kennels in the hideout
    /// (KennelResidents). Changing dogs is done here: the server swaps the needs, kennel levels and the rest; the gear moves
    /// between the dog holder and the dogs' roster holders with the game's own item moves, so the profile the game has
    /// loaded stays as the server has it.
    /// </summary>
    internal static class Pack
    {
        private const string AdoptRoute = "/k9companion/pack/adopt", RenameRoute = "/k9companion/pack/rename", SwitchRoute = "/k9companion/pack/switch",
                             ReleaseRoute = "/k9companion/pack/release";

        internal static readonly List<PackDog> Dogs = new List<PackDog>();
        internal static PackDog Active => Dogs.FirstOrDefault(d => d.Active);
        internal static Breed ActiveBreed => Breed.Get(Active?.BreedId);
        internal static int ActiveCoat => Active?.Coat ?? 1;
        // Bumped whenever the roster changes, so the hideout's dogs and the DOG tab know to look again.
        internal static int Version { get; private set; }

        // The adoption papers Dr. Yang sells (the server mod's Pack.Cards): template -> breed and coat. Coat 1 keeps the ids the
        // breeds' pet cards had; coats 2 and 3 are templates 0x50 and 0x58 on, in the server's breed order.
        private static readonly string[] PaperBreeds = { "rottweiler", "husky", "beagle", "shibainu", "pug" };
        internal static readonly Dictionary<string, (string Breed, int Coat)> Cards = PaperBreeds
            .SelectMany((b, i) => new[] { (0x40, 1), (0x50, 2), (0x58, 3) }.Select(t => ("6a5d0c0c4fb9a3c5e1d600" + (t.Item1 + i).ToString("x2"), (b, t.Item2))))
            .ToDictionary(p => p.Item1, p => p.Item2);
        internal const string RosterHolderTemplate = "6a5d0c0c4fb9a3c5e1d0d090";

        internal static string CardBreed(Item item) => item != null && Cards.TryGetValue(item.TemplateId.ToString(), out var c) ? c.Breed : null;
        internal static int CardCoat(Item item) => item != null && Cards.TryGetValue(item.TemplateId.ToString(), out var c) ? c.Coat : 1;

        /// <summary>The roster from the K9 state (K9.Apply) or a pack route's reply.</summary>
        internal static void Apply(JToken dogs, string active = null)
        {
            if (!(dogs is JArray list)) return;
            string wasBreed = Active?.BreedId; int wasCoat = ActiveCoat; float wasScale = Active?.BodyScale ?? 0;
            Dogs.Clear();
            foreach (JToken d in list)
            {
                var dog = new PackDog
                {
                    Id = (string)d["id"], BreedId = (string)d["breed"] ?? Breed.ShepherdId, Name = (string)d["name"] ?? "",
                    Coat = (int?)d["coat"] ?? 1, Active = (bool?)d["active"] ?? false, Kennel = (int?)d["kennel"] ?? 0,
                    Building = (int?)d["building"] ?? 0, Bed = (int?)d["bed"] ?? 0, Training = (int?)d["training"] ?? 0,
                    Holder = (string)d["holder"], Jacket = (string)d["jacket"], Collar = (string)d["collar"], Needs = d["needs"] as JObject,
                    Growth = Mathf.Clamp01((float?)d["growth"] ?? 1), RaidsSurvived = (int?)d["raidsSurvived"] ?? 0,
                };
                if (dog.Id != null) Dogs.Add(dog);
            }
            if (active != null) foreach (PackDog d in Dogs) d.Active = d.Id == active;
            PackDog now = Active;
            // The name on the collar tag is the active dog's. The one name kept from before (in the settings) belongs to the
            // shepherd you had then, your only dog: it takes it once. A dog you haven't named goes by its breed's name; it
            // never takes the last active dog's (that's how every new dog came to be called K9).
            if (now != null)
            {
                if (string.IsNullOrEmpty(now.Name))
                {
                    if (Dogs.Count == 1 && now.Breed.IsShepherd && !string.IsNullOrEmpty(Plugin.DogName.Value))
                    { now.Name = Plugin.DogName.Value; Send(RenameRoute, new JObject { ["dog"] = now.Id, ["name"] = now.Name }); }
                    else if (Plugin.DogName.Value != now.Breed.Name) Plugin.SaveName(now.Breed.Name);
                }
                else if (now.Name != Plugin.DogName.Value) Plugin.SaveName(now.Name);
                DogModel.WarmUp(now.Breed);
            }
            Version++;
            if (now != null && (now.BreedId != wasBreed || now.Coat != wasCoat || Mathf.Abs(now.BodyScale - wasScale) > .02f) && wasBreed != null) Plugin.DogChanged();
        }

        /// <summary>The active dog was renamed (the rename dialog): kept with the dog on the server.</summary>
        internal static void Renamed(string name)
        {
            PackDog dog = Active;
            if (dog == null) return;
            dog.Name = name;
            Send(RenameRoute, new JObject { ["dog"] = dog.Id, ["name"] = name });
        }

        internal static void Rename(PackDog dog, string name, Action<string> done)
        {
            Post(RenameRoute, new JObject { ["dog"] = dog.Id, ["name"] = name }, reply =>
            {
                if (dog.Active) Plugin.SaveName(name);
                done(null);
            }, done);
        }


        // ---- adopting ---------------------------------------------------------------------------------------------------

        /// <summary>The card is used up (the game's own removal), then the server takes the dog on.</summary>
        internal static void Adopt(InventoryController controller, Item card, Action<string> done)
        {
            string breed = CardBreed(card); int coat = CardCoat(card);
            if (breed == null) { done("Those aren't adoption papers."); return; }
            if (Dogs.Any(d => d.BreedId == breed)) { done($"You have a {Breed.Get(breed).Name} already: one dog of each breed."); return; }
            DogPantry.Use(controller, card, ok =>
            {
                if (!ok) { done("The papers couldn't be used."); return; }
                Post(AdoptRoute, new JObject { ["breed"] = breed, ["coat"] = coat }, reply => done(null),
                    why => done("The papers are used, but the server mod didn't take the dog on (" + why + "). Check the SPT server window."));
            });
        }

        // ---- giving a dog back ------------------------------------------------------------------------------------------

        /// <summary>
        /// A dog given back to Dr. Yang: its gear goes to your stash first (the game's own moves), then the server lets it go,
        /// and its breed's papers are on his shelf again. Not the active dog, nor the K9 unit's shepherd.
        /// </summary>
        internal static void Release(InventoryController controller, EFT.Profile profile, PackDog dog, Action<string> done)
        {
            if (dog == null || dog.Active || dog.Breed.IsShepherd) { done("That dog can't be given back."); return; }
            CompoundItem holder = string.IsNullOrEmpty(dog.Holder) ? null
                : profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(i => i.Id.ToString() == dog.Holder) as CompoundItem;
            var gear = holder == null ? new List<Item>() : holder.Slots.Select(s => s.ContainedItem).Where(i => i != null).ToList();
            int left = 0;
            void Next(int i)
            {
                if (i >= gear.Count)
                {
                    if (left > 0) { done(left + " piece(s) of its gear wouldn't fit in your stash: make room and try again."); return; }
                    Post(ReleaseRoute, new JObject { ["dog"] = dog.Id }, _ => done(null), done);
                    return;
                }
                Item item = gear[i];
                ItemAddress spot = null;
                foreach (var grid in profile.Inventory.Stash.Grids) { spot = grid.FindLocationForItem(item); if (spot != null) break; }
                if (spot == null) { left++; Next(i + 1); return; }
                var move = ItemManipulator.Move(item, spot, controller, simulate: true);
                if (move.Failed) { left++; Next(i + 1); return; }
                controller.TryRunNetworkTransaction(move, result => { if (!result.Succeed) left++; Next(i + 1); });
            }
            if (controller == null) { done("The gear couldn't be moved (no inventory)."); return; }
            Next(0);
        }

        // ---- changing dogs ----------------------------------------------------------------------------------------------

        internal static bool Switching { get; private set; }

        /// <summary>Makes the dog the active one (out of raids, from the DOG tab).</summary>
        internal static void Switch(InventoryController controller, EFT.Profile profile, PackDog to, Action<string> done)
        {
            if (to == null || to.Active) { done(null); return; }
            if (Switching) { done("Already changing dogs."); return; }
            Switching = true;
            NeedsStore.Save();
            Post(SwitchRoute, new JObject { ["dog"] = to.Id }, reply =>
            {
                try
                {
                    K9.ApplySwitch(reply);
                    ApplyKennel(profile, reply["kennel"] as JObject);
                    NeedsStore.Forget();
                    string outHolder = (string)reply["out"], inHolder = (string)reply["in"];
                    Apply(reply["dogs"], (string)reply["active"]);
                    MoveGear(controller, profile, outHolder, inHolder, problem =>
                    {
                        Switching = false;
                        Plugin.DogChanged();
                        done(problem);
                    });
                }
                catch (Exception e) { Switching = false; Plugin.Log.LogError("Changing dogs: " + e); done("Something went wrong changing dogs; the BepInEx log has more."); }
            }, why => { Switching = false; done(why); });
        }

        // The kennel's three levels as they now are (the new dog's), in the profile the game has loaded and in the hideout's
        // own list, so the Rest Space screen and K9.Kennel see them straight away.
        private static void ApplyKennel(EFT.Profile profile, JObject kennel)
        {
            if (kennel == null) return;
            var built = profile?.Hideout?.Improvements;
            var controller = Comfort.Common.Singleton<EFT.Hideout.HideoutRepresentation>.Instantiated
                ? Comfort.Common.Singleton<EFT.Hideout.HideoutRepresentation>.Instance.ImprovementController : null;
            var live = controller?._improvementDataBy;
            foreach (string id in K9.KennelIds)
            {
                var entry = kennel[id] as JObject;
                EFT.Hideout.ImprovementData data = entry == null ? null
                    : new EFT.Hideout.ImprovementData { Completed = (bool?)entry["completed"] ?? false, CompleteTimestamp = (int)((long?)entry["at"] ?? 0) };
                foreach (var list in new[] { built, live })
                {
                    if (list == null) continue;
                    list.Remove(id);
                    if (data != null) list[id] = data;
                }
            }
            // The hideout's levels keep their own copy of whether they're built (Improvement.Completed, what the kennel's
            // screen shows): taken from the list again, so the new dog's kennel shows as its own, not the last dog's.
            if (controller != null)
                try { controller.UpdateImprovements(); }
                catch (Exception e) { Plugin.Log.LogWarning("The kennel's levels couldn't be shown afresh (they will be next time the hideout loads): " + e.Message); }
        }

        // The active dog's gear into its roster holder, then the new dog's out of its own onto the dog holder; slot by slot,
        // each a move the game makes itself. Anything that can't go where it should goes to your stash instead.
        private static void MoveGear(InventoryController controller, EFT.Profile profile, string outHolder, string inHolder, Action<string> done)
        {
            CompoundItem holder = DogGear.Holder(profile);
            CompoundItem From(string id) => string.IsNullOrEmpty(id) ? null
                : profile?.Inventory?.QuestStashItems?.GetAllItems().FirstOrDefault(i => i.Id.ToString() == id) as CompoundItem;
            CompoundItem kept = From(outHolder), coming = From(inHolder);
            var moves = new List<(Item Item, Func<ItemAddress> To)>();
            if (holder != null)
            {
                foreach (Slot slot in holder.Slots)
                {
                    Item item = slot.ContainedItem;
                    if (item == null) continue;
                    string name = slot.ID;
                    moves.Add((item, () => kept?.Slots?.FirstOrDefault(s => s.ID == name)?.CreateItemAddress()));
                }
                if (coming != null)
                    foreach (Slot slot in coming.Slots)
                    {
                        Item item = slot.ContainedItem;
                        if (item == null) continue;
                        string name = slot.ID;
                        moves.Add((item, () => holder.Slots.FirstOrDefault(s => s.ID == name)?.CreateItemAddress()));
                    }
            }
            int stashed = 0;
            void Next(int i)
            {
                if (i >= moves.Count) { done(stashed > 0 ? stashed + " piece(s) of gear went to your stash: there was nowhere else for them." : null); return; }
                var (item, to) = moves[i];
                ItemAddress address = to();
                if (address == null || ItemManipulator.Move(item, address, controller, simulate: true).Failed) { ToStash(item, () => Next(i + 1)); return; }
                controller.TryRunNetworkTransaction(ItemManipulator.Move(item, address, controller, simulate: true), result =>
                {
                    if (result.Succeed) Next(i + 1);
                    else ToStash(item, () => Next(i + 1));
                });
            }
            void ToStash(Item item, Action then)
            {
                stashed++;
                ItemAddress spot = null;
                foreach (var grid in profile.Inventory.Stash.Grids) { spot = grid.FindLocationForItem(item); if (spot != null) break; }
                if (spot == null) { Plugin.Log.LogWarning("Changing dogs: " + item.ShortName.Localized() + " couldn't be moved at all; it stays where it is."); then(); return; }
                var move = ItemManipulator.Move(item, spot, controller, simulate: true);
                if (move.Failed) { Plugin.Log.LogWarning("Changing dogs: " + item.ShortName.Localized() + " couldn't be moved at all; it stays where it is."); then(); return; }
                controller.TryRunNetworkTransaction(move, _ => then());
            }
            if (controller == null) { done("The gear couldn't be moved (no inventory)."); return; }
            Next(0);
        }

        // ---- talking to the server -----------------------------------------------------------------------------------

        private static void Post(string route, JObject body, Action<JObject> ok, Action<string> failed)
        {
            RequestHandler.PostJsonAsync(route, body.ToString(Newtonsoft.Json.Formatting.None)).ContinueWith(t =>
            {
                string error = null; JObject reply = null;
                if (t.IsFaulted) error = t.Exception?.GetBaseException().Message ?? "no answer";
                else
                {
                    try { reply = JObject.Parse(t.Result ?? "{}"); }
                    catch (Exception e) { error = e.Message; }
                    if (reply != null && reply["ok"]?.Value<bool>() != true) error = (string)reply["problem"] ?? "the server mod said no";
                }
                // Back on the main thread: the replies change game objects.
                Plugin.OnMainThread(() =>
                {
                    if (error != null) { Plugin.Log.LogWarning($"{route}: {error}"); failed(error); return; }
                    if (reply["dogs"] != null && route != SwitchRoute) Apply(reply["dogs"]);
                    ok(reply);
                });
            });
        }

        private static void Send(string route, JObject body) => Post(route, body, _ => { }, _ => { });
    }
}
