using System.Collections.Generic;
using System.Linq;
using System;
using Comfort.Common;
using EFT.Communications;
using EFT.HealthSystem;
using EFT.InputSystem;
using EFT.Interactive;
using EFT.InventoryLogic;
using EFT.UI.Gestures;
using EFT.UI;
using EFT.Utilities;
using EFT;
using HarmonyLib;
using UnityEngine.AI;
using UnityEngine.UI;
using UnityEngine;
using Object = UnityEngine.Object;
using Random = UnityEngine.Random;

namespace K9Companion
{
    public sealed partial class Companion
    {
        // The dog the command wheel talks to (raid or hideout); null when there is none.
        internal static Companion Current;

        internal bool CanTakeOrders
        {
            get
            {
                Player owner = CurrentPlayer;
                return dog != null && !dead && !failed && owner != null && owner.HealthController != null &&
                    owner.HealthController.IsAlive && !owner.IsInventoryOpened && !RenameDialog.IsOpen;
            }
        }

        // What a command's line in the menu says, where that changes: how much it needs, what it's set to.
        internal string NeedsLabelFor(DogOrder order)
        {
            switch (order)
            {
                case DogOrder.Feed: case DogOrder.Water: return NeedsLabel(order);
                case DogOrder.AttackMode: return "Attack: " + AttackName(Plugin.Aggressiveness);
                case DogOrder.ApproachMode: return "Approach: " + Plugin.Approaching.ToString().ToLowerInvariant();
                default: return null;
            }
        }

        private static string AttackName(Aggression level)
            => level == Aggression.Passive ? "on order" : level == Aggression.Defensive ? "defends you" : "any enemy";

        internal bool Available(DogOrder order, out string why)
        {
            if (order == DogOrder.OpenBag) why = BagUnavailable();
            else if (order == DogOrder.Feed || order == DogOrder.Water) why = NeedsUnavailable(order);
            else if (order == DogOrder.AttackMode) why = !Plugin.Combat ? "needs combat mode" : ServerRules.Aggression != null ? "set by the server" : null;
            else if (order == DogOrder.ApproachMode) why = !Plugin.Combat ? "needs combat mode" : ServerRules.Approach != null ? "set by the server" : null;
            else why = dog == null ? "No dog" : dead ? DeadNote : rules.Unavailable(order, IsHideout);
            return why == null;
        }

        // The jacket can be opened from the dog's side, alive or dead.
        private string BagUnavailable()
        {
            Player owner = CurrentPlayer;
            if (dog == null || owner == null) return "No dog";
            if (IsHideout) return "Use the DOG tab";
            if (DogBag.FindInRaid(owner) == null) return "No jacket this raid";
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > 2.5f) return "Get closer to the dog";
            return null;
        }

        internal void Order(DogOrder order)
        {
            Player owner = CurrentPlayer;
            if (owner == null) return;
            if (order == DogOrder.Feed || order == DogOrder.Water)
            {
                if (Available(order, out string missing)) OrderMeal(owner, order == DogOrder.Feed); else Say(missing + ".");
                return;
            }
            if (order == DogOrder.OpenBag)
            {
                string problem = Available(order, out string reason) ? DogBag.Open(owner) : reason + ".";
                if (problem != null) Say(problem);
                return;
            }
            if (!Available(order, out string why) || !CanTakeOrders) { Say((why ?? "Not now") + "."); return; }
            switch (order)
            {
                case DogOrder.OverThere: OrderAttack(owner); break;
                case DogOrder.ComeHere: Plugin.Log.LogInfo("Come here."); Recall(owner, IsHideout); break;
                case DogOrder.BringHere: BringHere(owner); break;
                case DogOrder.Sit: OrderSit(); break;
                case DogOrder.Follow: SetCommand(Command.Follow); Say("Follow me."); K9.Report("follow"); break;
                case DogOrder.LieDown: SetCommand(Command.Down); Say("Lie down."); break;
                case DogOrder.GoToBed: GoToBed(); break;
                case DogOrder.Stay: SetCommand(Command.Stay); Say("Stay."); break;
                case DogOrder.AttackMode: NextAttackMode(); break;
                case DogOrder.ApproachMode: NextApproach(); break;
            }
        }

        // Each pick moves on to the next setting, and the menu's line says what it's on. Saved like the DOG tab's.
        private void NextAttackMode()
        {
            Aggression next = (Aggression)(((int)Plugin.DogAggression.Value + 1) % 3);
            Plugin.DogAggression.Value = next;
            rules = Plugin.Rules(IsHideout);
            // Told to wait for orders: it drops a fight it picked itself (one you ordered goes on).
            if (next == Aggression.Passive && attackTarget != null && (!brain.Ordered || defending)) StopFight();
            Say(Plugin.DogName.Value + (next == Aggression.Passive ? " attacks on your order." : next == Aggression.Defensive ? " defends you: it goes for whoever shoots at you." : " goes for any enemy it detects."));
        }

        private void NextApproach()
        {
            DogApproach next = (DogApproach)(((int)Plugin.ApproachMode.Value + 1) % 3);
            Plugin.ApproachMode.Value = next;
            Say(Plugin.DogName.Value + (next == DogApproach.Auto ? ": Auto approach, straight at anything close, stalking the rest."
                : next == DogApproach.Stealth ? ": Stealth approach, working its way in out of sight." : ": Direct approach, straight at them."));
        }

        // Same as the Recall key: drops any attack, ends sit/stay/lie, and follows again. For the next 30 s it starts no
        // fight of its own (Defending you or Any enemy), so you can get it back while you're under fire; Over There still works.
        private void Recall(Player owner, bool hideout)
        {
            bool fighting = attackTarget != null || brain.State != CombatState.Idle;
            SetCommand(Command.Follow); CallOff(); audio.Play("whistle", true);
            // In the hideout the dog walks over when it can find a way, and otherwise comes straight to you.
            if (hideout && !Reachable(owner.Transform.position) && FindPoint(owner.Transform.position, out Vector3 p)) Teleport(p, owner.Transform.position);
            Say(fighting ? $"Come here! {Plugin.DogName.Value} breaks off and stays with you for {CallOffTime:0} s." : "Come here!");
            K9.Report("come");
        }

        // Put straight in front of you (stuck indoors, lost on the far side of a fence) and following again. Not while
        // it's dead or downed: it stays where it fell.
        internal void BringHere(Player owner)
        {
            if (dog == null || owner == null) return;
            if (dead) { Say(DeadNote + "."); return; }
            Vector3 at = owner.Transform.position;
            if (!FindPoint(at, out Vector3 point, owner.LookDirection)) { Say("No room in front of you for " + Plugin.DogName.Value + "."); return; }
            SetCommand(Command.Follow);
            CallOff();
            Teleport(point, at);
            audio?.Play("whistle", true);
            Say(Plugin.DogName.Value + " is here.");
        }

        /// <summary>
        /// Off a killed dog, for good: its jacket (and what's in it) into your own gear, its head gear and camera with it.
        /// If you get out alive the server puts it all back on the dog; if you die it's lost with the rest of your gear.
        /// </summary>
        internal void TakeGear(Player owner)
        {
            if (!Killed || owner == null || IsHideout) return;
            if (DogBag.GearTaken) { Say("You've already taken its gear."); return; }
            if (Vector3.Distance(owner.Transform.position, dog.transform.position) > 3f) { Say("Get closer to the dog."); return; }
            CompoundItem jacket = DogBag.FindInRaid(owner);
            bool kit = DogGear.RaidHead != null || DogGear.RaidCamera != null;
            if (jacket == null && !kit) { Say(Plugin.DogName.Value + " wore no gear this raid."); return; }
            string name = Plugin.DogName.Value;
            string jacketName = jacket?.LocalizedName();
            void Taken()
            {
                DogBag.TookGear();
                decor?.SetVest(null); decor?.SetHead(null); decor?.SetCamera(false);
                string what = jacket != null ? jacketName + (DogGear.RaidHead != null ? " and head gear" : "") : "head gear";
                Say($"You take {name}'s {what}. Get out alive and it's back on the dog.");
            }
            if (jacket == null) { Taken(); return; }
            InventoryController controller = owner.InventoryController;
            if (controller == null) { Say("Can't take its gear right now."); return; }
            var move = ItemManipulator.QuickFindAppropriatePlace(jacket, controller,
                new CompoundItem[] { controller.Inventory.Equipment }, ItemManipulator.EMoveItemOrder.PickUp, simulate: true);
            if (move.Failed) { Say("No room in your gear for its " + jacketName + ". Make room, or empty it first."); return; }
            controller.TryRunNetworkTransaction(move, result =>
            {
                if (result.Succeed) Taken();
                else Say("Its jacket couldn't be taken: " + result.Error);
            });
        }

