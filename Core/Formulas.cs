namespace Berwick.Core;

/// <summary>What a part of a formula is, so the Formula Editor can say what may be changed and what is only shown.</summary>
public enum FormulaTermKind
{
    /// <summary>A value from the situation: the unit, its weapon, the target or the terrain.</summary>
    Input,
    /// <summary>A step the game always takes. It cannot be changed here.</summary>
    Fixed,
    /// <summary>A value an exposed rule on the Rules page can change.</summary>
    Editable,
    /// <summary>Worked out from other values.</summary>
    Derived,
    /// <summary>Applies only in some cases.</summary>
    Conditional,
    /// <summary>A limit or rounding step.</summary>
    Limit,
    /// <summary>Not known yet, so the calculator states its assumption.</summary>
    Unknown,
}

/// <summary>One part of a formula, with its unit and, for editable parts, the rule that holds it.</summary>
public sealed record FormulaTerm(string Name, FormulaTermKind Kind, string Unit, string Explanation, int? RuleId = null);

/// <summary>A formula as the game uses it, the evidence for it, and the Mechanics entry it belongs to.</summary>
public sealed record Formula(string Key, string Name, string Expression, IReadOnlyList<FormulaTerm> Terms, IReadOnlyList<string> Limits,
    string Evidence, string Mechanic);

/// <summary>A calculated value for the original disc and for the project, with the steps shown.</summary>
public sealed record FormulaResult(int? Original, int? Current, string OriginalSteps, string CurrentSteps)
{
    public bool Changed => Original != Current;
}

/// <summary>Inputs for a hit rate. Accuracy is as the Items page shows it (precision times 10); weapon level is in stored tenths.</summary>
public sealed record HitInputs(int WeaponAccuracy, int WeaponLevelTenths, int OtherHitBonus, int TargetAttackSpeed, int TargetTerrainAvoid,
    int TargetOtherAvoid, bool Ranged);

/// <summary>Inputs for an equipment failure chance: the item's rank and the level it is compared with.</summary>
public sealed record FailureInputs(int ItemRank, int Level);

/// <summary>Inputs for a shield block chance. Shield level is in stored tenths.</summary>
public sealed record BlockInputs(int ShieldAccuracy, int ShieldLevelTenths, bool Shieldfaire, bool Eligible = true);

/// <summary>
/// The Formula Editor's catalogue: a readable layer over the same exposed rules the Rules page edits. It never writes anything itself;
/// editable parts are changed through their rules, so both pages always show the same project.
/// </summary>
public static class Formulas
{
    public const int HitAddsRule = 103, AvoidSpeedRule = 52, FailureRule = 231, ShieldBlockRule = 9, ShieldfaireRule = 48, ShieldProficiencyTerm = 252, ShieldAccuracyTerm = 253;

