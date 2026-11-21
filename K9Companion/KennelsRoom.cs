using System;
using System.Collections.Generic;
using System.Linq;
using HideoutExtensions;
using UnityEngine;

namespace K9Companion
{
    /// <summary>
    /// The K9 kennels room: a Hideout Extensions room of this mod's own (the server mod's hideoutextensions/k9_kennels.json),
    /// off the north wall of Hideout Extensions' east hall. Once it's built every dog lives there, each at its own kennel
    /// along the walls, the active dog too unless you've moved its kennel by hand. Until then the kennels go where they
    /// always have. Hideout Extensions also says where its doorways are, and no kennel is ever put in one.
    /// </summary>
    internal static class KennelsRoom
    {
        internal const string SpotId = "6a5d0c0c4fb9a3c5e1de0001";

        internal static void Hook() => HideoutExtensionsApi.RoomReady += Ready;

        // Built while you're in the hideout, or rebuilt on a visit: the kennels move in.
        private static void Ready(ExtensionRoom room)
        {
            if (room?.Spot?.Id != SpotId) return;
            Plugin.Log.LogInfo("The K9 kennels room is ready: the dogs' kennels move in.");
            KennelResidents.Rehome();
            Plugin.HideoutDog?.KennelsRoomReady();
        }

        /// <summary>The room in the hideout scene right now, or null (not built, or not in the hideout).</summary>
        internal static ExtensionRoom Room
        {
            get
            {
                try
                {
                    foreach (ExtensionRoom r in HideoutExtensionsApi.Rooms)
                        if (r?.Spot?.Id == SpotId && r.Root != null) return r;
                }
                catch (Exception) { }
                return null;
            }
        }

        internal static bool Inside(Vector3 p) => Inside(Room, p, 0);

        private static bool Inside(ExtensionRoom room, Vector3 p, float margin)
        {
            if (room == null) return false;
            Vector3 local = p - room.Root.position;
            return local.x >= -margin && local.z >= -margin && local.x <= room.Size.x + margin && local.z <= room.Size.z + margin && Mathf.Abs(local.y) < 1.5f;
        }

        /// <summary>Inside any of Hideout Extensions' rooms (they're part of the hideout, though outside its own walls).</summary>
        internal static bool InAnyRoom(Vector3 p)
        {
            try { return HideoutExtensionsApi.Rooms.Any(r => r?.Root != null && Inside(r, p, .5f)); }
            catch (Exception) { return false; }
        }

        // ---- the room's kennel spots ----------------------------------------------------------------------------------

        // Along the walls, backs to the wall, facing into the room: the west wall first, then the north, the east, and the
        // south one either side of the doorway. Spots are tried every 40 cm, each against the kennels already down
        // (KennelSpot.Fits keeps them apart and checks the floor and the room for the kennel's level).
        private const float FromWall = .8f, Step = .4f, EndGap = 1.4f;

        /// <summary>A free spot in the room for a kennel at this level (own: the kennel's own name, so it isn't in its own way).</summary>
        internal static bool Find(int level, string own, out Vector3 at, out Quaternion facing)
        {
            at = default; facing = Quaternion.identity;
            ExtensionRoom room = Room;
            if (room == null) return false;
            Vector3 origin = room.Root.position, size = room.Size;
            foreach (var (local, yaw) in Spots(size))
            {
                Vector3 p = origin + local;
                Quaternion face = Quaternion.Euler(0, yaw, 0);
                if (NearDoorway(p) || !KennelSpot.Fits(p, face, level, own)) continue;
                at = KennelSpot.Floor(p, own, out Vector3 floor) ? floor : p;
                facing = face;
                return true;
            }
            Plugin.Log.LogWarning("The K9 kennels room is full: no clear spot along its walls for another kennel.");
            return false;
        }

        private static IEnumerable<(Vector3, float)> Spots(Vector3 size)
        {
            for (float z = EndGap; z <= size.z - EndGap; z += Step) yield return (new Vector3(FromWall, 0, z), 90);            // west wall
            for (float x = EndGap; x <= size.x - EndGap; x += Step) yield return (new Vector3(x, 0, size.z - FromWall), 180);   // north wall
            for (float z = size.z - EndGap; z >= EndGap; z -= Step) yield return (new Vector3(size.x - FromWall, 0, z), 270);   // east wall
            for (float x = EndGap; x <= size.x - EndGap; x += Step) yield return (new Vector3(x, 0, FromWall), 0);              // south wall
        }

        // ---- doorways ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Within reach of one of Hideout Extensions' doorways (its built rooms' doors, openings and links, both sides): a
        /// kennel there would block the way through, or stand in the doorway itself, where a cut wall has no collider.
        /// </summary>
        internal static bool NearDoorway(Vector3 p)
        {
            foreach (Vector4 d in Doorways())
            {
                // Clear of a doorway's sides by a metre, as wide as it is (an ordinary door's 1.2 m when not given).
                float clear = Mathf.Max(1.7f, d.w / 2 + 1);
                float dx = d.x - p.x, dz = d.z - p.z;
                if (dx * dx + dz * dz < clear * clear && Mathf.Abs(d.y - p.y) < 2) return true;
            }
            return false;
        }

        private static readonly List<Vector4> doorways = new List<Vector4>();   // x, y, z, width
        private static float doorwaysAt = -100;

        private static List<Vector4> Doorways()
        {
            if (Time.unscaledTime - doorwaysAt < 2) return doorways;
            doorwaysAt = Time.unscaledTime;
            doorways.Clear();
            try
            {
                var spots = HideoutExtensionsApi.Spots;
                var built = new HashSet<string>(spots.Where(s => s?.State == "built").Select(s => s.Id));
                foreach (SpotInfo s in spots)
                {
                    if (s == null || !built.Contains(s.Id)) continue;
                    float width = s.Door?.Width ?? 0;
                    Add(s.Door?.Position, width); Add(s.Inside?.Position, width);
                    if (s.Opening != null) Add(s.Opening.Position, s.Opening.Width);
                    if (s.MoreDoors != null) foreach (DoorPairInfo m in s.MoreDoors) { Add(m?.Door?.Position, m?.Door?.Width ?? 0); Add(m?.Inside?.Position, m?.Door?.Width ?? 0); }
                    if (s.Links != null) foreach (OpeningInfo l in s.Links) if (l != null && built.Contains(l.From)) Add(l.Position, l.Width);
                }
            }
            catch (Exception) { }
            return doorways;
        }

        private static void Add(float[] p, float width) { if (p != null && p.Length >= 3) doorways.Add(new Vector4(p[0], p[1], p[2], width)); }
    }
}
