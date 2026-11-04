using EFT;
using UnityEngine;

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

        // Same as the Recall key: drops any attack, ends sit/stay/lie, and follows again.
        private void Recall(Player owner, bool hideout)
        {
            SetCommand(Command.Follow); audio.Play("whistle", true);
            // In the hideout the dog walks over when it can find a way, and otherwise comes straight to you.
            if (hideout && !Reachable(owner.Transform.position) && FindPoint(owner.Transform.position, out Vector3 p)) Teleport(p, owner.Transform.position);
            Say("Come here!");
            K9.Report("come");
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
}
