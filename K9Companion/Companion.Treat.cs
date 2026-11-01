using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace K9Companion
{
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
            if (dead) return name + " is dead.";
            if (treatItem != null) return "You're already treating " + name + ".";
            if (health >= rules.Health - .5f) return name + " isn't hurt.";
            if (DogVet.Holds(item, 1) <= 0) return "This kit is empty.";
            if (meal != MealSource.None) return name + " is eating.";
            if (attackTarget != null || hitClip != null) return name + " is fighting.";
            if (!DogPantry.Carried(owner, item)) return "Take the kit first.";
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > HandReach) return "Call " + name + " over first.";
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
            if (kit == null || item.CurrentAddress == null || dead) { Say("The kit is gone."); return; }
            VetMath.Portion(kit.HpResource, kit.MaxHpResource, MissingPercent, 1, out float gives, out float use, out bool lastOfIt);
            if (gives <= 0) return;
            void Healed()
            {
                health = Mathf.Min(rules.Health, health + gives / 100f * rules.Health);
                // It came into the raid hurt or was hurt in it: this counts when it gets home too.
                if (needs != null && Plugin.CarryOverInjuries.Value) { needs.Health = Mathf.Clamp(health / rules.Health * 100f, 0, 100); NeedsStore.Save(); }
                audio?.Play("happy");
                Say($"{name} was treated (+{Mathf.RoundToInt(gives)}%): now at {Mathf.RoundToInt(health / rules.Health * 100)}%.");
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
}
