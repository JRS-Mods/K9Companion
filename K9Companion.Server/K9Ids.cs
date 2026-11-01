using SPTarkov.Server.Core.Models.Common;

namespace K9Companion.Server;

/// <summary>
/// Every id the K9 expansion adds. All are 24 hex digits under the mod's own prefix; the 20th digit says what kind of
/// thing it is (1 quest, 2 condition, 3 reward, 4 reward item, 5 trader offer, 6 item template, 7 hideout improvement,
/// 8 counter part), so nothing here can collide with the 0.15 ids (which have a 0 there) or with each other.
/// </summary>
public static class K9Ids
{
    private const string Prefix = "6a5d0c0c4fb9a3c5e1d";

    public static string Make(int kind, int a, int b) => $"{Prefix}{kind:x1}{a:x2}{b:x2}";
    public static MongoId Quest(int n) => new(Make(1, 0, n));
    public static string Condition(int quest, int index) => Make(2, quest, index);
    public static string Reward(int quest, int index) => Make(3, quest, index);
    public static string RewardItem(int quest, int index) => Make(4, quest, index);
    public static string CounterPart(int quest, int index) => Make(8, quest, index);
    public static MongoId Offer(int n) => new(Make(5, 0, n));
    public static MongoId Template(int n) => new(Make(6, 0, n));

    // ---- the questline (1-10) and the contracts (0x11-0x15) ----------------------------------------------------
    public const int ManBestFriend = 1, StraysOfTarkov = 2, FamiliarScent = 3, MissingUnit = 4, TrustIsEarned = 5,
        LearningTheRopes = 6, WorkingDog = 7, TeethAndSteel = 8, NoDogLeftBehind = 9, LastHandler = 10;
    public const int DailyRations = 0x11, SafeReturn = 0x12, SupplyRun = 0x13, WorkingPartnership = 0x14, ThePack = 0x15;
    // The story, five quests since 0.30: Strays of Tarkov and A Familiar Scent became part of The Missing Unit,
    // Learning the Ropes part of Teeth and Steel, No Dog Left Behind part of The Last Handler, and Working Dog (the
    // dog's loot search) went. Their numbers aren't reused (profiles may still name them).
    public static readonly int[] Story = { ManBestFriend, MissingUnit, TrustIsEarned, TeethAndSteel, LastHandler };
    public static readonly int[] RetiredStory = { StraysOfTarkov, FamiliarScent, LearningTheRopes, WorkingDog, NoDogLeftBehind };
    public static readonly int[] Contracts = { DailyRations, SafeReturn, SupplyRun, WorkingPartnership, ThePack };

    // ---- new item templates -----------------------------------------------------------------------------------
    public static readonly MongoId K9Tag = Template(0x01), HandlerJacket = Template(0x02), DogWhistle = Template(0x03),
        TransportRecords = Template(0x04), VetSupplyCase = Template(0x05), K9Insignia = Template(0x06),
        VetKit = Template(0x10), EliteHarness = Template(0x20), ArmourLocker = Template(0x30), PremiumLocker = Template(0x31);
    // The camera vest, head gear and the clip-on camera (DogGear.Kits).
    public static readonly MongoId CameraVest = Template(0x21), GogglesTan = Template(0x22), GogglesBlack = Template(0x23),
        CameraHeadset = Template(0x24), ActionCamera = Template(0x25);
    // Dog supplies sold by loyalty level (see DrYang).
    public static readonly MongoId Treats = Template(0x11), PremiumFood = Template(0x12), BedLiner = Template(0x13), ThermalBed = Template(0x14),
        TrainingKit = Template(0x15), TacticalCollar = Template(0x16);
    // The other two collars (the tactical collar is the third): the dog wears one in its collar slot (DogGear.Collars).
    public static readonly MongoId TrainingCollar = Template(0x17), BlackCollar = Template(0x18);
    // Licence-built Asian copies of the game's own weapons (K9Shop): Template(0x40 + n).
    public static MongoId Weapon(int n) => Template(0x40 + n);

    // ---- the kennel: three improvements of the hideout's Rest Space ---------------------------------------------
    public static readonly MongoId[] Kennel = { new(Make(7, 0, 1)), new(Make(7, 0, 2)), new(Make(7, 0, 3)) };

    // ---- vanilla templates --------------------------------------------------------------------------------------
    public static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    public const string Water06 = "5448fee04bdc2dbc018b4567", Aquamari = "5c0fa877d174af02a012e1cf", WaterRation = "60098b1705871270cd5352a1";
    public const string Ai2 = "5755356824597772cb798962", CarKit = "590c661e86f7741e566b646a", Salewa = "544fb45d4bdc2dee738b4568",
        Ifak = "590c678286f77426c9660122", Afak = "60098ad7c2240c0fe85c570a", Grizzly = "590c657e86f77412b013051d";
    public const string Bloodset = "5b4335ba86f7744d2837a264", Saline = "59e3606886f77417674759a5", MedTools = "619cc01e0a7c3a1a2731940c",
        Syringe = "5d1b3f2d86f774253763b735", Ophthalmoscope = "5af0534a86f7743b6f354284";
    public const string CorrugatedHose = "59e35cbb86f7741778269d83", Bolts = "57347c5b245977448d35f6e1", ScrewNuts = "57347c77245977448d35f6e2",
        Toolset = "590c2e1186f77425357b6124", MetalParts = "61bf7b6302b3924be92fa8c3", MilitaryCable = "5d0375ff86f774186372f685",
        Ledx = "5c0530ee86f774697952d952";
    public const string Fleece = "5e2af47786f7746d404f3aaa", Ripstop = "5e2af4a786f7746d3f3c3400", Paracord = "5c12688486f77426843c7d32",
        Aramid = "5e2af4d286f7746d4159f07a", Cordura = "5e2af41e86f774755a234b67", Kektape = "5e2af29386f7746d4159f077";

    // ---- maps ---------------------------------------------------------------------------------------------------
    public const string Customs = "56f40101d2720b2a4d8b45d6", Woods = "5704e3c2d2720bac5b8b4567",
        Reserve = "5704e5fad2720bc05b8b4567", Streets = "5714dc692459777137212e12", AnyMap = "any";
}
