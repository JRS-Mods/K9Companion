using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// With Hideout Extensions installed, the kennel moves from the overview like the hideout's areas: right-click the
    /// paw, and the kennel follows the mouse until it's put down where it fits. Without that mod nothing here runs (its
    /// types are only touched inside methods that are called once it's known to be loaded).
    /// </summary>
    internal static class HideoutExtensionsLink
    {
        private const string Id = "jrs.hideoutextensions";
        private static bool? present;
        private static GameObject offeredIcon;
        private static Transform offeredRoot;
        private static bool failed;

        private static bool Present => present ?? (present = Chainloader.PluginInfos.ContainsKey(Id)).Value;

        /// <summary>Offers the kennel (once per icon and kennel) for moving.</summary>
        internal static void Kennel(GameObject icon, Transform kennel, Companion companion)
        {
            if (failed || !Present || icon == null || kennel == null || companion == null) return;
            if (icon == offeredIcon && kennel == offeredRoot) return;
            try { Offer(icon, kennel, companion); offeredIcon = icon; offeredRoot = kennel; }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("The kennel can't be moved from the overview: " + e.Message); }
        }

        /// <summary>
        /// An icon that's a copy of the Rest Space's but moves nothing (the paw before the kennel is built, the kennel's
        /// entry in the area bar): without this, right-clicking it would pick up the Rest Space.
        /// </summary>
        internal static void Unmovable(GameObject icon, string reason)
        {
            if (failed || !Present || icon == null || refused.Contains(icon)) return;
            try { Refuse(icon, reason); refused.Add(icon); }
            catch (Exception e) { failed = true; Plugin.Log.LogWarning("The kennel's icon couldn't be kept from moving the Rest Space (update Hideout Extensions): " + e.Message); }
        }
        private static readonly System.Collections.Generic.HashSet<GameObject> refused = new System.Collections.Generic.HashSet<GameObject>();

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Refuse(GameObject icon, string reason) => HideoutExtensions.HideoutExtensionsApi.AddUnmovable(icon, reason);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Offer(GameObject icon, Transform kennel, Companion companion)
        {
            HideoutExtensions.HideoutExtensionsApi.AddMovable(new HideoutExtensions.MovableThing
            {
                Name = "K9 Kennel",
                Icon = icon,
                Root = kennel,
                Check = companion.KennelCheck,
                Placed = companion.KennelPlacedByHand,
            });
        }
    }
}
