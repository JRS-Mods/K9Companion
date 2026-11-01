using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Interactive;
using EFT.InventoryLogic;
using UnityEngine;

namespace K9Companion
{
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
}
