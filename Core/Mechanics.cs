namespace Berwick.Core;

public enum MechanicStatus
{
    /// <summary>From published community tables; not yet found in the game program.</summary>
    Published,
    /// <summary>Found in the game program by reading its code; not yet checked in a running game.</summary>
    Located,
    /// <summary>Checked in a running game.</summary>
    Confirmed,
}

/// <summary>A place in the editor where part of a mechanic can be changed.</summary>
public sealed record MechanicLink(string Label, string Page, int? RuleId = null);

public sealed record Mechanic(string Section, string Name, string Formula, string Explanation, MechanicStatus Status, string Source,
    IReadOnlyList<MechanicLink> Changes, string? NotYetEditable = null);

/// <summary>How Berwick Saga's systems work, as far as they are known, and what can be changed today.</summary>
public static class Mechanics
{
    private const string Wiki = "EmblemWiki, Berwick Saga Calculations";
    private const string Items = "items", Characters = "characters", Classes = "classes", Movement = "movement", TerrainPage = "terrain", SkillsPage = "skills", Rules = "rules", Furniture = "furniture";

    public static IReadOnlyList<Mechanic> All { get; } = Array.AsReadOnly(new Mechanic[]
    {
        // Damage
        new("Damage", "Physical attack", "Strength + weapon Might",
            "Knives add a random amount from 0 up to Strength instead of all of it. Crossbows and ballistae ignore Strength and use weapon Might alone. The stat it uses can be changed on the Rules page.",
            MechanicStatus.Confirmed, "Program reading (attack 0x0017BEE0, stat call 0x0017C00C), checked in a running game",
            [new("Weapon Might", Items), new("Strength bonus", Characters), new("Class Strength", Classes)],
            "The formula itself is not located in the game program yet."),
        new("Damage", "Magic attack", "Mind + spell Might", "Magic uses Mind in place of Strength.",
            MechanicStatus.Published, Wiki,
            [new("Spell Might", Items), new("Mind bonus", Characters), new("Class Mind", Classes)],
            "The formula itself is not located in the game program yet."),
        new("Damage", "Physical defense", "Defense + defense bonuses; a successful shield block also takes the shield's Might off the hit",
            "A shield helps only on a successful block, so damage taken by a shielded unit swings between two values from hit to hit. The Shield guard rule can make part of the Might count on failed blocks too.",
            MechanicStatus.Confirmed, "Program reading, checked in a running game",
            [new("Shield Might", Items), new("Defense bonus", Characters), new("Class Defense", Classes), new("Shield block rule", Rules, 9), new("Shield guard rule", Rules, 10)]),
        new("Damage", "Magic defense", "Mind + elemental resistance bonus", "Shields do not help against magic. The equipment window shows it as Mag. The stat it uses can be changed on the Rules page.",
            MechanicStatus.Confirmed, "Program reading (0x0017B690), checked in a running game", [new("Mind bonus", Characters), new("Class Mind", Classes)]),
        new("Damage", "Damage dealt", "Attack − defense − block reduction (never below 0), plus magic attack − magic defense for weapons that deal both",
            "The block reduction is the shield's Might on a successful block. Axe Guard (the weapon's Might, at a chance of weapon level + 25) or the Shieldaxe (10, at a 33% chance) can take its place; only one applies. Knives and some weapon effects ignore Defense completely, and certain weapons ignore it against armored units. Whether a hit that does not get past defense deals 0 or 1 can be changed on the Rules page.",
            MechanicStatus.Confirmed, "Program reading (damage routine at 0x0022A2D0, minimum at 0x0022AEC4), checked in a running game", [new("Minimum damage", Rules, 107)]),

        // Hit and avoid
        new("Hit and avoid", "Accuracy", "Weapon accuracy + weapon level + bonuses",
            "The Accuracy shown on the Items page is the weapon's precision times 10. Weapon level is the character's level in that weapon type.",
            MechanicStatus.Confirmed, "Program reading (0x00179620), checked in a running game", [new("Weapon accuracy", Items), new("Weapon levels", Characters), new("Hit adds", Rules, 103)]),
        new("Hit and avoid", "Attack speed", "Agility minus (weapon weight + shield weight minus Strength / 2), with the penalty never below 0",
            "Heavy equipment slows a unit unless its Strength can carry it. Both stats and the share of Strength can be changed on the Rules page.",
            MechanicStatus.Confirmed, "Program reading (0x0017C330), checked in a running game", [new("Item weight", Items), new("Agility and Strength", Characters)],
            "Not located in the game program yet."),
        new("Hit and avoid", "Avoid", "Attack speed × 2 + terrain bonus (terrain counts twice at range 2 or more)",
            "Agility is the main source of avoid, which makes it the stat that most changes how often a unit is hit.",
            MechanicStatus.Confirmed, "Program reading (0x00179D30), checked in a running game", [new("Attack speed in avoid", Rules, 52), new("Terrain avoid", TerrainPage), new("Agility bonus", Characters)]),
        new("Hit and avoid", "Hit rate", "Weapon accuracy + weapon level + item, food and skill bonuses, minus the target's avoid",
            "The weapon level is the unit's level in the weapon's type, at most the class's limit; Reese's 78 hit with a Rapier is its 70 accuracy plus sword level 8. Against units with a matching Bane skill or Miracle, hit chances are reduced on a curve rather than by a flat amount. What hit adds can be changed on the Rules page.",
            MechanicStatus.Confirmed, "Program reading (hit 0x00179620, weapon level 0x0017AD70), checked in a running game", [new("Hit adds", Rules, 103)]),
        new("Hit and avoid", "Random rolls", "A random number from 0 to 99 compared against the rate",
            "Every chance in the game (hit, shield block, critical and skills) is decided by the same random number routine, found at 0x0013A740 in the program.",
            MechanicStatus.Located, "Decrypted 2005 cheat codes and program reading", [],
            "Single random numbers are used, not the averaged rolls some other tactics games use."),

        // Shields
        new("Shields", "Shield block chance", "Shield accuracy + shield level + skill bonuses",
            "This works like weapon accuracy, using the shield's own accuracy and the unit's level with that shield size. A shield only counts when it is not broken and the class can use that size.",
            MechanicStatus.Located, "Program reading (0x00177C70)", [new("Shield accuracy", Items), new("Shield levels", Characters), new("Shield sizes", Classes)]),
        new("Shields", "Shield block", "Rolled when a hit would do damage: a random 0 to 99 below the block chance blocks, and the shield's Might comes off the hit",
            "Knives and a few weapon effects slip past shields even when the Shield block rule is Always. Shield guard adds part of the Might to Defense on a failed block roll, but not when a successful roll is later canceled. Attacks that ignore Defense, including knives, ignore this bonus too.",
            MechanicStatus.Confirmed, "Program reading, checked in a running game", [new("Shield block rule", Rules, 9), new("Shield guard rule", Rules, 10)]),
        new("Shields", "Shield training", "After a block roll: 1 or 2 training rolls (2 below level 10), plus 1 more if the block succeeded; each passes when random < growth × 10 and random < 190 − 3 × level, and adds 0.4 of a level",
            "Shield levels rise only from attacks that trigger a block roll, so a unit with Vantage that kills its attacker first, or one too sturdy to be hurt, gets no shield training. The Vantage rule can give such a unit one training roll per battle. At level 20 and above a failed block trains nothing. A unit with no shield growth never trains. The gain stops at the class's shield level cap.",
            MechanicStatus.Confirmed, "Program reading (0x001716D0, granted at 0x00228070), checked in a running game", [new("Shield training rule", Rules, 11), new("Failed attempts rule", Rules, 14), new("Vantage rule", Rules, 15), new("Weapon growth", Characters)]),
        new("Shields", "Parry", "With the Parry skill: a chance based on weapon level cancels the hit completely",
            "Parry uses the unit's weapon, not its shield. A parry can wear out the weapon.",
            MechanicStatus.Confirmed, "Program reading, checked in a running game", [new("Parry rule", Rules, 1)]),

        // Critical hits and injuries
        new("Critical hits and injuries", "Critical rate", "Weapon critical + (weapon level minus 50, never below 0) + bonuses",
            "Weapon level only adds critical chance above 50, so critical hits come mostly from the weapon itself until a unit masters it.",
            MechanicStatus.Confirmed, "Program reading (critical 0x00179250, weapon level part 0x0017A1F0), checked in a running game", [new("Weapon critical", Items), new("Weapon levels", Characters), new("Critical adds", Rules, 104)]),
        new("Critical hits and injuries", "Critical damage", "Normal damage + a random 10 to 20", "Critical hits add a flat random amount rather than multiplying damage.",
            MechanicStatus.Confirmed, "Program reading (0x0022AEE8), checked in a running game", [new("Critical bonus rule", Rules, 13)]),
        new("Critical hits and injuries", "Injury rate", "9 for knives, 3 for other weapons, plus weapon, skill and food bonuses",
            "Injured units fight at a penalty and can be crippled by further damage.",
            MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Critical hits and injuries", "Equipment failure", "5% for each weapon rank above the unit's level (7 levels of slack with Advanced)",
            "Using a weapon above a unit's level risks it failing in use.",
            MechanicStatus.Published, Wiki, [new("Weapon level requirement", Items), new("Character level", Characters)],
            "Not located in the game program yet."),

        new("Prices", "Mercenary fee", "Base fee + level × fee per level (Volo 200 + 150 per level, Faramir 1,200 + 50 per level)",
            "Eighteen characters can be hired as mercenaries. When both values are 0 the game picks a random fee instead (Saphira). Some hires cost half; the game marks this for each mercenary.",
            MechanicStatus.Confirmed, "Program reading (fee table at 0x0042D450, fee 0x00170480), checked in a running game", [new("Mercenary fee", Characters)]),

        new("Prices", "Furniture prices", "Art: \"Hope\" 14,500, Bird Statue 17,000, Big Rug 19,000, Art: \"Revival\" 22,000, Sword Mount 23,000, Porcelain Vase 25,000, Deer Mount 26,000, Coat of Arms 27,500, Coffers 30,000, Luxury Bed 32,000, Treasure Chest 36,000, Maiden Statue 42,000, Tapestry 45,000",
            "The furniture shop asks an event script for each piece's price, the same way the stable does for horses. The shop charges the price set here. Its list of pieces still shows the original prices, because those numbers are part of the list's own text, which cannot be edited yet.",
            MechanicStatus.Confirmed, "Event script 727 (stored in several copies on each disc), checked in a running game", [new("Furniture prices", Furniture)]),

        // Partners
        new("Partners", "Partner bonus", "For each partner within 2 spaces: hit + bond ÷ 10; critical + (total of those amounts) ÷ 5",
            "Sixteen characters have up to four partners, each with a bond value (Ward and Reese 40, Lynette and Reese 100). The partner has to be on the map and able to act. The game reloads each character's partners from this table whenever units are loaded, so edits also reach existing saves.",
            MechanicStatus.Confirmed, "Program reading (partner table at 0x00173C80, hit 0x001B7F70, critical 0x001B79D0), checked in a running game", [new("Partners", Characters)]),

        // Growth
        new("Growth", "Stat growth", "Expected stat = starting stat + growth rate × levels gained; the actual stat stays within the bracket around it",
            "Growth is not purely random: each level the game keeps a stat near its expected value. The bracket sets how far it may drift: Loose ±3, Tight closer, None unlimited (only Daoud and Lynette).",
            MechanicStatus.Published, "Player write-up (West-East blog), matched against the discs",
            [new("Growth rates and bracket", Characters)], "The level-up routine is located in part; the bracket logic is not yet read."),
        new("Growth", "Weapon training", "After an attack: 1 or 2 training rolls (2 below level 10), plus 1 more on a hit; each passes when random < growth × 10 and random < 190 − 3 × level, and adds 0.1 of a level",
            "Same routine as shield training with a smaller step. At level 20 and above a miss trains nothing. The gain stops at the class's weapon level cap.",
            MechanicStatus.Confirmed, "Program reading (0x001716D0, granted at 0x00229FB0), checked in a running game", [new("Weapon training rule", Rules, 12), new("Failed attempts rule", Rules, 14), new("Weapon growth", Characters)]),
        new("Growth", "Empty level ups", "If no stat rises, the game re-rolls for a weapon level; if every weapon level is capped, Luck rises by 1",
            "This keeps level ups from being wasted.", MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Growth", "Stat bonus limits", "HP bonus up to 50; Strength, Agility, Defense and Mind bonuses up to 15; Luck up to 20",
            "Bonuses are what a character has above the class base. Each limit is written twice in the program, once where a gain is measured and once where it is stored; level ups use the second, and the limit rules change both. Strength, Agility, Defense and Mind cannot go above 15 because the game stores them in a small field.",
            MechanicStatus.Confirmed, "Program reading, checked in game",
            [new("HP bonus limit", Rules, 3), new("Strength bonus limit", Rules, 4), new("Agility bonus limit", Rules, 5),
             new("Luck limit", Rules, 6), new("Defense bonus limit", Rules, 7), new("Mind bonus limit", Rules, 8)]),

        // Skills
        new("Skills", "Iaido and Knock Away", "Half the weapon level + 40 (Iaido, sword attacks) or + 30 (Knock Away), rolled against 0 to 99",
            "A unit with a high weapon level activates these skills on almost every attack.",
            MechanicStatus.Confirmed, "Program reading (0x00229E40, 0x0022788C), checked in a running game", [new("Iaido base chance", SkillsPage, 40), new("Knock Away base chance", SkillsPage, 41)]),
        new("Skills", "Bane skills", "Attacker's hit rate, at most 33%",
            "Spearbane, Swordbane, Axebane, Arrowbane and Magicbane each answer one kind of weapon, so a defender facing an accurate attacker gets the full 33%.",
            MechanicStatus.Confirmed, "Program reading (0x0022753C to 0x002277C0), checked in a running game", [new("Bane skill chances", SkillsPage, 42)]),
        new("Skills", "Parry chance", "(Weapon level + 10) ÷ 2, only with the Parry skill",
            "A parry cancels the hit completely.",
            MechanicStatus.Confirmed, "Program reading (0x00177EC0), checked in a running game", [new("Parry chance bonus", SkillsPage, 50), new("Parry rule", Rules, 1)]),
        new("Skills", "Active skill bonuses", "Aim +33 hit, Focus Chant +30 hit, Desperation +22 hit and no defense, Throw +10 hit, Deadeye -15 hit, Sunder +25 critical, Pulverize doubles attack",
            "These apply only to the attack the skill is used for.",
            MechanicStatus.Located, "Program reading (battle setup 0x0022C060 to 0x0022C1B4, equipment window 0x00389C68); Aim, Deadeye, Sunder and Desperation checked in a running game",
            [new("Aim hit bonus", SkillsPage, 55), new("Deadeye hit change", SkillsPage, 56), new("Sunder critical bonus", SkillsPage, 53), new("Desperation hit bonus", SkillsPage, 54)]),
        new("Skills", "Commands after moving", "Aim, Deadeye, Sunder, Pulverize, Evasion and others only before moving; Astra, Double Shot, Guard and others before or after; Throw, Mercy, Desperation and others after moving unless the weapon rules it out",
            "Each command skill has its own check in the command menu, so each can be changed on its own.",
            MechanicStatus.Confirmed, "Program reading (command menu 0x0038B400 to 0x0038CB00), checked in a running game", [new("Aim after moving", SkillsPage, 60)]),
        new("Skills", "Command waits", "After use: Astra and Focus Chant wait 6 turns, Triple Shot and Evasion 4, Double Shot, Imbue and Flourish 3",
            "The wait counts down once per turn of the unit; the command returns when it reaches zero.",
            MechanicStatus.Confirmed, "Program reading (0x001C34A0, countdown 0x001B6F10), checked in a running game", [new("Evasion wait", SkillsPage, 90)]),
        new("Skills", "Steal count", "3 steals per map (2 with Mug II), shared by Steal, Robbery and Mug II",
            "Each steal uses one; when none are left the command disappears for that map.",
            MechanicStatus.Confirmed, "Program reading (count set at 0x001C3850, lowered at 0x001DF1BC), checked in a running game", [new("Steal limit", SkillsPage, 94)]),
        new("Skills", "Swimmer", "Water costs the same movement as plain ground, mounted or on foot",
            "The same movement costs are used by your units and by enemies.",
            MechanicStatus.Confirmed, "Program reading (movement cost builder 0x001BA870), checked in a running game", [new("Swimmer while mounted", SkillsPage, 93)]),
        new("Skills", "Pursuit", "An extra attack when attack speed beats the enemy's by 5 or more",
            "Without Pursuit a unit never attacks twice in one fight.",
            MechanicStatus.Confirmed, "Program reading (0x00225450), checked in a running game", [new("Pursuit speed lead", SkillsPage, 47)]),
        new("Skills", "Shieldfaire and Paragon", "Shieldfaire: block chance + 18. Paragon: experience + 20%",
            "Both are plain bonuses on top of the normal formula.",
            MechanicStatus.Confirmed, "Program reading (0x00177E44, 0x00173DAC), checked in a running game", [new("Shieldfaire block bonus", SkillsPage, 48), new("Paragon experience bonus", SkillsPage, 49)]),
        // Prices
        new("Prices", "Stable prices", "Thin Horse 1,000, Plain 2,500, Spry 5,000, Tough 8,000, Strong 15,000, Ish's Stallion 15,000, Sinon Stallion 12,000, Liga Destrier 18,000, Liga Courser 20,000, Morach 24,000, Ish's Courser 10,000",
            "The stable does not use a horse's base price. An event script returns the price, and the stable menu, the shopkeeper's line and the payment all use it.",
            MechanicStatus.Confirmed, "Event script 602 (three copies on each disc), checked in a running game", [new("Stable price (horse items)", Items)]),
        // Experience
        new("Experience", "Experience from a defeat", "Enemy class experience value + level difference bonus (-40 at 8 or more levels below up to +15 at 7 or more above), +20% with the experience skill, then kept between 0 and 100",
            "Only defeating an enemy gives battle experience. The level difference bonus, the highest amount and the class experience values are all editable.",
            MechanicStatus.Confirmed, "Program reading (0x00174050, table at 0x00429F10), checked in a running game", [new("Class experience value", Classes), new("Experience formula", Rules, 20)]),
        new("Experience", "Experience from a staff", "Staff level + 6 + random 0 to 3 - unit level ÷ 5, at least 0, then the skill bonus",
            "A higher level healer gains less from the same staff.",
            MechanicStatus.Confirmed, "Program reading (0x00224340), checked in a running game", [new("Staff experience", Rules, 37)]),
        new("Experience", "Experience after a fight", "Added to the unit after each fight, at most up to the next level (100)",
            "The Experience after a fight rule can replace the game's amount with a fixed one.",
            MechanicStatus.Confirmed, "Program reading, checked in game", [new("Experience after a fight rule", Rules, 2)]),
        new("Experience", "Level limit", "Level 30; experience stops at level 30 and cannot pass 100 at level 29",
            "Three numbers in the program work together to stop growth at level 30.",
            MechanicStatus.Located, "Program reading", [], "Needs a test in game before it can be offered."),

        // Movement
        new("Movement", "Movement range", "A unit can enter a hex while (costs paid so far + terrain cost) <= Move",
            "Each hex costs points by its terrain and by the class's movement type. Costs are kept in tenths of a point, so 1.5 works. Mounted and dismounted forms are separate classes with their own Move and movement type.",
            MechanicStatus.Confirmed, "Terrain cost table in the game data, checked in a running game", [new("Class Move", Classes), new("Terrain costs", Movement)]),
        new("Movement", "Movement type", "Class record: one of 14 movement types (Cavalry, Horseman, Flying, Knight, Armor, Infantry, Thief, Civilian, Special, Snow, War priest, Fighter, Priest, and one unused)",
            "Every class points to one row of the terrain cost table. Changing it changes where the class can go: a class given Flying crosses water and cliffs. The mount state (on foot, mounted, flying) is stored separately and always agrees with the type on the original discs.",
            MechanicStatus.Confirmed, "Class records and terrain table on the disc, checked in a running game", [new("Class movement type", Classes), new("Terrain costs", Movement)]),
    });