    public static IReadOnlyList<Formula> All { get; } =
    [
        new("hit", "Hit rate", "Weapon accuracy + weapon level + other hit bonuses − target's avoid",
        [
            new("Weapon accuracy", FormulaTermKind.Input, "percentage points", "As the Items page shows it: the weapon's precision times 10."),
            new("Weapon level", FormulaTermKind.Input, "weapon levels", "The unit's level in the weapon's type, at most the class's limit. A level of 0 still attacks."),
            new("Hit adds", FormulaTermKind.Editable, "", "What the game adds to accuracy. The game adds the weapon level; the rule can add a stat or nothing.", HitAddsRule),
            new("Whole levels only", FormulaTermKind.Unknown, "", "Levels are stored in tenths. The calculator drops the tenths (8.7 counts as 8); the game's exact rounding is not confirmed."),
            new("Other hit bonuses", FormulaTermKind.Input, "percentage points", "Items, food, supports and skills, entered together."),
            new("Target's avoid", FormulaTermKind.Derived, "percentage points", "Attack speed × the avoid multiplier + terrain avoid (counted twice at range 2 or more) + other avoid."),
            new("Avoid multiplier", FormulaTermKind.Editable, "times", "How many times attack speed counts in avoid. The game uses times 2.", AvoidSpeedRule),
            new("Terrain at range", FormulaTermKind.Conditional, "", "Terrain avoid counts twice when the attack comes from 2 or more hexes away."),
        ],
        ["Skills such as Bane, Miracle, Deadeye and Aim change the result in ways this calculator leaves out.",
         "Whether the game keeps the result between 0 and 100 is not confirmed; the calculator shows the raw value."],
        "Program reading (hit 0x00179620, weapon level 0x0017AD70, avoid 0x00179D30), checked in a running game. A weapon level of 0 still attacks, with very low accuracy (owner test, 2026-11-26).",
        "Hit rate"),
        new("failure", "Equipment failure", "(Item rank − level) × chance per level, when the rank is above the level",
        [
            new("Item rank", FormulaTermKind.Input, "ranks", "The item's rank."),
            new("Level", FormulaTermKind.Input, "levels", "The level the rank is compared with. Which level the game uses here is still to be confirmed in play."),
            new("Chance per level", FormulaTermKind.Editable, "percentage points per level", "The game uses 5.", FailureRule),
            new("Only above the level", FormulaTermKind.Limit, "", "No chance when the rank is at or below the level."),
            new("Advanced, Expert and Limited", FormulaTermKind.Conditional, "", "Skills that change the rank before it is compared; see the Expert and Limited rules. Not included in the calculator."),
        ],
        ["Whether the chance is capped at 100 is not confirmed; the calculator shows the raw value.",
         "The game's routine at 0x0017C880 multiplies the difference; rounding does not arise for whole numbers."],
        "Program reading (0x0017C880; rule 231 sites 0x0017C8D8 and 0x0017C8E0 hold the game's ×5 in both editions). Published as 5% per rank above the unit's level.",
        "Equipment failure"),
        new("block", "Shield block chance", "Scaled shield accuracy + included shield level + skill bonuses; a roll of 0 to 99 below it blocks",
        [
            new("Shield accuracy", FormulaTermKind.Input, "percentage points", "As the Items page shows it."),
            new("Accuracy contribution", FormulaTermKind.Editable, "", "Scale the accuracy term independently. This native binding is experimental.", ShieldAccuracyTerm),
            new("Proficiency contribution", FormulaTermKind.Editable, "", "Include or omit the shield level. This native binding is experimental.", ShieldProficiencyTerm),
            new("Shield level", FormulaTermKind.Input, "levels", "The unit's level with that shield size. A shield counts only when it is not broken and the class can use the size."),
            new("Whole levels only", FormulaTermKind.Unknown, "", "Levels are stored in tenths. The calculator drops the tenths; the game's exact rounding is not confirmed."),
            new("Shieldfaire", FormulaTermKind.Editable, "percentage points", "Added with the Shieldfaire skill. The game uses 18.", ShieldfaireRule),
            new("Shield block mode", FormulaTermKind.Editable, "", "Normal rolls against the chance; Always and Never fix the result only when a shield block roll is eligible.", ShieldBlockRule),
        ],
        ["Small and medium shields share their animations in tested classes; large shields use different ones. Size permission, level and cap are separate."],
        "Program reading (0x00177C70); block roll checked in a running game.",
        "Shield block chance"),
    ];

    public static Formula ByKey(string key) => All.FirstOrDefault(f => f.Key == key) ?? throw new EditException($"There is no formula called \"{key}\".");

    private static int Rule(EditSession session, int id, bool original)
    {
        var rule = GameRules.ById(id);
        if (!session.IsRuleAvailable(id)) throw new EditException($"The game code for {rule.Name} is not recognized on this disc.");
        return original ? session.OriginalValue(EditSession.Rules, id, rule.Key) : session.Value(EditSession.Rules, id, rule.Key);
    }

    private static void RequireRange(int value, int min, int max, string name)
    {
        if (value < min || value > max) throw new EditException($"{name} must be between {min} and {max}.");
    }

