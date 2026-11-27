namespace Berwick.Core;

public static partial class GameRules
{
    public const string ShieldChanceGroup = "Shield block chance formula";
    public const string FurnitureAssignmentGroup = "Furniture effect assignment (experimental)";
    // Keep the original three indices stable for existing saved projects.
    public static int[] FurnitureTriggerPieces => FurnitureTriggerData.Pieces;
    private static uint[] FurnitureTriggerWords => FurnitureTriggerData.Words;
    public static int? OriginalFurnitureEffectFlag(int piece)
    {
        int index = Array.IndexOf(FurnitureTriggerPieces, piece);
        return index < 0 ? null : (int)(FurnitureTriggerWords[index] & 0xffff);
    }
    // Separate type initialization avoids a dependency on partial-class field order.
    private static class FurnitureTriggerData
    {
        internal static readonly int[] Pieces = [1, 5, 11, 2, 4, 8, 10, 13, 9];
        internal static readonly uint[] Words = [0x24040E74u, 0x24040E75u, 0x24040E79u, 0x24040E76u, 0x24040E77u, 0x24040E7Cu, 0x24040E7Du, 0x24040E7Au, 0x24040E78u];
    }
    public static readonly IReadOnlyDictionary<int, int> FurnitureTriggerRules = new Dictionary<int, int>
    {
        [1] = 256, [5] = 257, [11] = 258, [2] = 259, [4] = 260, [8] = 261, [10] = 262, [13] = 263, [9] = 266,
    };
    private static IEnumerable<GameRule> FormulaTermRules()
    {
        // Preserve the eligibility checks and calls, replacing only the accumulation of each term.
        yield return ShieldTerm(252, "shieldChanceLevel", "Shield proficiency contribution", 0x00177E70);
        yield return ShieldAccuracyTerm();
        yield return MugLevelTerm(254, "Mug", "mugLevelTerm", [0x00227B3C, 0x00227B40, 0x00227B44], 2);
        yield return MugLevelTerm(255, "Mug+", "mug2LevelTerm", [0x0022DE30, 0x0022DE38, 0x0022DE3C], 3);
        // The trigger instructions and their original flag identities are in both edition traces.
        // The recipient and story flag gates stay untouched. This moves an existing effect; it does not create one.
        yield return FurnitureTrigger(256, "furnitureWeaponGrowthTrigger", "Weapon growth bonus", [0x0017143C], 0);
        yield return FurnitureTrigger(257, "furnitureBlockTrigger", "Shield block bonus", [0x0022A5D4], 1);
        yield return FurnitureTrigger(258, "furnitureMiracleTrigger", "Miracle effect", [0x00227F94], 2);
        yield return FurnitureTrigger(259, "furnitureAdeptTrigger", "Adept chance bonus", [0x00226BFC], 3);
        yield return FurnitureTrigger(260, "furnitureCritAvoidTrigger", "Critical avoid bonus", [0x002273A4], 4);
        yield return FurnitureTrigger(261, "furnitureCommandRangeTrigger", "Commander and Leader range bonus", [0x001B8EBC, 0x001B909C], 5);
        yield return FurnitureTrigger(262, "furnitureThriftTrigger", "Chance to save a weapon use", [0x0016D2E4], 6);
        yield return FurnitureTrigger(263, "furnitureAvoidTrigger", "Avoid bonus", [0x0022733C], 7);
        yield return MaimProficiency();
        yield return InitialHorse();
        yield return FurnitureTrigger(266, "furnitureForestHitTrigger", "Forest Hit bonus", [0x002272BC], 8);
    }
    private static GameRule ShieldTerm(int id, string key, string name, uint address) =>
        new(id, key, name, "", RuleKind.Choice, 0, 1, 0, ["Include (normal)", "Ignore"],
            [new RuleSite(address, n => n == 0 ? 0x02028021u : 0u,
                w => w switch { 0x02028021u => 0, 0u => 1, _ => null })], confirmed: false)
        { Group = ShieldChanceGroup, Short = name };

    // The last accuracy contribution is followed by a copy of the accumulator into v0.
    // Scaling v0 and adding it directly to the return value keeps the same two-word footprint.
    // Preserve existing choice 0 (normal) and 1 (ignore) and their original instruction encodings.
    private static GameRule ShieldAccuracyTerm()
    {
        uint[][] words = [
            [0x02028021u, 0x0200102Du],
            [0u, 0x0200102Du],
            [0x00021043u, 0x02021021u], // arithmetic shift right by 1, then return s0 + v0
            [0x00021083u, 0x02021021u], // arithmetic shift right by 2
            [0x00021040u, 0x02021021u], // shift left by 1
        ];
        return new(253, "shieldChanceAccuracy", "Shield accuracy contribution",
            "Scales the shield accuracy term before it is added to the other block bonuses. Half and quarter round down; the other terms and eligibility are unchanged.",
            RuleKind.Choice, 0, words.Length - 1, 0, ["Full (normal)", "Ignore", "Half", "Quarter", "Double"],
            Enumerable.Range(0, 2).Select(i => new RuleSite(0x00177E98u + (uint)i * 4, n => words[n][i], _ => null)).ToArray(), false,
            decode: w => Array.FindIndex(words, row => row.SequenceEqual(w)) is var n && n >= 0 ? n : null)
        { Group = ShieldChanceGroup, Short = "Shield accuracy contribution" };
    }