        private void OrderSit()
        {
            SetCommand(Command.Sit);
            // Skip the usual wait: the dog sits as soon as it has stopped.
            idle = SitAfter + .1f;
            Say("Sit.");
            K9.Report("sit");
        }

        // Over there: the enemy you aim at, or the one nearest where you aim (through a scope at 40 m or more, say), up to
        // the ordered attack range; with nobody there, the dog runs to where you aim and hunts around it.
        private void OrderAttack(Player owner)
        {
            calledOffUntil = 0;
            Player target = AimedEnemy(owner, out string problem, out Vector3? aimed);
            if (!rules.CanAttack) target = null;   // without combat mode: a "go there"
            Plugin.Log.LogInfo($"Over there: target {(target != null ? target.Profile.Nickname : "none")}, aimed at {(aimed.HasValue ? aimed.Value.ToString() : "nothing")}" + (target == null ? $" ({problem})" : "") + ".");
            // Standing up from a sit or stay is part of obeying; the dog only bites once it is on its feet.
            if (target != null)
            {
                SetCommand(Command.Follow);
                hunt = null;
                Engage(target, true);
                audio.Play("bark");
                Say($"Over there! {(stalking ? "Stalking" : "Going for")} {target.Profile.Nickname} ({Mathf.RoundToInt(Vector3.Distance(owner.Transform.position, target.Transform.position))} m).");
                return;
            }
            if (aimed is Vector3 point && StartHunt(owner, point, out problem)) return;
            Say(problem);
        }