    /// <summary>Hit rate for the original disc and the project. Unsupported stat contributions have no numeric result.</summary>
    public static FormulaResult Hit(EditSession session, HitInputs inputs)
    {
        RequireRange(inputs.WeaponAccuracy, 0, 127, "Weapon accuracy");
        RequireRange(inputs.WeaponLevelTenths, 0, 1023, "Weapon level in tenths");
        RequireRange(inputs.TargetAttackSpeed, 0, int.MaxValue, "Target attack speed");
        (int? Value, string Steps) One(bool original)
        {
            int adds = Rule(session, HitAddsRule, original);
            int multiplier = Rule(session, AvoidSpeedRule, original) switch { 0 => 1, 1 => 2, _ => 4 };
            int level = inputs.WeaponLevelTenths / 10;
            string addsName = GameRules.ById(HitAddsRule).Describe(adds);
            if (adds != 0 && addsName != "Nothing")
                return (null, $"Hit adds is set to {addsName}. Its effective stat contribution is not calculated here.");
            int added = adds == 0 ? level : 0;
            int terrain = checked(inputs.Ranged ? inputs.TargetTerrainAvoid * 2 : inputs.TargetTerrainAvoid);
            int avoid = checked(inputs.TargetAttackSpeed * multiplier + terrain + inputs.TargetOtherAvoid);
            int hit = checked(inputs.WeaponAccuracy + added + inputs.OtherHitBonus - avoid);
            string addsText = adds == 0 ? $"weapon level {level}" : "0 (Nothing)";
            return (hit, $"{inputs.WeaponAccuracy} + {addsText} + {inputs.OtherHitBonus} − ({inputs.TargetAttackSpeed} × {multiplier} + {terrain} + {inputs.TargetOtherAvoid}) = {hit}");
        }
        var a = One(true); var b = One(false);
        return new(a.Value, b.Value, a.Steps, b.Steps);
    }

    /// <summary>Equipment failure chance for the original disc and the project.</summary>
    public static FormulaResult Failure(EditSession session, FailureInputs inputs)
    {
        RequireRange(inputs.ItemRank, 0, 63, "Item rank");
        RequireRange(inputs.Level, 0, 63, "Comparison level");
        (int Value, string Steps) One(bool original)
        {
            string per = GameRules.ById(FailureRule).Describe(Rule(session, FailureRule, original));
            int perLevel = per == "Never" ? 0 : int.Parse(per.TrimEnd('%'), System.Globalization.CultureInfo.InvariantCulture);
            int above = Math.Max(0, inputs.ItemRank - inputs.Level);
            int chance = checked(above * perLevel);
            return (chance, $"max(0, {inputs.ItemRank} − {inputs.Level}) × {perLevel} = {chance}");
        }
        var a = One(true); var b = One(false);
        return new(a.Value, b.Value, a.Steps, b.Steps);
    }

    /// <summary>Shield block chance for the original disc and the project; Always and Never show as 100 and 0.</summary>
    public static FormulaResult Block(EditSession session, BlockInputs inputs)
    {
        RequireRange(inputs.ShieldAccuracy, 0, 127, "Shield accuracy");
        RequireRange(inputs.ShieldLevelTenths, 0, 1023, "Shield level in tenths");
        if (!inputs.Eligible) return new(0, 0, "No eligible shield block roll.", "No eligible shield block roll.");
        (int Value, string Steps) One(bool original)
        {
            int mode = Rule(session, ShieldBlockRule, original);
            int bonus = inputs.Shieldfaire ? Rule(session, ShieldfaireRule, original) : 0;
            if (mode == 1) return (100, "Shield block is set to Always");
            if (mode == 2) return (0, "Shield block is set to Never");
            int accuracy = Rule(session, ShieldAccuracyTerm, original) switch
            {
                0 => inputs.ShieldAccuracy, 1 => 0, 2 => inputs.ShieldAccuracy / 2,
                3 => inputs.ShieldAccuracy / 4, 4 => checked(inputs.ShieldAccuracy * 2),
                _ => throw new EditException("Unrecognized shield accuracy contribution.")
            };
            int level = Rule(session, ShieldProficiencyTerm, original) == 0 ? inputs.ShieldLevelTenths / 10 : 0;
            int chance = checked(accuracy + level + bonus);
            return (chance, $"accuracy contribution {accuracy} + shield level contribution {level} + {bonus} = {chance}");
        }
        var a = One(true); var b = One(false);
        return new(a.Value, b.Value, a.Steps, b.Steps);
    }
}
