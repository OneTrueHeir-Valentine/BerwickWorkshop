namespace Berwick.Core;

/// <summary>
/// Guards for source-observed manual editor fields. These are not evidence of gameplay acceptance.
/// They do not alter unrelated character, class, story, promotion or mount data.
/// </summary>
public static class ManualParityGuards
{
    public static bool IsExperimentalField(string table, string key) =>
        table == EditSession.Characters && (key is "mainhandSlot" or "offhandSlot" ||
            (key.StartsWith("item", StringComparison.Ordinal) &&
             (key.EndsWith("Lock", StringComparison.Ordinal) || key.EndsWith("Drop", StringComparison.Ordinal))))
        || table == EditSession.Growths && key.StartsWith("learned", StringComparison.Ordinal)
        || table == EditSession.Classes && key.EndsWith("Growth", StringComparison.Ordinal);

    public static void ValidateEquipment(IReadOnlyDictionary<string, int> values, IReadOnlySet<string> changed)
    {
        bool any = changed.Any(k => IsExperimentalField(EditSession.Characters, k)
            || k.StartsWith("item", StringComparison.Ordinal));
        if (!any) return;
        for (int slot = 1; slot <= CharacterFields.ItemSlots; slot++)
        {
            string prefix = $"item{slot}";
            int item = values[prefix];
            bool affects = changed.Contains(prefix) || changed.Contains(prefix + "Lock") || changed.Contains(prefix + "Drop");
            if (affects && item == 0 && (values[prefix + "Lock"] != 0 || values[prefix + "Drop"] != 0))
                throw new EditException($"Slot {slot} is empty; clear its lock and drop flags before applying.");
        }
        foreach (string field in new[] { "mainhandSlot", "offhandSlot" })
        {
            int slot = values[field];
            // If the original record had unusual selector values, unrelated edits remain possible.
            bool touchesSlot = changed.Contains(field) || slot is >= 1 and <= CharacterFields.ItemSlots && changed.Contains($"item{slot}");
            if (touchesSlot && slot > 0 && slot <= CharacterFields.ItemSlots && values[$"item{slot}"] == 0)
                throw new EditException($"{field}: selected starting item slot {slot} is empty.");
        }
    }

    public static void ValidateLearned(IReadOnlyDictionary<string, int> values, IReadOnlySet<string> changed)
    {
        if (!changed.Any(k => k.StartsWith("learned", StringComparison.Ordinal))) return;
        for (int slot = 1; slot <= 6; slot++)
        {
            string skill = $"learned{slot}Skill", level = $"learned{slot}Level";
            int id = values[skill], atLevel = values[level];
            bool touched = changed.Contains(skill) || changed.Contains(level);
            if (!touched) continue;
            if (id != 255 && id >= CharacterDecoder.LearnedSkillIds.Length)
                throw new EditException($"Learned skill slot {slot}: unknown skill ID {id} is read-only until mapped.");
            if (id == 255 && atLevel != 0)
                throw new EditException($"Learned skill slot {slot} is empty; set its learning level to 0 too.");
            if (id != 255 && atLevel > 63)
                throw new EditException($"Learned skill slot {slot}: acquisition levels over 63 need native verification.");
        }
        // Reject a newly introduced duplicate in either changed or unchanged other slots.
        // Existing duplicate rows not touched by an edit are preserved for research.
        for (int slot = 1; slot <= 6; slot++)
        {
            if (!changed.Contains($"learned{slot}Skill")) continue;
            int id = values[$"learned{slot}Skill"];
            if (id == 255) continue;
            if (Enumerable.Range(1, 6).Any(other => other != slot && values[$"learned{other}Skill"] == id))
                throw new EditException($"Learned skill slot {slot}: this skill also appears in another learned slot.");
        }
    }
}