    // Level is masked to six bits before the original signed multiply-high division.
    // In the normal /10 and /5 modes keep the original magic multiply and sign correction.
    // For powers of two use the still-live masked level register, not the multiply result.
    private static GameRule MugLevelTerm(int id, string group, string key, uint[] addresses, uint levelRegister)
    {
        uint shift(int n) => levelRegister << 16 | 2u << 11 | (uint)n << 6 | 2u;
        uint[][] words = [
            [0x00001010u, 0x00021083u, 0x00431021u],
            [0x00001010u, 0x00021043u, 0x00431021u],
            [shift(0), 0u, 0u], [shift(1), 0u, 0u], [shift(2), 0u, 0u], [shift(3), 0u, 0u],
            [0x0000102Du, 0u, 0u],
        ];
        return new(id, key, $"{group} level contribution", "", RuleKind.Choice, 0, words.Length - 1, 0,
            ["Level / 10 (normal)", "Level / 5", "Full level", "Level / 2", "Level / 4", "Level / 8", "Ignore level"],
            addresses.Select((address, i) => new RuleSite(address, n => words[n][i], _ => null)).ToList(), false,
            decode: w => Array.FindIndex(words, row => row.SequenceEqual(w)) is var n && n >= 0 ? n : null)
        { Group = group, Short = "Level contribution" , Section = SkillsSection };
    }
    private static GameRule FurnitureTrigger(int id, string key, string name, uint[] addresses, int normal) =>
        new(id, key, name, "", RuleKind.Choice, 0, FurnitureTriggerWords.Length - 1, normal,
            FurnitureTriggerPieces.Select(piece => FurniturePrices.Names[piece]).ToArray(),
            addresses.Select(address => new RuleSite(address, n => FurnitureTriggerWords[n],
                w => Array.IndexOf(FurnitureTriggerWords, w) is var n && n >= 0 ? n : null)).ToArray(), false)
        { Group = FurnitureAssignmentGroup, Short = name };

    // v0 contains weapon proficiency, v1 contains proficiency + twice the existing base chance.
    // The four original instructions produce the signed halving result in s0. Other choices
    // recover the base from v1-v0 and add it to the selected nonnegative proficiency term.
    private static GameRule MaimProficiency()
    {
        uint[][] words = [
            [0x04610003u, 0x00038043u, 0x24630001u, 0x00038043u],
            [0x00621823u, 0x00031843u, 0x00028003u, 0x02038021u],
            [0x00621823u, 0x00031843u, 0x00028083u, 0x02038021u],
            [0x00621823u, 0x00031843u, 0x000280C3u, 0x02038021u],
            [0x00621823u, 0x00031843u, 0x00028040u, 0x02038021u],
            [0x00621823u, 0x00031843u, 0x0000802Du, 0x02038021u],
        ];
        return new(264, "maimProficiencyTerm", "Maim weapon proficiency contribution", "Changes only the weapon proficiency term. The existing base chance is still added. Experimental for nonnegative weapon proficiency.",
            RuleKind.Choice, 0, words.Length - 1, 0,
            ["Half (normal)", "Full", "Quarter", "Eighth", "Double", "Ignore"],
            Enumerable.Range(0, 4).Select(i => new RuleSite(0x00226198u + (uint)i * 4, n => words[n][i], _ => null)).ToArray(), false,
            decode: w => Array.FindIndex(words, row => row.SequenceEqual(w)) is var n && n >= 0 ? n : null)
        { Group = "Maim", Short = "Weapon proficiency contribution", Section = SkillsSection };
    }

    // The initializer creates the horse instance, sets HP using that instance, then checks the
    // same item ID for effect 65. Change both ID loads together, not the HP or riding checks.
    private static GameRule InitialHorse() => new(265, "initialHorse", "Default horse when assigned",
        "Changes the horse given by the default horse initializer. Existing mounting conditions and horse HP calculation are unchanged. This is not an individual character horse assignment.",
        RuleKind.Choice, 0, 15, 0,
        Enumerable.Range(0x205, 16).Select(id => $"{GameNames.Item(id) ?? $"Horse {id:X}"} (0x{id:X})").ToArray(),
        new uint[] { 0x00172C74, 0x00172CA8 }.Select(address => new RuleSite(address,
            n => 0x24050205u + (uint)n, w => w is >= 0x24050205u and <= 0x24050214u ? (int)(w - 0x24050205u) : null)).ToArray(), false);

}
