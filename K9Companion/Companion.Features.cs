using System;
using EFT;
using EFT.InventoryLogic;
using UnityEngine;

namespace K9Companion
{
    public sealed partial class Companion
    {
        private enum Command { Follow, Stay, Sit, Down }
        private Command command;
        private bool crouching, navigationAvailable = true;
        private float nextVestCheck;
        private float ownerSpeed, nextWhine, actionUntil, messageUntil;
        private string message = "", actionClip;
        private CompanionDecor decor;
        private CompanionAudio audio;

        private string IdleClip(string prefix, float elapsed)
        {
            // Long dwell times keep variants from restarting every frame.
            int variant = ((int)(elapsed / 22)) % 3;
            return prefix + "_0" + variant;
        }

        private void WakeUp()
        {
            if (stance == Stance.Sitting || stance == Stance.SitDown) stance = Stance.StandUp;
            else if (stance == Stance.Lying || stance == Stance.LieDown) stance = Stance.LieUp;
            else if (stance == Stance.Sleeping || stance == Stance.SleepDown) stance = Stance.Wake;
        }

        private void InitFeatures(Player owner)
        {
            Current = this;
            decor = prepared?.Decor ?? new CompanionDecor(dog.transform, model);
            ApplyCosmetics();
            audio = new CompanionAudio(dog);
            prepared = null;
            nextWhine = Time.time + UnityEngine.Random.Range(240f, 540f);
            InitNeeds();
            // The bed and bowls are the kennel's: a new handler has them once the Makeshift Kennel is built.
            if (IsHideout) { kennelLevel = K9.Kennel(); if (kennelLevel >= 1) SetUpKennel(); else Plugin.Log.LogInfo("No kennel built yet: the dog has no bed or bowls."); }
            else { InitCombat(); dog.gameObject.AddComponent<DogCam>().Dog = this; }
        }

        // Where the dog's camera looks from in a raid (DogCam), or null when it wears none: the camera vest's own first,
        // then the clip-on camera on whatever jacket it wears.
        internal Transform CameraMount()
        {
            if (IsHideout || decor == null) return null;
            string vest = decor.VestLook;
            if (vest != null && DogGear.BuiltInCamera.Contains(vest)) return decor.Mount("vest");
            if (DogGear.RaidCamera != null && vest != null) return decor.Mount("clip");
            return null;
        }

        internal void Say(string text) { message = text; messageUntil = Time.unscaledTime + 5; }
        private void SetCommand(Command value)
        {
            command = value; actionClip = null; idle = 0; CancelTasks(); roaming = false; bedTrip = false;
            corners = Array.Empty<Vector3>(); corner = 0; nextPath = 0; speed = 0;
            following = false; lastProgress = Time.time;
            if (value == Command.Follow) WakeUp();
        }

        private void Commands(Player owner, bool hideout)
        {
            // Specific modified bindings take precedence over their unmodified equivalents.
            if (Plugin.Feed.Value.IsDown()) { Care(owner, hideout, true); return; }
            if (Plugin.Water.Value.IsDown()) { Care(owner, hideout, false); return; }
            if (Plugin.Lie.Value.IsDown()) { if (hideout) GoToBed(); else { SetCommand(Command.Down); Say("Lie down."); } return; }
            if (Plugin.Recall.Value.IsDown())
            {
                Recall(owner, hideout); return;
            }
            if (Plugin.Stay.Value.IsDown())
            {
                SetCommand(command == Command.Follow ? Command.Stay : Command.Follow);
                Say(command == Command.Stay ? "Stay." : "Follow me.");
                if (command == Command.Follow) K9.Report("follow");
                return;
            }
            if (Plugin.Pet.Value.IsDown()) { PetDog(owner); return; }
            if (Plugin.Rename.Value.IsDown())
            {
                if (Vector3.Distance(owner.Transform.position, dog.transform.position) > 3) { Say("Move closer to rename your dog."); return; }
                RenameDialog.Open(this);
            }
        }

        private void Care(Player owner, bool hideout, bool feed)
        {
            if (!hideout || decor.Home == null) { Say("Food and water are available at the hideout bed."); return; }
            if (Vector3.Distance(owner.Transform.position, decor.Home.position) > 3)
            { Say("Move closer to the dishes."); return; }
            if (!NeedsOn) { Say("Survival needs are off."); return; }
            RefillDish(owner, feed, false);
        }

