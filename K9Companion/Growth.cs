using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// Dogs grow up. One from Dr. Yang's adoption papers comes home young, and grows with every day you have it and every
    /// raid it comes back from (the server's PackKeeper.Growth: grown in 30 days, or 20 raids survived, sooner with both).
    /// A young dog is smaller, with less health and a softer bite, and slower, its gaits going with its size; grown, it's
    /// a little bigger than a real dog of its breed (Breed.AdultScale). Dogs from before, and the K9 unit's shepherd,
    /// are grown.
    /// </summary>
    internal static class Growth
    {
        // Just adopted: this share of its grown size, and of its grown health and bite.
        internal const float YoungSize = .6f, YoungStrength = .6f;

        /// <summary>The active dog's size against its breed's model as made (1 with no roster yet: a grown shepherd).</summary>
        internal static float ActiveScale => Pack.Active?.BodyScale ?? Breed.Shepherd.AdultScale;
        internal static float ActiveStrength => Pack.Active?.Strength ?? 1;

        /// <summary>For the DOGS panel: how far along it is.</summary>
        internal static string Describe(PackDog dog)
        {
            if (dog.Growth >= .999f) return "grown";
            string age = dog.Growth < .34f ? "young" : dog.Growth < .67f ? "growing" : "nearly grown";
            return $"{age} ({Mathf.RoundToInt(dog.Growth * 100)}%)";
        }
    }
}
