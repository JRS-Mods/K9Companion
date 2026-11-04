using EFT;
using EFT.InventoryLogic;
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
}
