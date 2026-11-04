using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System;
using Comfort.Common;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using SPT.Common.Http;
using UnityEngine;

namespace K9Companion
{
    public sealed partial class Companion
    {
        // The pack has no eating or drinking clip. Stand_02's idle puts the nose to the ground (jaw ~0.2 m above it,
        // frames 121-181 of 501), the same segment the old grab used, so meals play that. Only these constants depend on it.
        private const string MealClip = "Stand_02";
        private const float MealFrom = 95f / 501f, MealTo = 205f / 501f, MealSpeed = 1.1f, HandReach = 3f, BowlReach = 1.2f;

        private enum MealSource { None, Dish, Hand }

        private DogNeeds needs;
        private MealSource meal;
        private bool mealFood, mealRestart;
        private Item mealItem;
        private float nextNeedsSave, nextAutoCheck, nextRefill, nextDishCheck;
        private int warned;

        private bool NeedsOn => Plugin.SurvivalNeeds.Value;
        // Combat-mode penalty for a hungry or thirsty dog: slower, and slower to bite again. Never any damage.
        private float NeedsFactor => NeedsOn && rules.Combat && needs != null ? needs.Fitness : 1f;

        private void InitNeeds()
        {
            needs = NeedsStore.Current;
            nextNeedsSave = Time.time + 60;
            decor?.SetSupplies(needs.FoodDish, needs.WaterDish);
        }

        private void TickNeeds(float dt, Player owner, bool hideout)
        {
            if (needs == null) return;
            if (NeedsOn && !dead)
                needs.Decay(dt / 3600f * (hideout ? K9.RestPace(kennelLevel) : 1f), Plugin.HungerDecayRate.Value, Plugin.ThirstDecayRate.Value);
            if (hideout) decor?.SetSupplies(needs.FoodDish, needs.WaterDish);
            if (Time.time >= nextNeedsSave) { nextNeedsSave = Time.time + 60; NeedsStore.Save(); }
            if (!NeedsOn || dead) return;

            int level = Mathf.Min(needs.Hunger, needs.Thirst) < 10 ? 2 : Mathf.Min(needs.Hunger, needs.Thirst) < DogNeeds.Hungry ? 1 : 0;
            if (level > warned) Say(Plugin.DogName.Value + (needs.Hunger <= needs.Thirst ? " is hungry." : " is thirsty.") + (level == 2 ? " It's slowing down." : ""));
            warned = level;

            if (meal != MealSource.None || attackTarget != null || hitClip != null || Time.time < nextAutoCheck) return;
            nextAutoCheck = Time.time + 3;
            if (hideout)
            {
                // At home the dog helps itself from its dishes, and (if allowed) you keep them topped up from the stash.
                if (Plugin.AutoFeedInHideout.Value && Time.time >= nextRefill)
                {
                    if (needs.FoodDish < 25) { nextRefill = Time.time + 20; RefillDish(owner, true, true); }
                    else if (needs.WaterDish < 25) { nextRefill = Time.time + 20; RefillDish(owner, false, true); }
                }
                if (needs.Hunger < DogNeeds.EatsFromDishBelow && needs.FoodDish >= 1 && decor?.FoodBowl != null) StartMeal(MealSource.Dish, true, null);
                else if (needs.Thirst < DogNeeds.EatsFromDishBelow && needs.WaterDish >= 1 && decor?.WaterBowl != null) StartMeal(MealSource.Dish, false, null);
            }
            else if (command == Command.Follow || command == Command.Stay || command == Command.Sit)
            {
                // In a raid the dog only ever helps itself from its own jacket, never from your gear.
                if (needs.Hunger < DogNeeds.EatsFromBagBelow) { Item food = DogPantry.Best(owner, false, true, 100 - needs.Hunger, true); if (food != null) StartMeal(MealSource.Hand, true, food); }
                else if (needs.Thirst < DogNeeds.EatsFromBagBelow) { Item water = DogPantry.Best(owner, false, false, 100 - needs.Thirst, true); if (water != null) StartMeal(MealSource.Hand, false, water); }
            }
        }

        // ---- commands -------------------------------------------------------------------------------------------

        // DOG menu Feed / Give water. In the hideout this fills the dish; in a raid you hand the food over.
        private void OrderMeal(Player owner, bool food)
        {
            if (IsHideout) { RefillDish(owner, food, false); return; }
            float need = food ? needs.Hunger : needs.Thirst;
            if (need > 95) { Say(Plugin.DogName.Value + (food ? " isn't hungry." : " isn't thirsty.")); return; }
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach) { Say("Call " + Plugin.DogName.Value + " over first."); return; }
            Item item = DogPantry.Best(owner, false, food, 100 - need);
            if (item == null) { Say(food ? "No dog food or meat in your gear or the dog's jacket." : "No water in your gear or the dog's jacket."); return; }
            if (meal != MealSource.None) { Say(Plugin.DogName.Value + " is already eating."); return; }
            StartMeal(MealSource.Hand, food, item);   // it keeps its current order; it only stops to eat
        }

