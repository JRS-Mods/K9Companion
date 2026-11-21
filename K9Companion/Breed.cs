using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace K9Companion
{
    /// <summary>
    /// What makes one kind of dog: its model and coats, and what the code measured off its animations (gait speeds, jumps,
    /// turns, size). The German shepherd (k9dog.dog, k9dog.png) is the first; the others are in breeds/&lt;id&gt;/, made by
    /// tools/convert_breed.py, with their bones under the shepherd's names. Their gear is fitted to each (breeds/&lt;id&gt;/gear_*.mesh,
    /// tools/convert_gear.py with DOG_BREED) and shares the shepherd's textures.
    /// </summary>
    internal sealed class Breed
    {
        public string Id, Name;
        // The model and its maps; Coats is how many coats it comes in (coat 1 to Coats).
        public string Folder, DogFile, NormalFile, OcclusionFile;
        // The fur strands' cut-out, shared by the coats (null: none, the shepherd).
        public string AlphaFile;
        public int Coats = 1;
        private string coatPattern;
        // Its height against the shepherd's (1): everything sized for the shepherd (hitboxes, jumps it clears, the kennel)
        // is scaled by it.
        public float Size = 1;
        // Grown, its size against its model as made: about a fifth over the top of the real breed's height (the models are
        // made at about a real dog's). A young dog is smaller (Growth).
        public float AdultScale = 1.32f;
        // What its coats are called (coat 1 to Coats), as on Dr. Yang's papers.
        public string[] CoatNames = { "black and tan" };
        public string CoatName(int coat) => CoatNames[Math.Max(1, Math.Min(CoatNames.Length, coat)) - 1];
        // Ground speed (m/s) each in-place gait clip was authored at (Gait order).
        public float[] Authored;
        // How a breed's gaits are played (the shepherd's all 1, and its sneak stretched 5.5 times): see Locomotion.
        //   Cadence  its natural step rate against the clips' (the pack's clips are timed for a big dog; small dogs step
        //            quicker, about as the square root of their size)
        //   Stride   how much further each run and gallop stride carries than the in-place clip shows (feet barely touch
        //            the ground at a gallop, so a little more ground per stride doesn't show as sliding)
        //   Burst    how much quicker its sprint is than its natural gallop
        //   Stretch  how much its sneak can be quickened (a stalk at a normal step rate)
        public float Cadence = 1, Stride = 1, Burst = 1, Stretch = 5.5f;
        // The fastest it plays a gait: past this it looks like a film run fast. The shepherd's clips allow 2.5.
        public float MaxPlayback = 2.5f;
        // Turning on the spot: how far round it has come over the clip (0-1, at 21 even steps).
        public float[] TurnL, TurnR;
        public JumpData StandJump, TrotJump, RunJump;
        // Breed stats against the shepherd's: health, how hard it bites (fewer bites to kill), and how far it smells.
        public float Health = 1, Bite = 1, ScentBonus;

        public sealed class JumpData { public float TakeOff, Land; public float[] Travel; }

        public string CoatFile(int coat) => Path.Combine(Folder, string.Format(coatPattern, Math.Max(1, Math.Min(Coats, coat))));
        // Where this breed's fitted copy of a piece of gear is (the shepherd's is in the main folder).
        public string GearFile(string key) => Path.Combine(Folder, "gear_" + key + ".mesh");
        public bool IsShepherd => Id == ShepherdId;

        public const string ShepherdId = "shepherd";

        // Name, health, bite, scent bonus (m), grown size against the model, and coat names for each breed; the rest comes
        // from breed.json. Grown sizes: about a fifth over the tall end of the breed (Rottweiler 69 cm at the shoulder, Husky
        // 60, Shiba 41, Beagle 41, Pug 33), from each model's height as made.
        private static readonly (string Id, string Name, float Health, float Bite, float Scent, float Adult, string[] Coats)[] Stats =
        {
            ("rottweiler", "Rottweiler", 1.25f, 1.2f, 0, 1.42f, new[] { "black and tan", "black and mahogany", "grey and tan" }),
            ("husky", "Siberian Husky", 1.05f, .9f, 0, 1.39f, new[] { "black and white", "copper", "silver" }),
            ("beagle", "Beagle", .7f, .6f, 6, 1.30f, new[] { "tricolour", "pied", "red and white" }),
            ("shibainu", "Shiba Inu", .75f, .7f, 2, 1.34f, new[] { "light red", "red", "cream" }),
            ("pug", "Pug", .55f, .45f, 0, 1.31f, new[] { "fawn", "apricot", "black" }),
        };

        // The pack's clips were timed for a dog of about this height (m): the same clips play on every breed.
        private const float PackHeight = .70f;

        // How fast to play one of its one-off clips (getting up, lying down, turning on the spot) when the code asks for
        // 'wanted' (tuned on the shepherd's clips). The pack's are already brisk, about half as long as the shepherd's, and
        // played that much quicker on top they snapped from pose to pose; so they're quickened far less. Its doze (lying to
        // sleeping, 0.46 s as made) and waking are slowed, as a dog settles.
        public float Timing(string clip, float wanted)
        {
            if (IsShepherd || !clip.StartsWith("Trans_", StringComparison.Ordinal)) return wanted;
            if (clip == "Trans_Lying_to_Sleeping") return wanted * .5f;
            if (clip == "Trans_Sleeping_to_Lying") return wanted * .75f;
            return wanted > 1 ? 1 + (wanted - 1) * .35f : wanted;
        }

        private static Dictionary<string, Breed> all;
        public static Breed Shepherd => Get(ShepherdId);

        public static IEnumerable<Breed> All { get { Load(); return all.Values; } }

        /// <summary>The breed by its id; the shepherd for an unknown one (or one whose files aren't installed).</summary>
        public static Breed Get(string id)
        {
            Load();
            return id != null && all.TryGetValue(id, out Breed b) ? b : all[ShepherdId];
        }

        public static bool Exists(string id) { Load(); return id != null && all.ContainsKey(id); }

        private static void Load()
        {
            if (all != null) return;
            all = new Dictionary<string, Breed>(StringComparer.OrdinalIgnoreCase);
            string folder = Plugin.AssetFolder;
            all[ShepherdId] = new Breed
            {
                Id = ShepherdId, Name = "German Shepherd", Folder = folder, DogFile = Path.Combine(folder, "k9dog.dog"), coatPattern = "k9dog.png",
                Authored = new[] { .264f, .408f, .753f, 2.884f, 8.01f, 11.348f },
                TurnL = new[] { 0, .061f, .193f, .319f, .409f, .491f, .569f, .646f, .730f, .820f, .900f, .954f, .979f, .991f, .998f, 1, 1, 1, 1, 1, 1 },
                TurnR = new[] { 0, .093f, .278f, .416f, .508f, .588f, .662f, .734f, .810f, .900f, .973f, .992f, .998f, 1, 1, 1, 1, 1, 1, 1, 1 },
                StandJump = new JumpData { TakeOff = .256f, Land = .581f, Travel = new[] { 0f, -.06f, -.105f, .031f, .309f, .608f, .94f, 1.287f, 1.584f, 1.849f, 2.096f, 2.323f, 2.527f, 2.696f, 2.775f, 2.778f, 2.778f } },
                TrotJump = new JumpData { TakeOff = .324f, Land = .824f, Travel = new[] { 0f, .229f, .459f, .711f, .976f, 1.242f, 1.507f, 1.772f, 2.037f, 2.302f, 2.567f, 2.833f, 3.098f, 3.363f, 3.628f, 3.893f, 4.034f } },
                RunJump = new JumpData { TakeOff = .292f, Land = .833f, Travel = new[] { 0f, .457f, .874f, 1.239f, 1.572f, 1.867f, 2.177f, 2.508f, 2.85f, 3.199f, 3.549f, 3.893f, 4.229f, 4.583f, 4.976f, 5.38f, 5.511f } },
            };
            foreach (var s in Stats)
            {
                string dir = Path.Combine(folder, "breeds", s.Id);
                string json = Path.Combine(dir, "breed.json");
                if (!File.Exists(json) || !File.Exists(Path.Combine(dir, "dog.dog"))) continue;
                try
                {
                    JObject o = JObject.Parse(File.ReadAllText(json));
                    var gaits = (JObject)o["gaits"];
                    float G(string clip) => (float)gaits[clip];
                    float[] authored = { G("Loco_Sneak"), G("Loco_WalkSlow"), G("Loco_Walk"), G("Loco_Trot"), G("Loco_Run"), G("Loco_Sprint") };
                    JumpData Jump(string clip)
                    {
                        var j = (JObject)o["jumps"][clip];
                        // At the shepherd's size: the code scales jumps by the dog's (Companion.DogSize).
                        float size = (float)o["size"]["height"] / .796f;
                        return new JumpData { TakeOff = (float)j["takeoff"], Land = (float)j["land"], Travel = j["travel"].Select(t => (float)t / size).ToArray() };
                    }
                    float[] Turn(string clip) => o["turns"][clip]["curve"].Select(t => (float)t).ToArray();
                    float height = (float)o["size"]["height"];
                    int coats = Enumerable.Range(1, 3).Count(k => File.Exists(Path.Combine(dir, "coat" + k + ".jpg")));
                    float cadence = Math.Max(1, Math.Min(1.4f, (float)Math.Sqrt(PackHeight / height)));
                    all[s.Id] = new Breed
                    {
                        Id = s.Id, Name = s.Name, Folder = dir, DogFile = Path.Combine(dir, "dog.dog"), coatPattern = "coat{0}.jpg", Coats = Math.Max(1, coats),
                        NormalFile = Path.Combine(dir, "dog_n.png"), OcclusionFile = Path.Combine(dir, "dog_ao.png"),
                        AlphaFile = File.Exists(Path.Combine(dir, "coat_alpha.png")) ? Path.Combine(dir, "coat_alpha.png") : null,
                        Size = height / .796f, Authored = authored,
                        Cadence = cadence, Stride = 1.12f, Burst = 1.45f, Stretch = 2.2f * cadence, MaxPlayback = 1.75f * cadence,
                        TurnL = Turn("Trans_TurnL90"), TurnR = Turn("Trans_TurnR90"),
                        StandJump = Jump("Jump_Stand"), TrotJump = Jump("Jump_Trot"), RunJump = Jump("Jump_Run"),
                        Health = s.Health, Bite = s.Bite, ScentBonus = s.Scent, AdultScale = s.Adult, CoatNames = s.Coats,
                    };
                }
                catch (Exception e) { Plugin.Log.LogWarning($"Breed {s.Id} couldn't be read ({json}): {e.Message}"); }
            }
        }
    }
}
