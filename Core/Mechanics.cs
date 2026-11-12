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
    private const string Items = "items", Characters = "characters", Classes = "classes", Rules = "rules";

    public static IReadOnlyList<Mechanic> All { get; } = Array.AsReadOnly(new Mechanic[]
    {
        // Damage
        new("Damage", "Physical attack", "Strength + weapon Might",
            "Knives add a random amount from 0 up to Strength instead of all of it. Crossbows and ballistae ignore Strength and use weapon Might alone.",
            MechanicStatus.Published, Wiki,
            [new("Weapon Might", Items), new("Strength bonus", Characters), new("Class Strength", Classes)],
            "The formula itself is not located in the game program yet."),
        new("Damage", "Magic attack", "Mind + spell Might", "Magic uses Mind in place of Strength.",
            MechanicStatus.Published, Wiki,
            [new("Spell Might", Items), new("Mind bonus", Characters), new("Class Mind", Classes)],
            "The formula itself is not located in the game program yet."),
        new("Damage", "Physical defense", "Defense + shield Might (only when the shield blocks) + defense bonuses",
            "A shield adds its Might only on a successful block, so damage taken by a shielded unit swings between two values from hit to hit.",
            MechanicStatus.Published, Wiki,
            [new("Shield Might", Items), new("Defense bonus", Characters), new("Class Defense", Classes), new("Shield block rule", Rules, 1)],
            "Where shield Might is added to defense is not located yet, so a passive shield bonus cannot be offered yet."),
        new("Damage", "Magic defense", "Mind + elemental resistance bonus", "Shields do not help against magic.",
            MechanicStatus.Published, Wiki, [new("Mind bonus", Characters), new("Class Mind", Classes)],
            "Resistance fields on items are not decoded yet."),
        new("Damage", "Damage dealt", "Attack minus the target's defense (never below 0)",
            "Physical and magic damage both subtract the matching defense.",
            MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),

        // Hit and avoid
        new("Hit and avoid", "Accuracy", "Weapon accuracy + weapon level + bonuses",
            "The Accuracy shown on the Items page is the weapon's precision times 10. Weapon level is the character's level in that weapon type.",
            MechanicStatus.Published, Wiki, [new("Weapon accuracy", Items), new("Weapon levels", Characters)],
            "Not located in the game program yet."),
        new("Hit and avoid", "Attack speed", "Agility minus (weapon weight + shield weight minus Strength / 2), with the penalty never below 0",
            "Heavy equipment slows a unit unless its Strength can carry it.",
            MechanicStatus.Published, Wiki, [new("Item weight", Items), new("Agility and Strength", Characters)],
            "Not located in the game program yet."),
        new("Hit and avoid", "Avoid", "Attack speed × 2 + terrain bonus (terrain counts twice at range 2 or more)",
            "Agility is the main source of avoid, which makes it the stat that most changes how often a unit is hit.",
            MechanicStatus.Published, Wiki, [new("Agility bonus", Characters), new("Class Agility", Classes)],
            "Not located in the game program yet."),
        new("Hit and avoid", "Hit rate", "Accuracy minus the target's avoid",
            "Against units with a matching Bane skill or Miracle, hit chances are reduced on a curve rather than by a flat amount.",
            MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Hit and avoid", "Random rolls", "A random number from 0 to 99 compared against the rate",
            "Every chance in the game (hit, shield block, critical and skills) is decided by the same random number routine, found at 0x0013A740 in the program.",
            MechanicStatus.Located, "Decrypted 2005 cheat codes and program reading", [],
            "Single random numbers are used, not the averaged rolls some other tactics games use."),

        // Shields
        new("Shields", "Shield block chance", "Shield accuracy + shield level + bonuses",
            "This works like weapon accuracy, using the shield's own accuracy and the unit's level with that shield size.",
            MechanicStatus.Published, Wiki, [new("Shield accuracy", Items), new("Shield levels", Characters)],
            "Where the rate is calculated is not located yet."),
        new("Shields", "Shield block roll", "Block happens when a random 0 to 99 is below the block chance",
            "The roll is made for the defending unit before damage is calculated. The Shield block rule can make it always or never succeed for everyone.",
            MechanicStatus.Confirmed, "Program reading, checked in game", [new("Shield block rule", Rules, 1)]),

        // Critical hits and injuries
        new("Critical hits and injuries", "Critical rate", "Weapon critical + (weapon level minus 50, never below 0) + bonuses",
            "Weapon level only adds critical chance above 50, so critical hits come mostly from the weapon itself until a unit masters it.",
            MechanicStatus.Published, Wiki, [new("Weapon critical", Items), new("Weapon levels", Characters)],
            "Not located in the game program yet."),
        new("Critical hits and injuries", "Critical damage", "Normal damage + a random 10 to 20", "Critical hits add a flat random amount rather than multiplying damage.",
            MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Critical hits and injuries", "Injury rate", "9 for knives, 3 for other weapons, plus weapon, skill and food bonuses",
            "Injured units fight at a penalty and can be crippled by further damage.",
            MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Critical hits and injuries", "Equipment failure", "5% for each weapon rank above the unit's level (7 levels of slack with Advanced)",
            "Using a weapon above a unit's level risks it failing in use.",
            MechanicStatus.Published, Wiki, [new("Weapon level requirement", Items), new("Character level", Characters)],
            "Not located in the game program yet."),

        // Growth
        new("Growth", "Stat growth", "Expected stat = starting stat + growth rate × levels gained; the actual stat stays within the bracket around it",
            "Growth is not purely random: each level the game keeps a stat near its expected value. The bracket sets how far it may drift: Loose ±3, Tight closer, None unlimited (only Daoud and Lynette).",
            MechanicStatus.Published, "Player write-up (West-East blog), matched against the discs",
            [new("Growth rates and bracket", Characters)], "The level-up routine is located in part; the bracket logic is not yet read."),
        new("Growth", "Empty level ups", "If no stat rises, the game re-rolls for a weapon level; if every weapon level is capped, Luck rises by 1",
            "This keeps level ups from being wasted.", MechanicStatus.Published, Wiki, [], "Not located in the game program yet."),
        new("Growth", "Stat bonus limits", "HP bonus up to 50; Strength, Agility, Defense and Mind bonuses up to 15; Luck up to 20; level up to 30",
            "Bonuses are what a character has above the class base. The limits are written twice in the program, once in the routine that measures a gain and once in the routine that stores it; level ups use the second.",
            MechanicStatus.Located, "Program reading; the measuring copy was tested in game",
            [new("Limit rules (after testing)", Rules)], "Being confirmed in game before being offered as rules."),

        // Experience
        new("Experience", "Experience from a defeat", "Enemy class experience value + a modifier",
            "The class experience value is part of each class record and is editable.",
            MechanicStatus.Published, Wiki, [new("Class experience value", Classes)], "The modifier is not located yet."),
        new("Experience", "Experience after a fight", "Added to the unit after each fight, at most up to the next level (100)",
            "The Experience after a fight rule can replace the game's amount with a fixed one.",
            MechanicStatus.Confirmed, "Program reading, checked in game", [new("Experience after a fight rule", Rules, 2)]),
        new("Experience", "Level limit", "Level 30; experience stops at level 30 and cannot pass 100 at level 29",
            "Three numbers in the program work together to stop growth at level 30.",
            MechanicStatus.Located, "Program reading", [], "Needs a test in game before it can be offered."),

        // Movement
        new("Movement", "Movement range", "Class Move, with terrain costs that depend on the class's movement type",
            "Mounted and dismounted forms are separate classes with their own Move. Changing Move changes how far a unit goes, but not which terrain it can cross or what it costs.",
            MechanicStatus.Published, "Class records on the disc; EmblemWiki class table", [new("Class Move", Classes)],
            "Movement type and terrain costs are not mapped yet."),
    });

    public static string StatusText(MechanicStatus status) => status switch
    {
        MechanicStatus.Confirmed => "Confirmed in game",
        MechanicStatus.Located => "Found in the game program",
        _ => "Published, not yet found in the game program",
    };
}
