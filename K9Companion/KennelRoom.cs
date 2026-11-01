using System.Collections.Generic;
using System.Linq;
using EFT;
using EFT.Hideout;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The kennel's own corner: the room behind the emergency wall, once the wall is broken through (its last stage).
    /// Its floor clutter (stepladder, bunk bed, tyre, box, the torch on the bed) is switched off while the kennel is in
    /// the hideout, and the kennel goes where the bunk bed stood. Nothing is changed otherwise, and the clutter comes
    /// back when the dog leaves the hideout.
    /// </summary>
    internal static class KennelRoom
    {
        private static readonly string[] Clutter = { "bed_metal_bunk", "Ladder", "jcb_Tire_reserve", "Box_closed_01", "flashlight_portable_P" };
        private static readonly List<GameObject> hidden = new List<GameObject>();
        private static bool reported;

        /// <summary>Clears the room; <paramref name="corner"/> is where the bunk bed stood, <paramref name="middle"/> the room's middle.</summary>
        public static bool Prepare(out Vector3 corner, out Vector3 middle)
        {
            corner = middle = default;
            Transform props = Room();
            if (props == null) return false;
            Transform bed = props.Cast<Transform>().First(t => t.name.StartsWith("bed_metal_bunk"));
            // Along the wall from where the bunk bed stood, a little towards the room's door.
            Transform door = props.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Outside_Door_Metal"));
            corner = door != null ? Vector3.Lerp(bed.position, new Vector3(door.position.x, bed.position.y, bed.position.z), .4f) : bed.position;
            foreach (Transform t in props)
            {
                if (!t.gameObject.activeSelf || !Clutter.Any(c => t.name.StartsWith(c))) continue;
                t.gameObject.SetActive(false);
                hidden.Add(t.gameObject);
            }
            var room = props.Cast<Transform>().FirstOrDefault(t => t.name.StartsWith("Basement_Room"))?.GetComponentInChildren<Renderer>(true);
            middle = room != null ? room.bounds.center : corner;
            middle.y = corner.y;
            Physics.SyncTransforms();
            return true;
        }

        // The emergency wall's stage that's switched on and holds the room's furniture: the bunk bed and the stepladder.
        private static Transform Room()
        {
            HideoutArea wall = Object.FindObjectsOfType<HideoutArea>(true).FirstOrDefault(a => a.Data?.Template != null && a.Data.Template.Type == EAreaType.EmergencyWall);
            if (wall == null) { Report("the emergency wall area wasn't found"); return null; }
            foreach (Transform t in wall.GetComponentsInChildren<Transform>(true))
            {
                if (!t.name.StartsWith("bed_metal_bunk") || t.parent == null || !t.parent.gameObject.activeInHierarchy) continue;
                if (t.parent.Cast<Transform>().Any(s => s.name.StartsWith("Ladder"))) return t.parent;
            }
            Report("the room behind the emergency wall isn't open (wall not broken through)");
            return null;
        }

        private static void Report(string why)
        {
            if (reported) return;
            reported = true;
            Plugin.Log.LogInfo("Kennel room not used: " + why + ".");
        }

        public static void Restore()
        {
            foreach (var go in hidden) if (go != null) go.SetActive(true);
            hidden.Clear();
        }
    }
}