        private void TickFeatures(float dt, Player owner, bool hideout)
        {
            // Before the kennel is built the icon stands over the dog (opening the kennel's screen, where level 1 is built).
            if (hideout) KennelIcon.Tick(kennelLevel >= 1 ? decor?.Home : dog?.transform, kennelLevel, this);
            // A kennel level finished while you're in the hideout shows at once.
            if (hideout && Time.time >= nextKennelCheck)
            {
                nextKennelCheck = Time.time + 2;
                int level = K9.Kennel();
                if (level != kennelLevel)
                {
                    Plugin.Log.LogInfo($"Kennel is now level {level}.");
                    bool first = kennelLevel < 1 && level >= 1;
                    kennelLevel = level;
                    if (first) SetUpKennel(); else KennelLevelChanged();
                }
            }
            TickNeeds(dt, owner, hideout);
            if (Time.time >= nextVestCheck)
            {
                nextVestCheck = Time.time + 1;
                // At home: the jacket in the dog's slot (DOG tab); in a raid: the jacket that came along.
                CompoundItem jacket = hideout ? DogGear.Worn(owner.Profile) : DogBag.FindInRaid(owner);
                decor?.SetVest(DogGear.Look(jacket));
                decor?.SetHead(hideout ? DogGear.HeadLook(owner.Profile) : DogGear.RaidHead);
                decor?.SetCamera(hideout ? DogGear.CameraLook(owner.Profile) != null : DogGear.RaidCamera != null);
                decor?.SetCosmetics(hideout ? DogGear.CollarStyle(owner.Profile) : DogGear.RaidCollar);
                // Heavier armour slows the dog in a raid, and so does more in its jacket than the carry limit.
                jacketSpeed = hideout || jacket == null ? 1 : (DogArmour.Of(jacket)?.Speed ?? 1) * DogRules.CarrySpeed(jacket.TotalWeight, Plugin.CarryLimitNow);
            }
            if (Time.time >= nextWhine)
            {
                nextWhine = Time.time + UnityEngine.Random.Range(240f, 540f);
                if (Plugin.RareWhine.Value && !crouching && shownSpeed < .15f && actionClip == null)
                { audio.Play("whine"); Say(Plugin.DogName.Value + " whines quietly."); }
            }
        }

        // Petting (its key, or "Pet" when you look at the dog): you reach out, it sits for you a moment. Its orders stay as
        // they were: a following dog gets up and follows again once you move off.
        internal void PetDog(Player owner)
        {
            if (dog == null || owner == null) return;
            if (dead) { Say(DeadNote + "."); return; }
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > 2.5f) { Say("Move closer to pet your dog."); return; }
            Reach(owner);
            WakeUp(); roaming = false; actionClip = "Sitting_02"; actionUntil = Time.time + 7;
            audio.Play("happy"); Say("Good dog, " + Plugin.DogName.Value + ".");
        }

        // Your hand goes out to the dog: the game's own push-a-door reach (the hand that pushes the door open).
        internal static void Reach(Player owner)
        {
            try { owner?.MovementContext?.SetInteractInHands(EInteraction.DoorPushForward); }
            catch (Exception e) { Plugin.Log.LogWarning("Hand animation for the dog: " + e.Message); }
        }

        private bool TickAction(float dt, Player owner)
        {
            if (actionClip != null)
            {
                speed = shownSpeed = 0; lastProgress = Time.time;
                if (Time.time > actionUntil || Vector3.Distance(owner.Transform.position, dog.transform.position) > 3)
                { actionClip = null; stance = Stance.Sitting; return false; }
                if (stance != Stance.Stand && stance != Stance.Sitting && stance != Stance.SitDown)
                { Animate(dt); return true; }
                if (stance == Stance.Stand) stance = Stance.SitDown;
                if (stance == Stance.SitDown) Animate(dt);
                else model.Tick(actionClip, 1, dt);
                return true;
            }
            return false;
        }

        private void LateUpdate() { if (dog != null) { decor?.Update(); UpdateHitboxes(); TickBreathing(); if (!IsHideout) TickNet(); } }

        // Fika: what other players' games need to show this dog.
        private float nextState, nextLooks;
        private void TickNet()
        {
            if (!DogNet.Online || Owner == null) return;
            if (Time.time >= nextLooks)
            {
                nextLooks = Time.time + 5;
                DogNet.SendLooks(new DogLooks { Vest = decor?.VestLook, Head = DogGear.RaidHead, Collar = DogGear.RaidCollar, Camera = DogGear.RaidCamera != null, Scale = Plugin.Scale.Value });
            }
            bool still = shownSpeed < .05f && !dead;
            if (Time.time < nextState) return;
            nextState = Time.time + (still ? .25f : .08f);
            DogNet.SendState(new DogState
            {
                Position = dog.transform.position, Yaw = yaw, Pitch = bodyPitch, LookYaw = model.LookYaw, LookPitch = model.LookPitch,
                Clip = (short)DogModel.ClipNumber(model.Current), Layer = (short)DogModel.ClipNumber(model.Layer), LayerWeight = model.LayerWeight, Playback = model.Playback,
                Starts = (byte)model.Starts, Looping = model.Looping, Walking = model.Walking, Low = Low, Resting = Resting, Targetable = Targetable, Dead = dead
            });
        }
        // Only short status messages (bottom left); the commands live in the game's Y menu under DOG.
        private void OnGUI()
        {
            if (dog == null || CurrentPlayer == null || CurrentPlayer.IsInventoryOpened) return;
            if (Time.unscaledTime < messageUntil) GUI.Box(new Rect(20, Screen.height - 90, 460, 40), message);
        }
        private int kennelLevel;
        private float nextKennelCheck;

        /// <summary>The collar in the dog's collar slot (DOG tab); in a raid, the one it came with.</summary>
        internal void ApplyCosmetics() => decor?.SetCosmetics(IsHideout ? DogGear.CollarStyle(K9.SessionProfile) : DogGear.RaidCollar);

        private void CleanupFeatures() { if (Current == this) Current = null; CancelMeal(); NeedsStore.Save(); CleanupCombat(); if (RenameDialog.Owner == this) RenameDialog.Close(); decor?.Dispose(); decor = null; audio?.Dispose(); audio = null;  }
    }
}