        // The hostile AI under the crosshair or, failing that, the one nearest the line you aim along (within a few
        // degrees), even out of sight: the dog tracks by scent. Allies, your group and anything out of range or reach are
        // refused. Also gives where the aim meets the world, for a hunt.
        private Player AimedEnemy(Player owner, out string problem, out Vector3? aimed)
        {
            problem = "Aim at a hostile, or where to hunt, and try again."; aimed = null;
            Transform camera = owner.CameraPosition;
            if (camera == null || world == null) return null;
            float range = Plugin.OrderReach;
            RaycastHit[] hits = Physics.SphereCastAll(camera.position, .3f, camera.forward, range + 20, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            Player target = null;
            foreach (RaycastHit hit in hits)
            {
                if (hit.collider.GetComponentInParent<DogHitbox>() != null || hit.collider.GetComponentInParent<Companion>() != null) continue;
                Player p = world.GetPlayerByCollider(hit.collider) ?? hit.collider.GetComponentInParent<Player>();
                if (p == owner) continue;
                if (p != null) { target = p; break; }
                if (hit.distance > .6f) { aimed = hit.point; break; }
            }
            if (aimed == null && target == null) aimed = camera.position + camera.forward * Mathf.Min(range, 60);
            if (target != null)
            {
                BotOwner bot = target.IsAI ? target.AIData?.BotOwner : null;
                bool hostBot = DogNet.IsRemoteBot(target);
                if ((!hostBot && (bot == null || bot.IsDead)) || !target.HealthController.IsAlive) { problem = "That isn't a valid target."; return null; }
                if (!IsEnemy(target, owner)) { problem = "The dog won't attack a friendly."; return null; }
                aimed = target.Transform.position;
                if (Vector3.Distance(target.Transform.position, owner.Transform.position) > range)
                { problem = $"Too far away (limit {Mathf.RoundToInt(range)} m)."; return null; }
                if (!Reachable(target.Transform.position)) { problem = "The dog can't reach that target."; return null; }
                return target;
            }
            // Nobody right under the crosshair: the enemy closest to the line of aim.
            Player best = null; float bestAngle = 6f;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                Vector3 to = Body(p) - camera.position;
                if (to.magnitude > range) continue;
                float angle = Vector3.Angle(camera.forward, to);
                if (angle >= bestAngle || !Reachable(p.Transform.position)) continue;
                best = p; bestAngle = angle;
            }
            return best;
        }
    }

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
            else { InitCombat(); dog.gameObject.AddComponent<DogCam>().Dog = this; dog.gameObject.AddComponent<DogHud>().Dog = this; }
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

        internal void Say(string text) { message = Lang.T(text); messageUntil = Time.unscaledTime + 5; }
        private void SetCommand(Command value)
        {
            command = value; actionClip = null; idle = 0; CancelTasks(); roaming = false; bedTrip = false;
            corners = Array.Empty<Vector3>(); corner = 0; nextPath = 0; speed = 0;
            following = false; lastProgress = Time.time;
            if (value == Command.Follow) WakeUp();
        }

        private void Commands(Player owner, bool hideout)
        {
            // An F-key you've bound to something in the game's own Y-menu binds is the game's (see GameBinds).
            bool Pressed(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> key) => GameBinds.Pressed(key, Say, owner.Side == EPlayerSide.Savage);
            // Specific modified bindings take precedence over their unmodified equivalents.
            if (Pressed(Plugin.Feed)) { Care(owner, hideout, true); return; }
            if (Pressed(Plugin.Water)) { Care(owner, hideout, false); return; }
            if (Pressed(Plugin.Lie)) { if (hideout) GoToBed(); else { SetCommand(Command.Down); Say("Lie down."); } return; }
            if (Pressed(Plugin.Bring)) { BringHere(owner); return; }
            if (Pressed(Plugin.Recall))
            {
                Recall(owner, hideout); return;
            }
            if (Pressed(Plugin.Stay))
            {
                SetCommand(command == Command.Follow ? Command.Stay : Command.Follow);
                Say(command == Command.Stay ? "Stay." : "Follow me.");
                if (command == Command.Follow) K9.Report("follow");
                return;
            }
            if (Pressed(Plugin.Pet)) { PetDog(owner); return; }
            if (Pressed(Plugin.Rename))
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
        // Only short status messages; the commands live in the game's Y menu under DOG. Along the very bottom of the
        // screen, just right of where Fika writes its version, as wide as the message: bottom left is your stance and
        // stamina, and the middle of the screen is for seeing.
        private GUIContent shownMessage;
        private void OnGUI()
        {
            if (dog == null || CurrentPlayer == null || CurrentPlayer.IsInventoryOpened) return;
            if (Time.unscaledTime >= messageUntil) return;
            if (shownMessage == null || shownMessage.text != message) shownMessage = new GUIContent(message);
            Vector2 size = GUI.skin.box.CalcSize(shownMessage);
            float width = Mathf.Min(size.x + 24, Screen.width * .6f), height = Mathf.Max(26, size.y + 6);
            GUI.Box(new Rect(Screen.width * .26f, Screen.height - height - Screen.height * .015f, width, height), shownMessage);
        }
        private int kennelLevel;
        private float nextKennelCheck;

        /// <summary>The collar in the dog's collar slot (DOG tab); in a raid, the one it came with.</summary>
        internal void ApplyCosmetics() => decor?.SetCosmetics(IsHideout ? DogGear.CollarStyle(K9.SessionProfile) : DogGear.RaidCollar);

        private void CleanupFeatures() { if (Current == this) Current = null; CancelMeal(); NeedsStore.Save(); CleanupCombat(); if (RenameDialog.Owner == this) RenameDialog.Close(); decor?.Dispose(); decor = null; audio?.Dispose(); audio = null;  }
    }

    /// <summary>
    /// A hunt (Over There with nobody to aim at): the dog runs to where you aimed, up to the ordered attack range, and
    /// searches the area by sight and scent. It picks the places worth checking within 20 m (inside buildings, behind
    /// cover, other floors), spread apart, and works through them nearest first, stopping at each to sniff, then comes
    /// back to you. The first enemy it picks up on the way, at the spot or during the search, it goes for (as if you'd
    /// ordered it). Without combat mode it just goes there and waits.
    /// Where you aim is often not ground the dog can walk (a wall, a roof, a fence): the nearest walkable spot it can
    /// reach is found back along your line of aim.
    /// </summary>
    public sealed partial class Companion
    {
        private const float HuntReach = 2f, HuntSense = 25f, SearchArea = 20f, SearchSpacing = 5f, CheckReach = 1.2f, SweepLimit = 75f;
        private const int SearchPlaces = 7;
        // Nearer the spot than this the run out to it is on the plain path.
        private const float HuntPlainWithin = 14f;
        private Vector3? hunt;
        private bool huntWaits;
        private float huntArrived, nextHuntScan, sniffUntil;
        // The places it checks round the hunted spot, in the order it goes, and how far through them it is.
        private readonly List<Vector3> sweep = new List<Vector3>();
        private int sweepAt;
        // Until then it's running back to you from a hunt or an ordered fight, and isn't teleported home.
        private float homeUntil;
        // Stopped at a place, nose down.
        private bool Sniffing => hunt != null && sniffUntil > 0;

        private bool StartHunt(Player owner, Vector3 point, out string problem)
        {
            problem = "The dog can't get there.";
            Vector3 from = owner.Transform.position;
            if (Vector3.Distance(from, point) > Plugin.OrderReach)
            { problem = $"Too far away (limit {Mathf.RoundToInt(Plugin.OrderReach)} m)."; return false; }
            if (!Walkable(from, point, out Vector3 spot)) { Plugin.Log.LogInfo($"Over there: no walkable ground the dog can reach near {point}."); return false; }
            bool waits = !rules.CanAttack;
            SetCommand(Command.Follow);
            StopFight();
            hunt = spot; huntWaits = waits; huntArrived = 0; nextHuntScan = 0; sniffUntil = 0; lastProgress = Time.time;
            sweep.Clear(); sweepAt = 0;
            corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
            ResetStalk();
            audio?.Play("bark");
            int metres = Mathf.RoundToInt(Vector3.Distance(from, spot));
            Say(waits ? $"Over there! ({metres} m)" : $"Over there! {Plugin.DogName.Value} goes hunting ({metres} m).");
            return true;
        }

        // The ground nearest the aimed point that the dog can walk to: the point itself, else stepping back towards you.
        private bool Walkable(Vector3 from, Vector3 point, out Vector3 spot)
        {
            spot = default;
            Vector3 back = from - point;
            float length = back.magnitude;
            for (float d = 0; d <= length; d += 2.5f)
            {
                Vector3 p = point + (length > .01f ? back / length * d : Vector3.zero);
                if (!NavMesh.SamplePosition(p, out NavMeshHit near, 4f, NavMesh.AllAreas) || !Reachable(near.position)) continue;
                spot = near.position;
                return true;
            }
            return false;
        }

        // True while the dog is on its way to (or working) the hunt area; the follow loop takes it there.
        private bool HuntWalking(Player owner, out Vector3 spot)
        {
            spot = default;
            if (hunt == null) return false;
            if (dead || attackTarget != null || command != Command.Follow) { hunt = null; return false; }
            spot = hunt.Value;
            if (!huntWaits && Time.time >= nextHuntScan)
            {
                nextHuntScan = Time.time + .4f;
                Player found = EnemyNear(owner, dog.transform.position, HuntSense);
                if (found != null)
                {
                    hunt = null;
                    Engage(found, true);
                    if (!stalking) audio?.Play("bark");
                    Say(Plugin.DogName.Value + " has found " + found.Profile.Nickname + "!");
                    return false;
                }
            }
            if (huntArrived == 0)
            {
                // On the way to the spot.
                if (Flat(dog.transform.position, spot) > HuntReach)
                {
                    if (Time.time - lastProgress > 8) { hunt = null; Say(Plugin.DogName.Value + " can't get there."); return false; }
                    return true;
                }
                if (huntWaits) { hunt = null; SetCommand(Command.Stay); Say(Plugin.DogName.Value + " waits there."); return false; }
                huntArrived = Time.time;
                ResetStalk();
                corners = System.Array.Empty<Vector3>(); corner = 0; nextPath = 0;
                PlanSearch(spot);
            }
            if (sweepAt >= sweep.Count || Time.time - huntArrived > SweepLimit)
            {
                hunt = null; sweep.Clear(); homeUntil = Time.time + 60;
                Say("Nothing there. " + Plugin.DogName.Value + " is coming back.");
                return false;
            }
            // At a place: it stands and sniffs a moment (still sensing), then moves on. One it can't get to is skipped.
            if (sniffUntil > 0)
            {
                lastProgress = Time.time;
                if (Time.time < sniffUntil) { spot = dog.transform.position; return true; }
                sniffUntil = 0; sweepAt++;
                return HuntWalking(owner, out spot);
            }
            if (Flat(dog.transform.position, sweep[sweepAt]) <= CheckReach) { sniffUntil = Time.time + Random.Range(1.5f, 2.8f); spot = dog.transform.position; return true; }
            if (Time.time - lastProgress > 6) { sweepAt++; lastProgress = Time.time; return HuntWalking(owner, out spot); }
            spot = sweep[sweepAt];
            return true;
        }

        // The places worth checking round the spot: walkable ground within the area (on its floor and the floors above
        // and below), scored up for being under a roof (inside a building), out of sight of the spot (behind cover) and
        // on another floor. The best are taken that are well apart and the dog can reach, and visited nearest first.
        // Cheap tests first; only the best few candidates get the (dearer) path check.
        private void PlanSearch(Vector3 centre)
        {
            sweep.Clear(); sweepAt = 0; sniffUntil = 0;
            var candidates = new List<(Vector3 Point, float Score, bool Inside)>();
            Vector3 eye = centre + Vector3.up;
            for (float x = -SearchArea; x <= SearchArea; x += 3f)
                for (float z = -SearchArea; z <= SearchArea; z += 3f)
                {
                    if (x * x + z * z > SearchArea * SearchArea) continue;
                    foreach (float y in new[] { 0f, 3.5f, -3.5f, 7f })
                    {
                        if (!NavMesh.SamplePosition(centre + new Vector3(x, y, z), out NavMeshHit near, 1.5f, NavMesh.AllAreas)) continue;
                        Vector3 p = near.position;
                        if (candidates.Any(c => (c.Point - p).sqrMagnitude < 2.25f)) continue;
                        bool inside = Physics.Raycast(p + Vector3.up * .5f, Vector3.up, 8f, groundMask, QueryTriggerInteraction.Ignore);
                        bool hidden = Physics.Linecast(eye, p + Vector3.up * .6f, groundMask, QueryTriggerInteraction.Ignore);
                        bool floor = Mathf.Abs(p.y - centre.y) > 2.2f;
                        float score = (inside ? 2f : 0) + (hidden ? 1.5f : 0) + (floor ? 1f : 0) + Mathf.Min(Flat(p, centre), 12f) / 12f + Random.value * .3f;
                        candidates.Add((p, score, inside));
                    }
                }
            var chosen = new List<(Vector3 Point, bool Inside)>();
            int pathChecks = 0;
            foreach (var c in candidates.OrderByDescending(c => c.Score))
            {
                if (chosen.Count >= SearchPlaces || pathChecks >= 40) break;
                if (chosen.Any(k => (k.Point - c.Point).sqrMagnitude < SearchSpacing * SearchSpacing)) continue;
                pathChecks++;
                if (Reachable(c.Point)) chosen.Add((c.Point, c.Inside));
            }
            // Nearest first, each from the last.
            Vector3 at = dog.transform.position;
            while (chosen.Count > 0)
            {
                var next = chosen.OrderBy(k => (k.Point - at).sqrMagnitude).First();
                chosen.Remove(next); sweep.Add(next.Point); at = next.Point;
            }
            int indoors = sweep.Count(p => Physics.Raycast(p + Vector3.up * .5f, Vector3.up, 8f, groundMask, QueryTriggerInteraction.Ignore));
            Plugin.Log.LogInfo($"Hunt: searching {sweep.Count} place(s) round {centre} ({indoors} under a roof; {candidates.Count} candidates, {pathChecks} path checks).");
            if (sweep.Count > 0) Say($"{Plugin.DogName.Value} searches the area ({sweep.Count} places" + (indoors > 0 ? $", {indoors} inside" : "") + ").");
        }

        // The nearest enemy around a point that the dog can see, or smell close by, and get to.
        private Player EnemyNear(Player owner, Vector3 around, float radius)
        {
            if (world == null) return null;
            Player best = null; float bestDistance = float.MaxValue;
            foreach (Player p in world.AllAlivePlayersList)
            {
                if (!IsEnemy(p, owner)) continue;
                float d = Vector3.Distance(p.Transform.position, around);
                if (d > radius || d >= bestDistance) continue;
                if (d > ScentRange && !ClearLine(HeadPoint, Body(p))) continue;
                best = p; bestDistance = d;
            }
            return best != null && Reachable(best.Transform.position) ? best : null;
        }
    }

    /// <summary>
    /// Getting about the hideout, which has no navigation mesh: the dog walks straight across clear, continuous floor,
    /// taking one turn when something is in the way. And when it's bored of its bed it gets up, wanders round the
    /// hideout (now and then coming over to you), and goes back to bed.
    /// </summary>
    public sealed partial class Companion
    {
        private const float DogRadius = .2f;
        private bool roaming;
        private int roamStops;
        private Vector3 roamSpot;
        private float nextRoam, roamPause, legStarted, offBedSince;
        private const float OffBedFor = 60f;

        private bool WalkingWithoutNavmesh => IsHideout && !navigationAvailable;

        // A route of one or two straight legs. The first point is 'from', the last is on the floor at 'to'.
        private bool HideoutRoute(Vector3 from, Vector3 to, out Vector3[] route)
        {
            route = null;
            if (!FloorNear(to, .6f, out Vector3 end)) end = to;
            bool climb = !IsHideout;   // raid floors have stairs; the hideout's legs are level
            if (Walkable(from, end, climb)) { route = new[] { from, end }; return true; }
            float best = float.MaxValue; Vector3 turn = default;
            foreach (Vector3 around in new[] { from, end })
                for (float radius = 1.2f; radius <= 3.6f; radius += 1.2f)
                    for (int i = 0; i < 16; i++)
                    {
                        float a = i * 22.5f * Mathf.Deg2Rad;
                        if (!FloorNear(around + new Vector3(Mathf.Sin(a), 0, Mathf.Cos(a)) * radius, .5f, out Vector3 corner)) continue;
                        float length = Flat(from, corner) + Flat(corner, end);
                        if (length < best && Walkable(from, corner, climb) && Walkable(corner, end, climb)) { best = length; turn = corner; }
                    }
            if (best == float.MaxValue) return false;
            route = new[] { from, turn, end };
            return true;
        }

        // Nothing solid at the dog's body height along the way, and floor under every step with no drop or wall of steps.
        // 'climb': up and down stairs, the body checked at each step's own height rather than the start's.
        private static bool Walkable(Vector3 a, Vector3 b, bool climb = false)
        {
            Vector3 flat = b - a; flat.y = 0;
            float length = flat.magnitude;
            if (length < .05f) return true;
            Vector3 direction = flat / length;
            if (climb) return Climbable(a, b, direction, length);
            Vector3 low = a + Vector3.up * (.25f + DogRadius), high = a + Vector3.up * (.9f - DogRadius);
            foreach (RaycastHit hit in Physics.CapsuleCastAll(low, high, DogRadius, direction, length, Solid, QueryTriggerInteraction.Ignore))
                if (hit.distance > 0 && !Ignored(hit.collider)) return false;
            float y = a.y;
            for (float d = .35f; d < length + .35f; d += .35f)
            {
                Vector3 p = a + direction * Mathf.Min(d, length);
                if (!FloorNear(new Vector3(p.x, y, p.z), .3f, out Vector3 floor)) return false;
                y = floor.y;
            }
            return Mathf.Abs(y - b.y) < .5f;
        }

        private static bool Climbable(Vector3 a, Vector3 b, Vector3 direction, float length)
        {
            float y = a.y;
            for (float d = .35f; d < length + .35f; d += .35f)
            {
                Vector3 p = a + direction * Mathf.Min(d, length);
                if (!FloorNear(new Vector3(p.x, y, p.z), .3f, out Vector3 floor)) return false;
                y = floor.y;
                foreach (Collider c in Physics.OverlapCapsule(floor + Vector3.up * (.25f + DogRadius), floor + Vector3.up * (.9f - DogRadius),
                                                              DogRadius, Solid, QueryTriggerInteraction.Ignore))
                    if (!Ignored(c)) return false;
            }
            return Mathf.Abs(y - b.y) < .5f;
        }

        // ---- wandering ----------------------------------------------------------------------------------------------

        // Called every frame in the hideout: a dog that has rested on its bed a while gets up for a wander. One lying
        // down anywhere else (told to, or left behind when its kennel was moved) goes back to its bed after a while.
        private void TickRoam()
        {
            if (!IsHideout || roaming || !Plugin.Roam.Value || decor?.Home == null || dead) return;
            bool lying = command == Command.Down && !bedTrip && meal == MealSource.None && actionClip == null
                         && (stance == Stance.Lying || stance == Stance.Sleeping);
            bool onBed = Flat(dog.transform.position, decor.Home.position) < .8f;
            if (lying && !onBed)
            {
                if (offBedSince == 0) offBedSince = Time.time;
                else if (Time.time - offBedSince > OffBedFor) { offBedSince = 0; GoToBed(true); }
            }
            else offBedSince = 0;
            bool resting = lying && onBed;
            if (!resting) { nextRoam = Mathf.Max(nextRoam, Time.time + 20); return; }
            if (nextRoam == 0) nextRoam = Time.time + Random.Range(45f, 90f);
            if (Time.time < nextRoam) return;
            SetCommand(Command.Stay); WakeUp();
            roaming = true; roamStops = Random.Range(2, 5);
            // Out of the shelter by its door first (never through its walls).
            if (decor.DoorStep is Vector3 door && FloorNear(door, .5f, out Vector3 step))
            { roamSpot = step; roamPause = Time.time; legStarted = lastProgress = Time.time; roamStops++; return; }
            if (!NextRoamSpot()) EndRoam();
        }

        // True while the dog is walking to its next stop; between stops it stands, sniffs about and sits.
        private bool RoamWalking(out Vector3 spot)
        {
            spot = roamSpot;
            if (!roaming) return false;
            if (Flat(dog.transform.position, roamSpot) > .45f)
            {
                if (Time.time - lastProgress < 5 && Time.time - legStarted < 25) return true;
                // Stuck or taking too long: try somewhere else.
                if (NextRoamSpot()) { spot = roamSpot; return true; }
                EndRoam(); return false;
            }
            if (roamPause == 0) roamPause = Time.time + Random.Range(5f, 12f);
            if (Time.time < roamPause) return false;
            if (--roamStops <= 0 || !NextRoamSpot()) { EndRoam(); return false; }
            spot = roamSpot; WakeUp();
            return true;
        }

        private bool NextRoamSpot()
        {
            roamPause = 0; legStarted = Time.time; lastProgress = Time.time;
            Vector3 from = dog.transform.position;
            Vector3 home = decor?.Home != null ? decor.Home.position : from;
            Player owner = CurrentPlayer;
            // Sometimes over to you, to say hello.
            if (owner != null && Random.value < .35f && Flat(owner.Transform.position, from) < 15)
            {
                Vector3 toDog = from - owner.Transform.position; toDog.y = 0;
                Vector3 hello = owner.Transform.position + (toDog.sqrMagnitude > .01f ? toDog.normalized : owner.Transform.forward) * 1.2f;
                if (FloorNear(hello, .5f, out Vector3 near) && HideoutRoute(from, near, out _)) { roamSpot = near; return true; }
            }
            for (int i = 0; i < 12; i++)
            {
                Vector2 r = Random.insideUnitCircle.normalized * Random.Range(2f, 7f);
                if (!FloorNear(home + new Vector3(r.x, 0, r.y), .5f, out Vector3 spot)) continue;
                if (Blocked(spot + Vector3.up * .5f, new Vector3(.3f, .4f, .45f), Quaternion.identity)) continue;
                if (HideoutRoute(from, spot, out _)) { roamSpot = spot; return true; }
            }
            return false;
        }

        private void EndRoam()
        {
            roaming = false;
            nextRoam = Time.time + Random.Range(120f, 300f);
            GoToBed(true);
        }
    }

    /// <summary>
    /// Valuables (raid only): while it follows you the dog sniffs the loot around (loose and in containers); something
    /// worth at least the configured price gets one bark, a look its way and a message saying what and where. Each find
    /// only once. (The dog's loot search and fetching, which were here and in Companion.Search, went in 0.30.)
    /// </summary>
    public sealed partial class Companion
    {
        private const float SniffEvery = 2.5f, SniffGap = 12f;
        private float nextSniff, lastAlert;
        private readonly HashSet<string> sniffed = new HashSet<string>();
        private LootableContainer[] containers;

        // The raid's containers, found once (they don't come and go).
        private LootableContainer[] Containers() => containers ??= Object.FindObjectsOfType<LootableContainer>();

        private static List<Item> Contents(LootableContainer container)
            => (container.ItemOwner?.RootItem as CompoundItem)?.GetAllItems().Where(i => i != container.ItemOwner.RootItem).ToList() ?? new List<Item>();

        private void TickSniff(Player owner)
        {
            int threshold = Plugin.ValuableAlert.Value;
            if (threshold <= 0 || IsHideout || world == null || command != Command.Follow || attackTarget != null || hunt != null
                || Time.time < nextSniff || Time.time - lastAlert < SniffGap) return;
            nextSniff = Time.time + SniffEvery;
            float radius = 10f + 2f * Mathf.Clamp(K9.Training, 0, 3);
            Vector3 nose = dog.transform.position;
            string key = null, what = null; Vector3 where = default; float bestDistance = float.MaxValue;
            foreach (LootItem loot in world.LootItems.GetValuesEnumerator())
            {
                if (loot == null || loot is Corpse || loot.Item == null || !loot.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(loot.transform.position, nose);
                if (d > radius || d >= bestDistance || !Valuable(loot.Item, threshold) || sniffed.Contains(loot.Item.Id.ToString())) continue;
                key = loot.Item.Id.ToString(); what = loot.Item.ShortName.Localized(); where = loot.transform.position; bestDistance = d;
            }
            foreach (LootableContainer container in Containers())
            {
                if (container == null || string.IsNullOrEmpty(container.Id) || sniffed.Contains(container.Id) || !container.gameObject.activeInHierarchy) continue;
                float d = Vector3.Distance(container.transform.position, nose);
                if (d > radius || d >= bestDistance) continue;
                Item best = Contents(container).Where(i => Valuable(i, threshold)).OrderByDescending(i => i.Template.CreditsPrice).FirstOrDefault();
                if (best == null) continue;
                key = container.Id; what = best.ShortName.Localized() + " in " + (container.ItemOwner?.RootItem?.ShortName?.Localized() ?? "a container");
                where = container.transform.position; bestDistance = d;
            }
            if (key == null) return;
            sniffed.Add(key);
            lastAlert = Time.time;
            Vector3 look = where - nose; look.y = 0;
            if (look.sqrMagnitude > .01f) yaw = Mathf.Atan2(look.x, look.z) * Mathf.Rad2Deg;
            audio?.Play("bark");
            Say($"{Plugin.DogName.Value} smells something valuable: {what} ({Mathf.RoundToInt(Vector3.Distance(owner.Transform.position, where))} m {Side(owner, where)}).");
        }

        // Worth the alert: its price (each, for a stack) at least the threshold; money doesn't count.
        private static bool Valuable(Item item, int threshold) => !(item is Money) && item.Template != null && item.Template.CreditsPrice >= threshold;

        private static string Side(Player owner, Vector3 where)
        {
            Vector3 to = where - owner.Transform.position; to.y = 0;
            Vector3 ahead = owner.Transform.forward; ahead.y = 0;
            float angle = Vector3.SignedAngle(ahead, to, Vector3.up);
            return Mathf.Abs(angle) < 45 ? "ahead" : Mathf.Abs(angle) > 135 ? "behind you" : angle > 0 ? "to your right" : "to your left";
        }
    }

    /// <summary>
    /// Puts the dog's commands into the game's own Y (gesture) menu as a DOG group, next to COMMAND, REACTION and the rest.
    /// The group is built with the menu's own CreatePhraseGroup, so it looks, opens and is selected exactly like the others
    /// (hover, click or release Y). Commands can also be bound to F-keys with a right-click, the same way as phrases.
    /// Each command uses an id far outside the game's phrase range; picking one is caught before the game would try to say it.
    /// </summary>
    /// <summary>
    /// The game's own F-key binds (right-click a phrase or DOG command in the Y menu's bind list): F1-F12, tapped or
    /// double-tapped. One of the mod's keys on an F-key you've bound there would fire both, so the bind wins and the mod's
    /// key stands aside (it says so once). Keys with Shift and the like aren't affected.
    /// </summary>
    internal static class GameBinds
    {
        private static readonly HashSet<KeyCode> bound = new HashSet<KeyCode>(), told = new HashSet<KeyCode>();
        private static string readFor;
        private static float readAt = -10;

        internal static bool Pressed(BepInEx.Configuration.ConfigEntry<BepInEx.Configuration.KeyboardShortcut> entry, Action<string> say, bool scav)
        {
            var key = entry.Value;
            if (!key.IsDown()) return false;
            if (key.Modifiers.Any() || !Bound(key.MainKey, scav)) return true;
            if (told.Add(key.MainKey))
                say?.Invoke($"{key.MainKey} is bound in the game's Y-menu binds, so the mod's \"{entry.Definition.Key}\" key is off. Pick another key in F12, or unbind {key.MainKey}.");
            return false;
        }

        private static bool Bound(KeyCode key, bool scav)
        {
            if (key < KeyCode.F1 || key > KeyCode.F12) return false;
            string name = scav ? "ScavBindCommands" : "BindCommands";
            if (name != readFor || Time.unscaledTime - readAt > 2) Read(name);
            return bound.Contains(key);
        }

        // Saved by the game as soon as a bind changes, as {"F7": phrase, "DoubleF8": phrase}; nothing saved yet is the
        // game's default (F1).
        private static void Read(string name)
        {
            readFor = name; readAt = Time.unscaledTime;
            bound.Clear();
            try
            {
                string json = PlayerPrefs.GetString(name);
                if (string.IsNullOrEmpty(json)) { bound.Add(KeyCode.F1); return; }
                foreach (var p in Newtonsoft.Json.Linq.JObject.Parse(json).Properties())
                {
                    if (!Enum.TryParse(p.Name, out ECommand command)) continue;
                    string text = command.ToString();
                    if (text.StartsWith("Double")) text = text.Substring(6);
                    if (text.Length > 1 && text[0] == 'F' && int.TryParse(text.Substring(1), out int n) && n >= 1 && n <= 12)
                        bound.Add(KeyCode.F1 + (n - 1));
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Couldn't read the game's F-key binds (the mod's keys work as usual): " + e.Message); }
        }
    }

    internal static class DogMenu
    {
        private const int Base = 7300;
        private static readonly DogOrder[] Orders = { DogOrder.OverThere, DogOrder.ComeHere, DogOrder.Sit, DogOrder.LieDown, DogOrder.OpenBag, DogOrder.Feed, DogOrder.Water, DogOrder.GoToBed, DogOrder.Stay, DogOrder.Follow, DogOrder.Search, DogOrder.FetchMeds, DogOrder.FetchFood, DogOrder.AttackMode, DogOrder.ApproachMode, DogOrder.BringHere };
        private static readonly string[] Labels = { "Over there (attack)", "Come here", "Sit", "Lie down", "Open jacket", "Feed", "Give water", "Go to bed", "Stay", "Follow", "Search (removed)", "Fetch (removed)", "Fetch (removed)", "When to attack", "Approach", "Bring to me" };
        // New commands go on the end: the ids (Base + position) are what F-key bindings remember. Search and the two
        // fetches were taken out in 0.30: their places stay (an old binding just does nothing) but they're not in the menu.
        private static readonly HashSet<DogOrder> Retired = new HashSet<DogOrder> { DogOrder.Search, DogOrder.FetchMeds, DogOrder.FetchFood };
        // Not in the menu either: Go to bed is for the hideout, where there is no Y menu (the DOG tab and the kennel do it).
        private static readonly HashSet<DogOrder> NotInMenu = new HashSet<DogOrder>(Retired) { DogOrder.GoToBed };

        private static bool IsDog(int id) => id >= Base && id < Base + Orders.Length;

        internal static void Patch(Harmony harmony)
        {
            Unbind();
            // F1-F12: the game's bind list shows each bound entry by name and greys out phrases the character can't say.
            harmony.Patch(Method(typeof(GestureCommands), nameof(GestureCommands.GetCommandName)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(BindName)));
            harmony.Patch(Method(typeof(GesturesBindItem), nameof(GesturesBindItem.UpdateVisual)),
                postfix: new HarmonyMethod(typeof(DogMenu), nameof(BindAvailable)));
            harmony.Patch(Method(typeof(GesturesMenu), nameof(GesturesMenu.InitPhraseGroups)),
                postfix: new HarmonyMethod(typeof(DogMenu), nameof(AddGroup)));
            harmony.Patch(Method(typeof(GesturesMenu), nameof(GesturesMenu.Show)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(MakeAvailable)), postfix: new HarmonyMethod(typeof(DogMenu), nameof(Position)));
            harmony.Patch(Method(typeof(GestureBaseItem), nameof(GestureBaseItem.ShowInternal)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(Label)));
            harmony.Patch(Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.PlayPhraseOrGesture)),
                prefix: new HarmonyMethod(typeof(DogMenu), nameof(Picked)));
        }

        // F-keys still bound to a command that's no longer in the menu (the old search and fetches, Go to bed) are
        // unbound, in the game's own saved bindings (PMC and Scav), before the game reads them.
        private static void Unbind()
        {
            var dead = new HashSet<int>();
            for (int i = 0; i < Orders.Length; i++) if (NotInMenu.Contains(Orders[i])) dead.Add(Base + i);
            foreach (string key in new[] { "BindCommands", "ScavBindCommands" })
            {
                try
                {
                    string json = PlayerPrefs.GetString(key);
                    if (string.IsNullOrEmpty(json)) continue;
                    var binds = Newtonsoft.Json.Linq.JObject.Parse(json);
                    var gone = new List<string>();
                    foreach (var p in binds.Properties())
                        if (p.Value.Type == Newtonsoft.Json.Linq.JTokenType.Integer && dead.Contains((int)p.Value)) gone.Add(p.Name);
                    if (gone.Count == 0) continue;
                    foreach (string name in gone) binds.Remove(name);
                    PlayerPrefs.SetString(key, binds.ToString(Newtonsoft.Json.Formatting.None));
                    PlayerPrefs.Save();
                    Plugin.Log.LogInfo($"Unbound {gone.Count} F-key(s) from dog commands that are no longer in the menu ({key}: {string.Join(", ", gone)}).");
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Couldn't tidy the dog's F-key bindings ({key}): {e.Message}"); }
            }
        }

        private static System.Reflection.MethodInfo Method(Type type, string name)
            => AccessTools.Method(type, name) ?? throw new MissingMethodException(type.Name, name);

        private static void AddGroup(GesturesMenu __instance)
        {
            try
            {
                var shown = new List<EPhraseTrigger>();
                for (int i = 0; i < Orders.Length; i++) if (!NotInMenu.Contains(Orders[i])) shown.Add((EPhraseTrigger)(Base + i));
                EPhraseTrigger[] phrases = shown.ToArray();
                GesturesAudioItem group = __instance.CreatePhraseGroup("DOG", __instance._commonGroupTemplate, phrases);
                // Kept out of the menu's fixed slot layout (which put it in the middle, under the gestures) and placed
                // by hand in the free spot at the bottom of the ring each time the menu opens.
                var element = group.gameObject.GetComponent<LayoutElement>() ?? group.gameObject.AddComponent<LayoutElement>();
                element.ignoreLayout = true;
                Groups[__instance] = (RectTransform)group.transform;
            }
            catch (Exception e) { Plugin.Log.LogError("Could not add the DOG group to the gesture menu: " + e); }
        }

        private static readonly Dictionary<GesturesMenu, RectTransform> Groups = new Dictionary<GesturesMenu, RectTransform>();

        private static readonly System.Reflection.FieldInfo NameField = AccessTools.Field(typeof(GesturesAudioItem), "_nameText");

        // Below the middle of the ring, between its two lower hexagons: 70% of the way down to where the top hexagon
        // (REACTION) would be mirrored, so the command list it opens stays clear of the F-key list under the menu. Placed
        // by the hexagons' own centres (their labels) in the ring's own space, so the group's pivot, size, screen
        // resolution and UI scale don't matter.
        private const float DownFromMiddle = .7f;
        private static void Position(GesturesMenu __instance)
        {
            if (!Groups.TryGetValue(__instance, out RectTransform dog) || dog == null) return;
            var ring = (RectTransform)__instance._audioContainer;
            LayoutRebuilder.ForceRebuildLayoutImmediate(ring);
            RectTransform top = null; Vector2 topCentre = default;
            var others = new List<(Vector2 centre, RectTransform group)>();
            for (int i = 0; i < ring.childCount; i++)
            {
                if (!(ring.GetChild(i) is RectTransform group) || group == dog || !group.gameObject.activeInHierarchy) continue;
                Vector2 c = Centre(ring, group);
                others.Add((c, group));
                if (top == null || c.y > topCentre.y) { top = group; topCentre = c; }
            }
            if (top == null) return;
            Vector2 middle = ring.rect.center;
            Vector2 target = new Vector2(topCentre.x, middle.y - (topCentre.y - middle.y) * DownFromMiddle);
            // The game's SITUATIONAL group sits there (it reports itself shown even when empty): the DOG group keeps the spot,
            // and that one moves a row down, below the F-key list's top (it only has entries now and then).
            float step = Mathf.Max(60, top.rect.height * 1.15f);
            string moved = null;
            foreach (var (c, group) in others)
                if ((c - target).magnitude < step * .6f) { group.localPosition += Vector3.down * step; moved = group.name; }
            dog.anchorMin = dog.anchorMax = top.anchorMin;
            Vector2 now = Centre(ring, dog);
            dog.localPosition += (Vector3)(target - now);
            if (!logged) { logged = true; Plugin.Log.LogInfo($"DOG group centred at {Centre(ring, dog)} (ring middle {middle}, top group {top.name} at {topCentre})" + (moved != null ? $"; {moved} moved down a row." : ".")); }
        }

        // Where a group's hexagon is drawn, in the ring's own coordinates: the middle of its name label.
        private static Vector2 Centre(RectTransform ring, RectTransform group)
        {
            var label = (NameField?.GetValue(group.GetComponent<GesturesAudioItem>()) as Component)?.transform as RectTransform;
            RectTransform at = label != null ? label : group;
            return ring.InverseTransformPoint(at.TransformPoint(at.rect.center));
        }
        private static bool logged;

        // The menu hides phrases the character can't say; the dog's commands are always listed (greyed out when unavailable).
        // Four underscores: Harmony strips three, leaving the field's own name, _availablePhrases.
        private static void MakeAvailable(HashSet<EPhraseTrigger> ____availablePhrases)
        {
            if (____availablePhrases == null) return;
            for (int i = 0; i < Orders.Length; i++) ____availablePhrases.Add((EPhraseTrigger)(Base + i));
        }

        // Runs as each item is shown: names the dog's commands and greys out the ones that can't be given right now.
        private static void Label(GestureBaseItem __instance, ref string caption)
        {
            if (!(__instance is GesturesAudioSubItem item) || !IsDog((int)item.PhraseTrigger)) return;
            int i = (int)item.PhraseTrigger - Base;
            Companion dog = Companion.Current;
            caption = dog?.NeedsLabelFor(Orders[i]) ?? Labels[i];
            string why = null;
            bool ok = dog != null && dog.Available(Orders[i], out why);
            if (dog == null) why = "no dog";
            item._textField.text = ok ? Lang.T(caption) : "<color=#7d7d7d>" + Lang.T(caption) + "</color> <size=75%><color=#c9705f>" + Lang.T(why) + "</color></size>";
        }

        private static bool BindName(int index, ref string __result)
        {
            if (!IsDog(index)) return true;
            __result = "Dog: " + Labels[index - Base];
            return false;
        }

        private static void BindAvailable(GesturesBindItem __instance, int index)
        {
            if (IsDog(index)) __instance.IsAvailable = true;
        }

        // A dog command picked in the menu, or its F-key pressed (the game's F-key handler plays bound entries through the
        // same method).
        private static bool Picked(int actionId)
        {
            if (!IsDog(actionId)) return true;
            Companion dog = Companion.Current;
            if (dog != null) dog.Order(Orders[actionId - Base]);
            return false;
        }
    }

    /// <summary>What the game's own interaction raycast finds when you look at the dog in a raid.</summary>
    internal sealed class DogInteractive : InteractableObject
    {
        internal Companion Dog;
    }

    /// <summary>What it finds when you look at the kennel's bowls and dog house in the hideout (first person).</summary>
    internal sealed class KennelInteractive : InteractableObject { }

    /// <summary>
    /// The dog in the game's own interaction systems:
    ///  - Look at it and press your interact key (E): open its jacket, feed it, give it water or pet it, in the same prompt
    ///    list as containers and doors (scroll to pick, as there).
    ///  - Right-click food or a drink in your inventory: "Feed dog" / "Give water to dog", in the item's own menu.
    ///  - The K9 veterinary kit is for the dog only: however you use it (USE, HEAL ALL, a double-click, its quick slot key,
    ///    dragging it onto your body), you treat the dog instead (DogVet).
    ///  - In the hideout, look at the kennel (its bowls and dog house): fill the food or water bowl from your stash, or
    ///    open the kennel's upgrades.
    /// </summary>
    internal static class DogInteraction
    {
        internal const string FeedKey = "Feed dog", WaterKey = "Give water to dog";

        internal static void Patch(Harmony harmony)
        {
            var actions = AccessTools.Method(typeof(InteractionContextHelper), nameof(InteractionContextHelper.GetAvailableActions),
                new[] { typeof(GamePlayerOwner), typeof(IInteractive) }) ?? throw new MissingMethodException(nameof(InteractionContextHelper), "GetAvailableActions(owner, interactive)");
            harmony.Patch(actions, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(Actions)));
            var menu = AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions))
                       ?? throw new MissingMethodException(nameof(ItemUiContext), nameof(ItemUiContext.GetItemContextInteractions));
            harmony.Patch(menu, postfix: new HarmonyMethod(typeof(DogInteraction), nameof(Menu)));
            // Every way of using a med ends in Player.Proceed(Meds, body parts, ...); a quick slot key goes through
            // TryProceed first (which would put your weapon away), so both are caught.
            var proceed = AccessTools.GetDeclaredMethods(typeof(Player)).FirstOrDefault(m => m.Name == nameof(Player.Proceed)
                              && m.GetParameters().Length > 1 && m.GetParameters()[0].ParameterType == typeof(Meds))
                          ?? throw new MissingMethodException(nameof(Player), "Proceed(Meds, ...)");
            harmony.Patch(proceed, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(UseMeds)));
            var quick = AccessTools.Method(typeof(Player), nameof(Player.TryProceed)) ?? throw new MissingMethodException(nameof(Player), nameof(Player.TryProceed));
            harmony.Patch(quick, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(QuickUse)));
            // Before that, a quick slot key asks whether the med would do YOU any good, and does nothing if not.
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.SetQuickSlotItem)), prefix: new HarmonyMethod(typeof(DogInteraction), nameof(QuickSlot)));
            // The item menu: USE and HEAL ALL go by whether YOU are hurt; for the kit, by whether the dog is. Out of a raid,
            // dragging it onto your body goes through the menu's health controller instead of Player.Proceed.
            harmony.Patch(AccessTools.Method(typeof(ItemContextInteractionsSwitcher), nameof(ItemContextInteractionsSwitcher.IsInteractive)),
                postfix: new HarmonyMethod(typeof(DogInteraction), nameof(KitButtons)));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.UseItem)), prefix: new HarmonyMethod(typeof(DogInteraction), nameof(QuickUse)));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.HealAll)), prefix: new HarmonyMethod(typeof(DogInteraction), nameof(UseMeds)));
            foreach (var apply in AccessTools.GetDeclaredMethods(typeof(OfflineHealthController)).Where(m => m.Name == nameof(OfflineHealthController.ApplyItem)))
                harmony.Patch(apply, prefix: new HarmonyMethod(typeof(DogInteraction), nameof(AppliedAtHome)));
        }

        private static bool UseMeds(Meds meds) => !TreatInstead(meds);
        private static bool QuickUse(Item item) => !TreatInstead(item);
        private static bool QuickSlot(Player __instance, EBoundItem quickSlot, Callback<IHandsController> callback)
        {
            if (!TreatInstead(__instance.InventoryController?.Inventory?.FastAccess?.GetBoundItem(quickSlot))) return true;
            callback?.Invoke(default);
            return false;
        }
        private static bool AppliedAtHome(Item item, ref bool __result)
        {
            if (!TreatInstead(item)) return true;
            __result = false;
            return false;
        }

        // The K9 veterinary kit is never used on a person: the dog gets it, if it can.
        private static bool TreatInstead(Item item)
        {
            if (!DogVet.IsKit(item)) return false;
            DogVet.Use(item);
            return true;
        }

        private static void KitButtons(ItemContextInteractionsSwitcher __instance, EItemInfoButton button, ref IResult __result)
        {
            if ((button != EItemInfoButton.Use && button != EItemInfoButton.HealAll && button != EItemInfoButton.UseAll) || !DogVet.IsKit(__instance._item)) return;
            try
            {
                string why = DogVet.Problem(__instance._item);
                __result = why == null ? (IResult)SuccessfulResult.New : new FailedResult(why);
            }
            catch (Exception e) { Plugin.Log.LogWarning("K9 veterinary kit's menu: " + e.Message); }
        }

        // The game has no interactions for an object type it doesn't know (it throws), so the dog's are answered first.
        private static bool Actions(GamePlayerOwner owner, IInteractive interactive, ref AvailableInteractionState __result)
        {
            if (interactive is KennelInteractive) { __result = Kennel(); return false; }
            if (!(interactive is DogInteractive target)) return true;
            Player player = owner?.Player;
            Companion dog = target.Dog;
            CompoundItem jacket = DogBag.FindInRaid(player);
            var state = new AvailableInteractionState();
            if (dog != null && dog.Killed && !DogBag.GearTaken && (jacket != null || DogGear.RaidHead != null || DogGear.RaidCamera != null))
                state.Actions.Add(new InteractionAction { Name = "Take " + Plugin.DogName.Value + "'s gear", Action = () => dog.TakeGear(player) });
            if (dog != null && dog.Downed)
            {
                Item kit = dog.ReviveKit(player);
                string why = kit == null ? "no K9 veterinary kit" : dog.TreatProblem(kit);
                string name = "Revive " + Plugin.DogName.Value;
                state.Actions.Add(new InteractionAction { Name = why == null ? name : name + ": " + why, Disabled = why != null, Action = () => DogVet.Use(kit) });
            }
            state.Actions.Add(new InteractionAction
            {
                // Named for what it is: "Open chest rig", "Open camera vest".
                Name = jacket != null ? "Open " + JacketWord(jacket) : "No jacket this raid",
                Disabled = jacket == null,
                Action = () =>
                {
                    string problem = DogBag.Open(player);
                    if (problem != null) NotificationManager.DisplayWarningNotification(problem);
                }
            });
            if (dog != null && !dog.IsDead)
            {
                foreach (DogOrder order in new[] { DogOrder.Feed, DogOrder.Water })
                {
                    bool can = dog.Available(order, out string why);
                    state.Actions.Add(new InteractionAction
                    {
                        Name = can ? (dog.NeedsLabelFor(order) ?? (order == DogOrder.Feed ? "Feed" : "Give water")) : (order == DogOrder.Feed ? "Feed" : "Give water") + ": " + why,
                        Disabled = !can,
                        Action = () => dog.Order(order)
                    });
                }
                state.Actions.Add(new InteractionAction { Name = "Pet " + Plugin.DogName.Value, Action = () => dog.PetDog(player) });
            }
            state.InitSelected();
            __result = state;
            return false;
        }

        private static AvailableInteractionState Kennel()
        {
            var state = new AvailableInteractionState();
            Companion dog = Companion.Current;
            if (dog != null && Plugin.SurvivalNeeds.Value)
                foreach (bool food in new[] { true, false })
                {
                    string what = food ? "food" : "water";
                    float level = dog.DishLevel(food);
                    state.Actions.Add(new InteractionAction
                    {
                        Name = level > 90 ? $"The {what} bowl is full" : $"Fill the {what} bowl ({Mathf.RoundToInt(level)}%)",
                        Disabled = level > 90,
                        Action = () => dog.FillDish(food)
                    });
                }
            state.Actions.Add(new InteractionAction
            {
                Name = "K9 Kennel upgrades",
                Action = () => { if (!KennelIcon.OpenScreen()) NotificationManager.DisplayWarningNotification("Open the kennel from its paw icon on the hideout overview."); }
            });
            state.InitSelected();
            return state;
        }

        // "chest rig", "pocket vest", "camera vest": the jacket's name ("K9 Chest Rig (Black)"), lower case, without the
        // "K9" and the colour.
        private static string JacketWord(Item jacket)
        {
            string name = jacket.LocalizedName() ?? "";
            if (name.StartsWith("K9 ")) name = name.Substring(3);
            int colour = name.IndexOf(" (", StringComparison.Ordinal);
            if (colour > 0) name = name.Substring(0, colour);
            return string.IsNullOrEmpty(name) ? "jacket" : name.ToLowerInvariant();
        }

        // Adds the feeding entries to food and drinks you carry, while the dog is alive.
        private static void Menu(ItemContext itemContext, ContextInteractions<EItemInfoButton> __result)
        {
            try
            {
                Item item = itemContext?.Item;
                Companion dog = Companion.Raid ?? Companion.Current;
                if (__result == null || item == null || dog == null || dog.IsDead) return;
                Player owner = dog.Owner;
                if (owner == null || !DogPantry.Carried(owner, item)) return;
                Sprite icon = ResourcesCache.Pop<Sprite>("Characteristics/Icons/" + EItemInfoButton.Use);
                if (Plugin.SurvivalNeeds.Value)
                {
                    Add(__result, item, true, FeedKey, icon);
                    Add(__result, item, false, WaterKey, icon);
                }
            }
            catch (Exception e) { Plugin.Log.LogWarning("Dog feeding menu: " + e.Message); }
        }

        private static void Add(ContextInteractions<EItemInfoButton> menu, Item item, bool food, string key, Sprite icon)
        {
            if (DogPantry.ValueOf(item, food) <= 0) return;
            foreach (DynamicContextInteraction existing in menu.DynamicInteractions) if (existing.Key == key) return;
            menu.method_2("k9companion_" + (food ? "feed" : "water"), key, () =>
            {
                // In a raid it's handed over (the dog comes and eats from your hand); at home it has it at once.
                Companion dog = Companion.Raid ?? Companion.Current;
                string result = dog == null ? "No dog." : dog.Owner is HideoutPlayer ? dog.FeedAtHome(item, food) : dog.FeedFromInventory(item, food);
                if (result != null) NotificationManager.DisplayWarningNotification(result);
            }, icon);
        }
    }

    internal sealed class RenameDialog : MonoBehaviour
    {
        private static RenameDialog dialog;
        internal static Companion Owner { get; private set; }
        internal static bool IsOpen => dialog != null;
        private CursorLockMode previousLock;
        private bool previousVisible, focusName = true;
        private string draft, error = "";
        private Rect window;

        internal static void PatchInput(Harmony harmony)
        {
            foreach (string name in new[] { "EFT.InputSystem.InputNode", "EFT.InputSystem.InputNodeAbstract" })
            {
                var type = AccessTools.TypeByName(name);
                var method = AccessTools.DeclaredMethod(type, "TranslateInput");
                if (method == null) throw new MissingMethodException(name, "TranslateInput");
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(RenameDialog), nameof(FilterInput)),
                    postfix: new HarmonyMethod(typeof(RenameDialog), nameof(KeepCursor)));
            }
        }
        private static void FilterInput(List<ECommand> __0, ref float[] __1)
        {
            if (!IsOpen) return;
            __0?.Clear();
            if (__1 != null) Array.Clear(__1, 0, __1.Length);
            // Allow zero axes through so movement is actually stopped, rather than retaining the last vector.
        }
        private static void KeepCursor(ref ECursorResult __2) { if (IsOpen) __2 = ECursorResult.ShowCursor; }

        // From the DOG tab, where there may be no dog in the scene to rename beside.
        internal static void OpenFromMenu() { Open(null); if (dialog != null) dialog.fromMenu = true; }

        internal static void Open(Companion owner)
        {
            if (IsOpen || Input.GetMouseButton(0) || Input.GetMouseButton(1)) return;
            Owner = owner;
            dialog = new GameObject("Name your dog").AddComponent<RenameDialog>();
            dialog.previousLock = Cursor.lockState; dialog.previousVisible = Cursor.visible;
            dialog.draft = Plugin.DogName.Value;
            dialog.window = new Rect((Screen.width - 440) / 2f, (Screen.height - 200) / 2f, 440, 200);
            Cursor.lockState = CursorLockMode.None; Cursor.visible = true;
        }
        internal static void Close()
        {
            if (dialog == null) { Owner = null; return; }
            var old = dialog; dialog = null; Owner = null;
            Cursor.lockState = old.previousLock; Cursor.visible = old.previousVisible;
            Destroy(old.gameObject);
        }
        private bool fromMenu;
        private void Update() { if (!fromMenu && (Owner == null || !Owner.isActiveAndEnabled)) Close(); }
        private void LateUpdate() { if (IsOpen) { Cursor.lockState = CursorLockMode.None; Cursor.visible = true; } }
        private void OnApplicationFocus(bool focused) { if (!focused) Close(); }
        private void OnGUI()
        {
            if (!IsOpen) return;
            GUI.depth = -1000;
            window = GUI.ModalWindow(GetInstanceID(), window, Draw, "Name your dog");
        }
        private void Draw(int id)
        {
            GUI.Label(new Rect(24, 35, 390, 25), "Choose a name (up to 16 characters).");
            GUI.SetNextControlName("K9DogName");
            draft = GUI.TextField(new Rect(24, 65, 390, 32), draft ?? "", 16);
            if (focusName) { GUI.FocusControl("K9DogName"); focusName = false; }
            GUI.Label(new Rect(24, 103, 390, 28), error);
            var ev = Event.current;
            bool save = ev.type == EventType.KeyDown && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter);
            bool cancel = ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Escape;
            if (save || cancel) ev.Use();
            save |= GUI.Button(new Rect(220, 145, 94, 32), "Save");
            cancel |= GUI.Button(new Rect(320, 145, 94, 32), "Cancel");
            if (cancel) { Close(); return; }
            if (save)
            {
                string name = DogNaming.Normalize(draft);
                if (name.Length == 0) { error = "Please enter a name."; return; }
                try { Plugin.SaveName(name); Close(); }
                catch (Exception e) { error = "Could not save the name. Please try again."; Plugin.Log.LogError(e); }
            }
        }
    }
}