        // Right-click "Feed dog" / "Give water to dog" on an item you carry in a raid. Null when the dog takes it,
        // otherwise why not.
        internal string FeedFromInventory(Item item, bool food)
        {
            Player owner = CurrentPlayer;
            string name = Plugin.DogName.Value;
            if (dog == null || owner == null || needs == null) return "No dog.";
            if (dead) return DeadNote + ".";
            if (!NeedsOn) return "Survival needs are off.";
            if (meal != MealSource.None) return name + " is already eating.";
            if (!DogPantry.Carried(owner, item) || DogPantry.ValueOf(item, food) <= 0) return "That can't be given to the dog.";
            if ((food ? needs.Hunger : needs.Thirst) > 95) return name + (food ? " isn't hungry." : " isn't thirsty.");
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach) return "Call " + name + " over first.";
            StartMeal(MealSource.Hand, food, item);
            return null;
        }

        // Right-click "Feed dog" / "Give water to dog" on food or water in the hideout (stash, gear or the dog's jacket):
        // the dog has it at once. Null when it does, otherwise why not.
        internal string FeedAtHome(Item item, bool food)
        {
            Player owner = CurrentPlayer;
            string name = Plugin.DogName.Value;
            if (dog == null || owner == null || needs == null) return "No dog.";
            if (!NeedsOn) return "Survival needs are off.";
            float wanted = 100 - (food ? needs.Hunger : needs.Thirst);
            if (wanted < 5) return name + (food ? " isn't hungry." : " isn't thirsty.");
            if (DogPantry.ValueOf(item, food) <= 0) return "That can't be given to the dog.";
            string itemName = item.ShortName.Localized();
            DogPantry.Consume(owner, item, food, wanted, given =>
            {
                if (given <= 0) { Say("Couldn't use the " + itemName + "."); return; }
                if (food) needs.Hunger = Mathf.Min(100, needs.Hunger + given); else needs.Thirst = Mathf.Min(100, needs.Thirst + given);
                NeedsStore.Save();
                audio?.Play("happy");
                Say(name + (food ? " ate the " : " drank the ") + itemName + ".");
            });
            return null;
        }

        /// <summary>How full a kennel dish is (0-100).</summary>
        internal float DishLevel(bool food) => needs == null ? 0 : food ? needs.FoodDish : needs.WaterDish;

        /// <summary>Fills a kennel dish from your stash and gear (the kennel's interaction menu).</summary>
        internal void FillDish(bool food) { Player owner = CurrentPlayer; if (owner != null) RefillDish(owner, food, false); }

        private void RefillDish(Player owner, bool food, bool quiet)
        {
            if (decor?.Home == null) { if (!quiet) Say("The kennel isn't set up yet."); return; }
            float dish = food ? needs.FoodDish : needs.WaterDish;
            if (dish > 90) { if (!quiet) Say(food ? "The food dish is full." : "The water dish is full."); return; }
            Item item = DogPantry.Best(owner, true, food, 100 - dish);
            if (item == null) { if (!quiet) Say(food ? "No dog food or meat in your stash or gear." : "No water in your stash or gear."); return; }
            float value = DogPantry.ValueOf(item, food);
            string name = item.ShortName.Localized();
            DogPantry.Consume(owner, item, ok =>
            {
                if (!ok) { if (!quiet) Say("Couldn't use the " + name + "."); return; }
                if (food) needs.FoodDish = Mathf.Min(100, needs.FoodDish + value); else needs.WaterDish = Mathf.Min(100, needs.WaterDish + value);
                decor?.SetSupplies(needs.FoodDish, needs.WaterDish);
                NeedsStore.Save();
                Say((food ? "Food" : "Water") + " dish filled with " + name + ".");
            });
        }

        internal string NeedsLabel(DogOrder order)
        {
            if (needs == null) return null;
            bool food = order == DogOrder.Feed;
            if (IsHideout)
                return (food ? "Fill food dish" : "Fill water dish") + $" ({Mathf.RoundToInt(food ? needs.FoodDish : needs.WaterDish)}%)";
            return (food ? "Feed" : "Give water") + $" ({(food ? "hunger" : "thirst")} {Mathf.RoundToInt(food ? needs.Hunger : needs.Thirst)}%)";
        }

        internal string NeedsUnavailable(DogOrder order)
        {
            if (!NeedsOn) return "Survival needs are off";
            if (needs == null || dog == null) return "No dog";
            if (dead) return DeadNote;
            bool food = order == DogOrder.Feed;
            if (DogPantry.Best(CurrentPlayer, IsHideout, food, 1) == null) return food ? "No dog food or meat" : "No water";
            return null;
        }

        // ---- the meal itself ------------------------------------------------------------------------------------

        private void StartMeal(MealSource source, bool food, Item item)
        {
            // From your hand: you hold it out to the dog.
            if (source == MealSource.Hand) Reach(CurrentPlayer);
            meal = source; mealFood = food; mealItem = item; mealRestart = true;
            roaming = false; nextDishCheck = 0;
            WakeUp();
        }

        private void CancelMeal() { meal = MealSource.None; mealItem = null; }

        private Vector3? MealSpot
        {
            get
            {
                if (meal == MealSource.Dish) return mealFood ? decor?.FoodBowl : decor?.WaterBowl;
                Player owner = CurrentPlayer;
                return meal == MealSource.Hand && owner != null ? owner.Transform.position : (Vector3?)null;
            }
        }

        // Walking to the dish is part of the meal; true while the dog is on its way.
        private bool MealWalking(out Vector3 spot)
        {
            spot = default;
            if (meal != MealSource.Dish || !(MealSpot is Vector3 at)) return false;
            spot = at;
            return Flat(dog.transform.position, at) > BowlReach;
        }

        private static float Flat(Vector3 a, Vector3 b) { a.y = b.y = 0; return Vector3.Distance(a, b); }

        // Eating or drinking: the dog stands at the dish (or in front of you), head down, and owns the frame.
        private bool TickMeal(float dt)
        {
            if (meal == MealSource.None) return false;
            if (dead || hitClip != null || attackTarget != null) { CancelMeal(); return false; }
            if (!(MealSpot is Vector3 spot)) { CancelMeal(); return false; }
            float reach = meal == MealSource.Dish ? BowlReach : HandReach;
            if (Flat(dog.transform.position, spot) > reach)
            {
                // Walks to the dish when it can find a way; otherwise (or if it gets stuck) it goes straight there.
                if (meal == MealSource.Dish && WalkingWithoutNavmesh && Time.time >= nextDishCheck)
                {
                    nextDishCheck = Time.time + 2;
                    if (!Reachable(spot) || Time.time - lastProgress > 6) Teleport(spot - (spot - dog.transform.position).normalized * .7f, spot);
                }
                else if (meal == MealSource.Hand) { CancelMeal(); Say("Call " + Plugin.DogName.Value + " over first."); }
                return false;   // keep walking
            }
            WakeUp();
            if (stance != Stance.Stand) { speed = 0; Animate(dt); return true; }
            speed = shownSpeed = 0; lastProgress = Time.time;
            Vector3 look = spot - dog.transform.position; look.y = 0;
            if (look.sqrMagnitude > .01f) yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, 360 * dt);
            dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            bool done = model.TickSegment(MealClip, MealFrom, MealTo, MealSpeed, dt, mealRestart);
            mealRestart = false;
            if (done) FinishMeal();
            return true;
        }

        private void FinishMeal()
        {
            MealSource source = meal; bool food = mealFood; Item item = mealItem;
            CancelMeal();
            if (source == MealSource.Dish)
            {
                if (food) needs.EatFromDish(); else needs.DrinkFromDish();
                decor?.SetSupplies(needs.FoodDish, needs.WaterDish);
                NeedsStore.Save();
                // Back to its bed afterwards, unless you have it following you around.
                if (IsHideout && command != Command.Follow) GoToBed(true);
                return;
            }
            // Handed over: the item is only used up now that the dog has actually eaten, only if you still have it, and
            // (for a multi-portion item) only as much as the dog needs.
            Player owner = CurrentPlayer;
            if (owner == null || item == null || item.CurrentAddress == null || DogPantry.ValueOf(item, food) <= 0)
            {
                Plugin.Log.LogInfo($"Meal not given: owner {owner != null}, item {item?.ShortName?.Localized() ?? "none"}, in place {item?.CurrentAddress != null}, worth {(item != null ? DogPantry.ValueOf(item, food) : 0)}.");
                Say("The food is gone."); return;
            }
            string name = item.ShortName.Localized();
            float wanted = 100 - (food ? needs.Hunger : needs.Thirst);
            DogPantry.Consume(owner, item, food, wanted, given =>
            {
                if (given <= 0) { Plugin.Log.LogWarning("The game wouldn't use up the " + name + " for the dog."); Say("Couldn't use the " + name + "."); return; }
                if (food) needs.Hunger = Mathf.Min(100, needs.Hunger + given); else needs.Thirst = Mathf.Min(100, needs.Thirst + given);
                NeedsStore.Save();
                audio?.Play("happy");
                Say(Plugin.DogName.Value + (food ? " ate the " : " drank the ") + name + ".");
            });
        }
    }

    /// <summary>
    /// Treating the dog in a raid with the K9 veterinary kit (DogVet). With the dog close by, you close your inventory, kneel
    /// and work on it, with the game's own kneel-and-work animation and countdown (planting a marker, repairing), the dog
    /// lying still beside you. Longer the more there is to do. Any key calls it off, as with those. Only as much of the kit
    /// as the dog needed is used, and what's healed stays healed after the raid.
    /// </summary>
    public sealed partial class Companion
    {
        private Item treatItem;
        private float treatStart, treatUntil;
        private const float QuickestTreat = 3f, LongestTreat = 8f;
        // Any input calls the kneeling off, the mouse button you clicked with coming up included: it starts a moment later.
        private const float TreatDelay = .3f;

        internal bool Treating => treatItem != null;

        private float MissingPercent => (rules.Health - health) / Mathf.Max(1, rules.Health) * 100;

        // Why this kit can't be used on the dog right now; null when it can.
        internal string TreatProblem(Item item)
        {
            Player owner = CurrentPlayer;
            string name = Plugin.DogName.Value;
            if (dog == null || owner == null) return "No dog.";
            if (dead && !downed) return name + " is dead.";
            if (treatItem != null) return "You're already treating " + name + ".";
            if (!downed && health >= rules.Health - .5f) return name + " isn't hurt.";
            if (DogVet.Holds(item, 1) <= 0) return "This kit is empty.";
            if (meal != MealSource.None) return name + " is eating.";
            if (attackTarget != null || hitClip != null) return name + " is fighting.";
            if (!DogPantry.Carried(owner, item)) return "Take the kit first.";
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach) return downed ? "Get closer to " + name + " first." : "Call " + name + " over first.";
            return null;
        }

        // The kit to revive it with: the first one you carry with anything left in it.
        internal Item ReviveKit(Player owner)
        {
            foreach (Item item in DogPantry.Sources(owner, false, false))
                if (DogVet.IsKit(item) && DogVet.Holds(item, 1) > 0) return item;
            return null;
        }

        /// <summary>Starts treating the dog. Null when it starts, otherwise why not.</summary>
        internal string Treat(Item item)
        {
            string problem = TreatProblem(item);
            if (problem != null) return problem;
            Player owner = CurrentPlayer;
            // As for planting or repairing: you have to be standing still.
            if (!(owner.CurrentState is IdlePlayerState)) return "Stop moving to treat " + Plugin.DogName.Value + ".";
            treatItem = item;
            treatStart = Time.time + TreatDelay; treatUntil = 0;
            roaming = false; hunt = null;
            // Out of the inventory (only if it's open: this call toggles it), so you see what you're doing.
            if (owner.IsInventoryOpened) owner.GetComponent<GamePlayerOwner>()?.CloseInventoryIfOpen();
            audio?.Play("whine");
            return null;
        }

        private void Kneel(Player owner)
        {
            treatStart = 0;
            string name = Plugin.DogName.Value;
            if (!(owner.CurrentState is IdlePlayerState)) { treatItem = null; Say("Stop moving to treat " + name + "."); return; }
            float time = Mathf.Clamp(2 + Mathf.Min(MissingPercent, DogVet.Holds(treatItem, 1)) * .06f, QuickestTreat, LongestTreat);
            treatUntil = Time.time + time + 1.5f;   // in case the game's state ends without saying
            owner.GetComponent<GamePlayerOwner>()?.ShowObjectivesPanel("Treating " + name + " {0:F1}", time);
            owner.CurrentManagedState.Plant(true, false, time, TreatmentEnded);
        }

        // From the kneeling state: true when its time ran out, false when it was called off.
        private void TreatmentEnded(bool finished)
        {
            Player owner = CurrentPlayer;
            GamePlayerOwner hands = owner != null ? owner.GetComponent<GamePlayerOwner>() : null;
            hands?.CloseObjectivesPanel();
            hands?.ClearInteractionState();
            if (treatItem == null || owner == null) return;
            if (finished) FinishTreatment(owner);
            else { treatItem = null; Say("Treatment stopped."); }
        }

        // The dog's side of it: it lies still and faces you. Called off by a fight (the kneeling is ended, which reports back).
        private bool TickTreatment(float dt, Player owner)
        {
            if (treatItem == null) return false;
            if (dead || hitClip != null || attackTarget != null || Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach + 1f)
            {
                StopTreatment();
                return false;
            }
            if (treatStart > 0 && Time.time >= treatStart) Kneel(owner);
            if (treatItem == null) return false;
            speed = shownSpeed = 0; lastProgress = Time.time;
            Vector3 look = owner.Transform.position - dog.transform.position; look.y = 0;
            if (stance == Stance.Stand && look.sqrMagnitude > .01f)
            {
                yaw = Mathf.MoveTowardsAngle(yaw, Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg, 360 * dt);
                dog.transform.rotation = Quaternion.Euler(0, yaw, 0);
            }
            if (stance == Stance.Stand || stance == Stance.Sitting || stance == Stance.SitDown) stance = Stance.LieDown;
            Animate(dt);
            if (treatStart <= 0 && Time.time >= treatUntil) TreatmentEnded(true);
            return true;
        }

        // Called off: the kneeling is ended, which reports back.
        private void StopTreatment()
        {
            if (treatItem == null) return;
            if (treatStart <= 0) CurrentPlayer?.CurrentManagedState?.Cancel();
            if (treatItem != null) TreatmentEnded(false);
        }

        private void FinishTreatment(Player owner)
        {
            Item item = treatItem;
            treatItem = null;
            string name = Plugin.DogName.Value;
            MedKitComponent kit = item?.GetItemComponent<MedKitComponent>();
            if (kit == null || item.CurrentAddress == null || (dead && !downed)) { Say("The kit is gone."); return; }
            // Reviving only gets it back on its feet: the rest takes another treatment.
            VetMath.Portion(kit.HpResource, kit.MaxHpResource, downed ? DogRules.RevivePercent : MissingPercent, 1, out float gives, out float use, out bool lastOfIt);
            if (gives <= 0) return;
            void Healed()
            {
                bool revived = downed;
                if (revived) GetUp();
                health = Mathf.Min(rules.Health, health + gives / 100f * rules.Health);
                // It came into the raid hurt or was hurt in it: this counts when it gets home too.
                if (needs != null && Plugin.CarryOverInjuries.Value) { needs.Health = Mathf.Clamp(health / rules.Health * 100f, 0, 100); NeedsStore.Save(); }
                audio?.Play("happy");
                Say(revived ? $"{name} is back on its feet, at {Mathf.RoundToInt(health / rules.Health * 100)}%." : $"{name} was treated (+{Mathf.RoundToInt(gives)}%): now at {Mathf.RoundToInt(health / rules.Health * 100)}%.");
            }
            if (lastOfIt)
            {
                DogPantry.Consume(owner, item, ok =>
                {
                    if (ok) { Healed(); return; }
                    Plugin.Log.LogWarning("The game wouldn't use up the K9 veterinary kit on the dog.");
                    Say("Couldn't use the K9 veterinary kit.");
                });
                return;
            }
            // Saved with everything else you carry when you get out.
            kit.HpResource -= use;
            item.RaiseRefreshEvent(false, false);
            Healed();
        }

        // Treated at home (DogVet): it's pleased, if it's about.
        internal void TreatedAtHome() { audio?.Play("happy"); }
    }

    /// <summary>
    /// The dog's hunger and thirst (100 = full, 0 = starving/parched), its health between raids and the two hideout dishes (0-100 each).
    /// Pure data and rules, so they can be validated and tested away from the game.
    /// Needs never hurt the dog: low needs only slow it down in combat mode.
    /// </summary>
    internal sealed class DogNeeds
    {
        public float Hunger = 100, Thirst = 100, FoodDish, WaterDish;
        // Health between raids, 0-100 % of the dog's full health (added in 0.16: wounds from a combat raid carry over).
        public float Health = 100;
        // Where the kennel stands in the hideout (scene coordinates), once you've placed it.
        public bool HomeSet;
        public float HomeX, HomeY, HomeZ, HomeYaw;
        // Put there by hand (moved from the overview with Hideout Extensions): it stays, even outside the kennel's room.
        public bool HomeByHand;
        // Which bag the dog wore before 0.14 (read once by the server mod to put that bag on the dog): "" = the first, "none" = none.
        public string VestId = "";

        public const float Hungry = 30f;           // below this the dog asks for food and (in combat) slows down
        public const float EatsFromDishBelow = 60f; // hideout: the dog goes to its dishes on its own
        public const float EatsFromBagBelow = 20f;  // raid: the dog eats from its own jacket on its own
        public const float HideoutPace = .5f;       // resting in the hideout uses less than a raid

        // Dog food, then meat as the alternative. Values are hunger points for a whole, unopened item.
        public const string DogFood = "6a5d0c0c4fb9a3c5e1d0d062", DogMeat = "6a5d0c0c4fb9a3c5e1d0d064";
        public static readonly Dictionary<string, float> Food = new Dictionary<string, float>
        {
            [DogFood] = 55,
            [DogMeat] = 40,
            ["6a5d0c0c4fb9a3c5e1d60011"] = 15,   // K9 training treats
            ["6a5d0c0c4fb9a3c5e1d60012"] = 100,  // K9 premium dog food
            ["635a758bfefc88a93f021b8a"] = 35,   // Salty Dog beef sausage
            ["57347da92459774491567cf5"] = 35,   // Can of beef stew (Large)
            ["57347d7224597744596b4e72"] = 30,   // Can of beef stew (Small)
            ["65815f0e647e3d7246384e14"] = 25,   // Pack of Tarker dried meat
        };

        // Plain water only. Values are thirst points for a full item.
        public static readonly Dictionary<string, float> Water = new Dictionary<string, float>
        {
            ["5448fee04bdc2dbc018b4567"] = 60,   // Bottle of water (0.6L)
            ["5c0fa877d174af02a012e1cf"] = 50,   // Aquamari water bottle with filter
            ["60098b1705871270cd5352a1"] = 40,   // Emergency Water Ration
            ["5d1b33a686f7742523398398"] = 100,  // Canister with purified water
        };

        // What an item is worth to the dog, given how much of it is left (0-1).
        public static float Value(string template, bool food, float remaining)
        {
            var table = food ? Food : Water;
            return table.TryGetValue(template, out float full) ? full * Clamp01(remaining) : 0;
        }

        public void Decay(float hours, float hungerPerHour, float thirstPerHour)
        {
            if (!(hours > 0)) return;
            Hunger = Clamp(Hunger - hours * Math.Max(0, hungerPerHour));
            Thirst = Clamp(Thirst - hours * Math.Max(0, thirstPerHour));
        }

        // Combat-mode speed (and bite pace) factor: 1 when fed and watered, down to 0.75 when either runs out.
        public float Fitness => 0.75f + 0.25f * Clamp01(Math.Min(Hunger, Thirst) / Hungry);

        // Eating or drinking from a dish takes what fits and leaves the rest in the dish.
        public void EatFromDish() { float take = Math.Min(FoodDish, 100 - Hunger); Hunger += take; FoodDish -= take; }
        public void DrinkFromDish() { float take = Math.Min(WaterDish, 100 - Thirst); Thirst += take; WaterDish -= take; }

        // Rejects anything that isn't a sane number; missing or broken values fall back to a fed dog with empty dishes.
        public DogNeeds Sanitized()
        {
            var copy = new DogNeeds { Hunger = Hunger, Thirst = Thirst, Health = Health, FoodDish = FoodDish, WaterDish = WaterDish, HomeSet = HomeSet, HomeX = HomeX, HomeY = HomeY, HomeZ = HomeZ, HomeYaw = HomeYaw, HomeByHand = HomeByHand, VestId = VestId };
            copy.Sanitize();
            return copy;
        }

        public void Sanitize()
        {
            Hunger = Sane(Hunger, 100); Thirst = Sane(Thirst, 100); Health = Sane(Health, 100); FoodDish = Sane(FoodDish, 0); WaterDish = Sane(WaterDish, 0);
            // A kennel position must be a real place; anything odd just means "not placed yet".
            if (!(Place(HomeX) && Place(HomeY) && Place(HomeZ) && !float.IsNaN(HomeYaw) && !float.IsInfinity(HomeYaw))) HomeSet = false;
            if (!HomeSet) { HomeX = HomeY = HomeZ = HomeYaw = 0; HomeByHand = false; }
            else HomeYaw = ((HomeYaw % 360) + 360) % 360;
            if (!ValidVest(VestId)) VestId = "";
        }

        public string ToJson()
            => string.Format(CultureInfo.InvariantCulture,
                "{{\"hunger\":{0:0.##},\"thirst\":{1:0.##},\"health\":{10:0.##},\"foodDish\":{2:0.##},\"waterDish\":{3:0.##},\"homeSet\":{4},\"homeX\":{5:0.###},\"homeY\":{6:0.###},\"homeZ\":{7:0.###},\"homeYaw\":{8:0.#},\"homeByHand\":{11},\"vestId\":\"{9}\"}}",
                Hunger, Thirst, FoodDish, WaterDish, HomeSet ? "true" : "false", HomeX, HomeY, HomeZ, HomeYaw, ValidVest(VestId) ? VestId : "", Health, HomeByHand ? "true" : "false");

        // Small, strict reader for the four numbers above; anything unexpected is ignored and then sanitised.
        public static DogNeeds FromJson(string json)
        {
            var needs = new DogNeeds { Hunger = float.NaN, Thirst = float.NaN, Health = float.NaN, FoodDish = float.NaN, WaterDish = float.NaN };
            if (string.IsNullOrEmpty(json)) return needs.Sanitized();
            needs.Hunger = Read(json, "hunger"); needs.Thirst = Read(json, "thirst"); needs.Health = Read(json, "health");
            needs.FoodDish = Read(json, "foodDish"); needs.WaterDish = Read(json, "waterDish");
            needs.HomeSet = json.Replace(" ", "").Contains("\"homeSet\":true");
            needs.HomeByHand = json.Replace(" ", "").Contains("\"homeByHand\":true");
            needs.VestId = ReadText(json, "vestId");
            needs.HomeX = Read(json, "homeX"); needs.HomeY = Read(json, "homeY"); needs.HomeZ = Read(json, "homeZ"); needs.HomeYaw = Read(json, "homeYaw");
            return needs.Sanitized();
        }

        private static float Read(string json, string key)
        {
            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return float.NaN;
            at = json.IndexOf(':', at);
            if (at < 0) return float.NaN;
            int end = at + 1;
            while (end < json.Length && "0123456789.-+eE ".IndexOf(json[end]) >= 0) end++;
            return float.TryParse(json.Substring(at + 1, end - at - 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : float.NaN;
        }

        public static bool ValidVest(string v)
        {
            if (v == null) return false;
            if (v.Length == 0 || v == "none") return true;
            if (v.Length != 24) return false;
            foreach (char c in v) if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))) return false;
            return true;
        }

        private static string ReadText(string json, string key)
        {
            int at = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (at < 0) return "";
            int open = json.IndexOf('"', json.IndexOf(':', at) + 1);
            int close = open < 0 ? -1 : json.IndexOf('"', open + 1);
            return open < 0 || close < 0 ? "" : json.Substring(open + 1, close - open - 1);
        }

        private static bool Place(float v) => !float.IsNaN(v) && !float.IsInfinity(v) && Math.Abs(v) < 20000;
        private static float Sane(float v, float fallback) => float.IsNaN(v) || float.IsInfinity(v) ? fallback : Clamp(v);
        private static float Clamp(float v) => Math.Max(0, Math.Min(100, v));
        private static float Clamp01(float v) => Math.Max(0, Math.Min(1, v));
    }

    /// <summary>
    /// Saves the dog's needs in your SPT profile data through the server mod, so they survive raids, game restarts
    /// and reinstalls of the client. A copy is also kept beside the plugin and used if the server mod can't be reached.
    /// Both sides validate the numbers.
    /// </summary>
    internal static class NeedsStore
    {
        private const string LoadRoute = "/k9companion/needs/load", SaveRoute = "/k9companion/needs/save";
        private static DogNeeds current;

        private static string LocalFile => Path.Combine(Plugin.AssetFolder, "needs.json");

        // Loaded once per game session; every dog (hideout or raid) shares the same state.
        internal static DogNeeds Current
        {
            get
            {
                if (current != null) return current;
                string json = null;
                try
                {
                    json = RequestHandler.PostJson(LoadRoute, "{}");
                    if (json == null || !json.Contains("\"hunger\"")) json = null;
                }
                catch (Exception e) { Plugin.Log.LogWarning("Couldn't load the dog's needs from the server mod: " + e.Message); }
                if (json == null && File.Exists(LocalFile))
                {
                    try { json = File.ReadAllText(LocalFile); Plugin.Log.LogInfo("Dog's needs loaded from the local copy."); }
                    catch (Exception e) { Plugin.Log.LogWarning("Couldn't read " + LocalFile + ": " + e.Message); }
                }
                current = DogNeeds.FromJson(json);
                Plugin.Log.LogInfo("Dog's needs: " + current.ToJson());
                return current;
            }
        }

        // The loaded state, without loading it (null until something has).
        internal static DogNeeds Peek => current;

        internal static void Save()
        {
            if (current == null) return;
            current.Sanitize();
            string json = current.ToJson();
            try { File.WriteAllText(LocalFile, json); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't write " + LocalFile + ": " + e.Message); }
            try { RequestHandler.PostJson(SaveRoute, json); }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't save the dog's needs to the server mod: " + e.Message); }
        }
    }

    /// <summary>
    /// Finds the dog's food and water in places it is allowed to come from, and uses it up with the same discard
    /// transaction the game uses (local in a raid, sent to the server in the hideout). Nothing is ever created.
    /// </summary>
    internal static class DogPantry
    {
        // The dog's own jacket first, then your gear, then (at home) your stash.
        internal static IEnumerable<Item> Sources(Player owner, bool hideout, bool bagOnly)
        {
            Inventory inventory = owner?.Profile?.Inventory;
            if (inventory == null) yield break;
            CompoundItem bag = hideout ? DogGear.Worn(owner.Profile) : DogBag.FindInRaid(owner);
            if (bag != null) foreach (Item item in bag.GetAllItems()) yield return item;
            if (bagOnly) yield break;
            if (inventory.Equipment != null) foreach (Item item in inventory.Equipment.GetAllItems()) yield return item;
            if (hideout && inventory.Stash != null) foreach (Item item in inventory.Stash.GetAllItems()) yield return item;
        }

        internal static float ValueOf(Item item, bool food)
        {
            FoodDrinkComponent portion = item.GetItemComponent<FoodDrinkComponent>();
            float left = portion != null && portion.MaxResource > 1 ? portion.RelativeValue : 1;
            return DogNeeds.Value(item.TemplateId.ToString(), food, left);
        }

        // The item that covers 'wanted' with the least waste; failing that, the biggest one there is.
        internal static Item Best(Player owner, bool hideout, bool food, float wanted, bool bagOnly = false)
        {
            Item best = null; float bestValue = 0; bool bestCovers = false;
            foreach (Item item in Sources(owner, hideout, bagOnly))
            {
                float value = ValueOf(item, food);
                if (value <= 0) continue;
                bool covers = value >= wanted;
                bool better = best == null || (covers && (!bestCovers || value < bestValue)) || (!covers && !bestCovers && value > bestValue);
                if (better) { best = item; bestValue = value; bestCovers = covers; }
            }
            return best;
        }

        // True while the item is somewhere you can hand it over from: your gear or the dog's jacket in a raid, or at home
        // also your stash.
        internal static bool Carried(Player owner, Item item)
        {
            Inventory inventory = owner?.Profile?.Inventory;
            ItemAddress at = item?.CurrentAddress;
            if (inventory == null || at == null) return false;
            // (Not Item.GetRootItem: that stops at the first grid, so for anything in a rig or a backpack it's the item itself.)
            if (at.IsChildOf(inventory.Equipment) || at.IsChildOf(inventory.QuestRaidItems)) return true;
            return owner is HideoutPlayer && (at.IsChildOf(inventory.Stash) || at.IsChildOf(inventory.QuestStashItems));
        }

        // Uses up what the dog needs ('wanted' points). From a multi-portion item in a raid only that share is taken, as
        // when you eat or drink part of it yourself (the game saves the remainder at extraction); a single portion, or the
        // last of one, goes with the game's own discard transaction. done(points given), 0 = nothing used.
        internal static void Consume(Player owner, Item item, bool food, float wanted, Action<float> done)
        {
            float value = ValueOf(item, food);
            FoodDrinkComponent portion = item.GetItemComponent<FoodDrinkComponent>();
            if (!(owner is HideoutPlayer) && portion != null && portion.MaxResource > 1 && portion.HpPercent > 0 && value > wanted + .5f && wanted > 0)
            {
                float perUnit = value / portion.HpPercent;
                float use = Mathf.Ceil(wanted / perUnit);
                if (portion.HpPercent - use >= .5f)
                {
                    portion.HpPercent -= use;
                    item.RaiseRefreshEvent(false, false);
                    done(use * perUnit);
                    return;
                }
            }
            Consume(owner, item, ok => done(ok ? value : 0));
        }

        internal static void Consume(Player owner, Item item, Action<bool> done) => Use(owner?.InventoryController, item, done);

        /// <summary>Uses an item up with the game's own discard transaction, through whichever inventory controller is at hand.</summary>
        internal static void Use(InventoryController controller, Item item, Action<bool> done)
        {
            if (controller == null || item == null) { done(false); return; }
            var removal = ItemManipulator.Remove(item, controller, simulate: true);
            if (removal.Failed) { done(false); return; }
            controller.TryRunNetworkTransaction(removal, result => done(result.Succeed));
        }

        // ---- at home (the DOG tab) ------------------------------------------------------------------------------

        // What the dog's care can come from out of a raid: your stash, the kennel's lockers, the dog's jacket and your gear.
        // The K9 veterinary kit in a raid (on you or in the dog's jacket) that covers 'wanted' percent with the least waste,
        // else the fullest.
        internal static Item BestKit(Player owner, float wanted)
        {
            Item best = null; float bestHolds = 0; bool bestCovers = false;
            foreach (Item item in Sources(owner, false, false))
            {
                if (!DogVet.IsKit(item)) continue;
                float holds = DogVet.Holds(item, 1);
                if (holds <= 0) continue;
                bool covers = holds >= wanted;
                if (best == null || (covers && (!bestCovers || holds < bestHolds)) || (!covers && !bestCovers && holds > bestHolds)) { best = item; bestHolds = holds; bestCovers = covers; }
            }
            return best;
        }

        internal static IEnumerable<Item> AtHome(Profile profile)
        {
            Inventory inventory = profile?.Inventory;
            if (inventory == null) yield break;
            var sources = new List<Item> { inventory.Stash, DogGear.Locker(profile, DogGear.ArmourLocker), DogGear.Locker(profile, DogGear.PremiumLocker),
                                           DogGear.Worn(profile), inventory.Equipment };
            foreach (Item root in sources)
                if (root != null) foreach (Item item in root.GetAllItems()) if (item != root) yield return item;
        }

        /// <summary>The item worth closest to 'wanted' (least waste), else the biggest; value 0 means "not this".</summary>
        internal static Item BestAtHome(Profile profile, Func<Item, float> value, float wanted)
        {
            Item best = null; float bestValue = 0; bool bestCovers = false;
            foreach (Item item in AtHome(profile))
            {
                float v = value(item);
                if (v <= 0) continue;
                bool covers = v >= wanted;
                if (best == null || (covers && (!bestCovers || v < bestValue)) || (!covers && !bestCovers && v > bestValue)) { best = item; bestValue = v; bestCovers = covers; }
            }
            return best;
        }
    }

    /// <summary>
    /// The K9 veterinary kit is the one thing that heals the dog, and it's never used on you: USE, HEAL ALL, its quick slot
    /// key, a double-click or dragging it onto your body all treat the dog. In a raid that's done kneeling beside the
    /// dog (Companion.Treat); at home at once, from anywhere the kit is.
    /// </summary>
    internal static class DogVet
    {
        internal const string Kit = "6a5d0c0c4fb9a3c5e1d60010";
        private const string UseRoute = "/k9companion/vet/use";
        private static readonly FieldInfo UiController = AccessTools.Field(typeof(ItemUiContext), "_inventoryController");

        internal static bool IsKit(Item item) => item != null && item.TemplateId.ToString() == Kit;

        internal static bool InRaid => Singleton<GameWorld>.Instantiated && !(Singleton<GameWorld>.Instance is HideoutGameWorld);

        internal static float Multiplier => K9.Kennel() >= 3 ? VetMath.StationBonus : 1f;

        // How much of the dog's health the kit can still restore, in percent.
        internal static float Holds(Item kit, float multiplier)
        {
            MedKitComponent medkit = kit?.GetItemComponent<MedKitComponent>();
            return medkit == null || medkit.MaxHpResource <= 0 ? 0 : medkit.HpResource / medkit.MaxHpResource * 100 * multiplier;
        }

        // Why the kit can't be used right now; null when it can.
        internal static string Problem(Item kit)
        {
            if (InRaid)
            {
                Companion dog = Companion.Raid;
                return dog == null ? "Your dog isn't with you." : dog.TreatProblem(kit);
            }
            if (!K9.Owned) return "The K9 veterinary kit is for a dog: you have none yet.";
            DogNeeds needs = NeedsStore.Current;
            if (needs == null) return "No dog.";
            if (needs.Health >= 99.5f) return Plugin.DogName.Value + " is in good health.";
            return Holds(kit, 1) <= 0 ? "This kit is empty." : null;
        }

        // Uses the kit on the dog however it was asked for. Tells you if it can't.
        internal static void Use(Item kit)
        {
            try
            {
                string problem = Problem(kit);
                if (problem == null && InRaid) problem = Companion.Raid.Treat(kit);
                else if (problem == null) UseAtHome(kit, ControllerFor(kit), said => NotificationManager.DisplayMessageNotification(said));
                if (problem != null) NotificationManager.DisplayWarningNotification(problem);
            }
            catch (Exception e) { Plugin.Log.LogError("K9 veterinary kit: " + e); }
        }

        private static InventoryController ControllerFor(Item kit)
            => kit.Owner as InventoryController ?? UiController?.GetValue(ItemUiContext.Instance) as InventoryController;

        /// <summary>
        /// At home it's done at once. The last of a kit goes with the game's own discard; otherwise the server takes what was
        /// used off the kit, so it's still there, with less in it, next time you log in.
        /// </summary>
        internal static void UseAtHome(Item kit, InventoryController controller, Action<string> said)
        {
            DogNeeds needs = NeedsStore.Current;
            MedKitComponent medkit = kit.GetItemComponent<MedKitComponent>();
            if (needs == null || medkit == null) { said("No dog."); return; }
            VetMath.Portion(medkit.HpResource, medkit.MaxHpResource, 100 - needs.Health, Multiplier, out float gives, out float use, out bool lastOfIt);
            if (gives <= 0) { said(Plugin.DogName.Value + " is in good health."); return; }
            void Healed()
            {
                needs.Health = Mathf.Min(100, needs.Health + gives);
                NeedsStore.Save();
                Companion.Current?.TreatedAtHome();
                said($"{Plugin.DogName.Value} was treated (+{Mathf.RoundToInt(gives)}%): now at {Mathf.RoundToInt(needs.Health)}%.");
            }
            if (lastOfIt)
            {
                DogPantry.Use(controller, kit, ok => { if (ok) Healed(); else said("Couldn't use the K9 veterinary kit."); });
                return;
            }
            string reply = null;
            try { reply = RequestHandler.PostJson(UseRoute, new JObject { ["id"] = kit.Id.ToString(), ["amount"] = use }.ToString(Newtonsoft.Json.Formatting.None)); }
            catch (Exception e) { Plugin.Log.LogWarning("K9 veterinary kit: " + e.Message); }
            JObject answer = null;
            try { if (!string.IsNullOrEmpty(reply)) answer = JObject.Parse(reply); }
            catch (Exception) { }
            if (answer == null || answer["ok"]?.Value<bool>() != true)
            {
                Plugin.Log.LogWarning("The server wouldn't use the K9 veterinary kit. Reply: " + reply);
                said("Couldn't use the K9 veterinary kit: restart the SPT server if you've just updated the mod.");
                return;
            }
            medkit.HpResource = answer["left"]?.Value<float>() ?? medkit.HpResource - use;
            kit.RaiseRefreshEvent(false, false);
            Healed();
        }
    }

    /// <summary>
    /// Dog settings the owner of the SPT server has fixed for everyone on it, from the server mod's config.jsonc: with Fika
    /// that's everyone in the raid, as they all use the same server. Anything left out stays each player's own (F12).
    /// </summary>
    internal static class ServerRules
    {
        private const string Route = "/k9companion/settings";
        internal static bool? CombatMode;
        internal static DogApproach? Approach;
        internal static Aggression? Aggression;
        internal static bool? SoloOnly, DownedMode;
        internal static float? EngageDistance, OrderRange, BitesToKill, BossBitesToKill, BiteCooldown, BiteStagger, DogHealth, AutoRushRange, DefendRange, BleedOutTime, CarryLimit;
        private static string applied = "{}";
        private static bool complained;

        // Asked for along with the K9 state: on the raid screens and just before a raid.
        internal static void Refresh()
        {
            try { Apply(RequestHandler.PostJson(Route, "{}")); }
            catch (Exception e)
            {
                if (!complained) Plugin.Log.LogWarning("Couldn't read the server's dog settings (restart the SPT server if you've just updated the mod): " + e.Message);
                complained = true;
            }
        }

        private static void Apply(string json)
        {
            if (string.IsNullOrEmpty(json) || json == applied) return;
            JObject s;
            try { s = JObject.Parse(json); }
            catch (Exception) { return; }   // a server too old to have them answers with something else
            applied = json;
            CombatMode = s["combatMode"]?.Value<bool?>();
            Approach = Enum.TryParse(s["approach"]?.Value<string>(), true, out DogApproach how) ? how : (DogApproach?)null;
            AutoRushRange = s["autoRushRange"]?.Value<float?>();
            DefendRange = s["defendRange"]?.Value<float?>();
            string aggression = s["aggression"]?.Value<string>();
            Aggression = Enum.TryParse(aggression, true, out Aggression level) ? level : (Aggression?)null;
            EngageDistance = s["engageDistance"]?.Value<float?>();
            OrderRange = s["orderRange"]?.Value<float?>();
            BitesToKill = s["bitesToKill"]?.Value<float?>();
            BossBitesToKill = s["bossBitesToKill"]?.Value<float?>();
            BiteCooldown = s["biteCooldown"]?.Value<float?>();
            BiteStagger = s["biteStagger"]?.Value<float?>();
            DogHealth = s["dogHealth"]?.Value<float?>();
            SoloOnly = s["soloOnly"]?.Value<bool?>();
            DownedMode = s["downedMode"]?.Value<bool?>();
            BleedOutTime = s["bleedOutTime"]?.Value<float?>();
            CarryLimit = s["carryLimit"]?.Value<float?>();
            Plugin.Log.LogInfo(s.Count == 0 ? "Server's dog settings: none, every player uses their own." : "Server's dog settings (for everyone): " + json);
        }
    }
}
