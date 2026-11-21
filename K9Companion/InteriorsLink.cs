using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// With Hideout Interiors (1.1.0 or later) installed, each dog's kennel is an area of its decor mode: the bed (with
    /// the shelter over it), the bowls and each upgrade piece can be moved, turned, sized or hidden like the hideout's own
    /// things, and where they're put is kept with that dog's kennel, so they go wherever the kennel goes. Without that
    /// mod nothing here runs (its types are only touched inside a method that's called once it's known to be loaded).
    /// </summary>
    internal static class InteriorsLink
    {
        private const string Id = "jrs.hideoutinteriors";
        private static bool? present;
        private static bool failed;

        private static bool Present => present ?? (present = Chainloader.PluginInfos.ContainsKey(Id)).Value;

        internal static void Area(string key, string name, Transform root)
        {
            if (failed || !Present || root == null) return;
            try { Add(key, name, root); }
            catch (Exception e) { failed = true; Plugin.Log.LogInfo("Hideout Interiors can't move the kennels' parts (that needs Hideout Interiors 1.1.0 or later): " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Add(string key, string name, Transform root) => HideoutInteriors.HideoutInteriorsApi.AddArea(key, name, root);
    }
}