    /// <summary>Published terrain costs (gamekouryaku.com terrain list). Null means the terrain cannot be entered.</summary>
    public static readonly string[] TerrainColumns = ["Cavalry", "Horseman", "Flying", "Knight", "Armor", "Infantry", "Fighter", "Thief", "Priest", "Civilian", "Special"];

    public sealed record TerrainCost(string Terrain, int Avoid, int?[] Costs, string Note = "");

    public static IReadOnlyList<TerrainCost> Terrain { get; } = Array.AsReadOnly(new TerrainCost[]
    {
        new("Road", 1, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]),
        new("Plain", 2, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2]),
        new("Grassland", 3, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2]),
        new("Thicket", 3, [2, 1, 1, 1, 1, 1, 1, 1, 1, 1, 2]),
        new("Bridge", 0, [1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1]),
        new("Floor", 0, [null, null, null, 1, 1, 1, 1, 1, 1, 1, 1]),
        new("Gate", 0, [1, 1, null, 1, 1, 1, 1, 1, 1, 1, 1], "Break it or use Locktouch"),
        new("Wasteland", 5, [3, 2, 1, 2, 2, 2, 1, 1, 2, 2, null]),
        new("Sand", 0, [4, 3, 1, 3, null, 2, 1, 1, 1, 2, null]),
        new("Shallows", 0, [4, 3, 1, 3, null, 2, 1, 1, 2, 2, null]),
        new("Poison swamp", 0, [4, 3, 1, 2, null, 2, 2, 1, 2, 2, null], "Recovery while resting minus 20%"),
        new("Snow road", 0, [2, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2]),
        new("Snowfield", 0, [2, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2]),
        new("Forest", 9, [4, 3, 1, 2, 2, 2, 2, 1, 2, 3, 2]),
        new("Snow forest", 10, [7, 6, 1, 4, 4, 4, 3, 2, 3, 3, 2]),
        new("Mountain foot", 8, [6, 4, 1, 4, null, 3, 2, 2, 3, 3, 2]),
        new("Cliff", 0, [null, null, 1, 4, null, 3, 2, 2, 3, 3, null], "Avoid 0 without Climber"),
        new("Cave (open)", 6, [2, 2, 1, 2, 2, 2, 2, 2, 2, 2, null]),
        new("Cave (narrow)", 2, [null, null, null, 1, 1, 1, 1, 1, 1, 1, null]),
        new("House", 5, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null], "Search finds items"),
        new("Shop", 5, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null]),
        new("Church", 5, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null], "Waiting restores HP each turn"),
        new("Lighthouse", 6, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null], "Unit vision +3"),
        new("Guardhouse", 6, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null], "Operates the drawbridge"),
        new("Manor", 7, [null, null, 1, 2, 2, 2, 2, 2, 2, 2, null]),
        new("Temple", 8, [2, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2]),
        new("Castle", 10, [2, 2, 1, 2, 2, 2, 2, 2, 2, 2, 2]),
        new("Throne", 10, [null, null, null, 2, 2, 2, 2, 2, 2, 2, null]),
        new("Drawbridge", 0, [null, null, 1, null, null, null, null, null, null, null, null], "Lowered from a guardhouse"),
        new("Jungle", 0, [null, null, 1, null, null, null, null, null, null, null, null]),
        new("Mountain", 0, [null, null, 1, null, null, null, null, null, null, null, null]),
        new("River", 12, [null, null, 1, null, null, null, null, null, null, null, null], "Swimmer lets a unit cross"),
        new("Sea or lake", 15, [null, null, 1, null, null, null, null, null, null, null, null]),
        new("Wall", 0, [null, null, null, null, null, null, null, null, null, null, null]),
        new("Door", 0, [null, null, null, null, null, null, null, null, null, null, null], "Break it or use Locktouch"),
    });

    public static string StatusText(MechanicStatus status) => status switch
    {
        MechanicStatus.Confirmed => "Confirmed in game",
        MechanicStatus.Located => "Found in the game program",
        _ => "Published, not yet found in the game program",
    };
}
