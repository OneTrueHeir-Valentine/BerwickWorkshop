namespace Berwick.Core;

public sealed record PromotionClassRow(string Field, string Source, string Destination, string Difference);

/// <summary>Compares stored class records only; does not simulate a promotion event.</summary>
public static class PromotionClassComparison
{
    // Class permission is distinct from level caps and unit-specific restrictions.
    // The three skill grants use the project's selected weapon types where available.
    public static bool? ClassAllows(ClassRecord cls, int type, IReadOnlyDictionary<string, int?> grants)
    {
        if (type is >= 18 and <= 20) return (cls.ShieldSizes & (1 << (type - 18))) != 0;
        if (type is < 0 or > 17 || cls.BaseWeaponTypes is not int mask) return null;
        if ((mask & (1 << type)) != 0) return true;
        bool unknown = false;
        foreach (string skill in new[] { "Pitchfork", "Lance", "Blade" })
        {
            if (!cls.Skills.Contains(skill)) continue;
            if (!grants.TryGetValue(skill, out int? granted) || granted is null) unknown = true;
            else if (granted == type) return true;
        }
        return unknown ? null : false;
    }

    public static string PermissionText(bool? allowed) => allowed switch
    {
        true => "Allowed by class", false => "Not allowed by class", null => "Unknown"
    };

    public static IReadOnlyList<PromotionClassRow> Compare(ClassRecord source, ClassRecord destination,
        IReadOnlyDictionary<string, int?>? grants = null)
    {
        var rows = new List<PromotionClassRow>();
        void Number(string label, int before, int after) => rows.Add(new(label, before.ToString(), after.ToString(), CharacterRecord.Signed(after - before)));
        void Text(string label, string before, string after) => rows.Add(new(label, before, after, before == after ? "Unchanged" : "Changed"));
        Number("Base HP", source.Hp, destination.Hp);
        Number("Base Strength", source.Strength, destination.Strength);
        Number("Base Defense", source.Defense, destination.Defense);
        Number("Base Agility", source.Agility, destination.Agility);
        Number("Base Mind", source.Mind, destination.Mind);
        Number("Movement", source.Move, destination.Move);
        Text("Movement type", source.MovementTypeText, destination.MovementTypeText);
        Text("Mount", source.MountText, destination.MountText);
        Text("Shield sizes", source.ShieldSizeText, destination.ShieldSizeText);
        if (grants is not null)
        {
            for (int type = 0; type < 18; type++)
                Text(ItemDecoder.Types[type] + " permission", PermissionText(ClassAllows(source, type, grants)),
                    PermissionText(ClassAllows(destination, type, grants)));
        }
        foreach (var weapon in source.WeaponCaps.Select(x => x.Weapon).Union(destination.WeaponCaps.Select(x => x.Weapon)))
            Number(weapon + " cap", source.WeaponCaps.FirstOrDefault(x => x.Weapon == weapon)?.Value ?? 0,
                destination.WeaponCaps.FirstOrDefault(x => x.Weapon == weapon)?.Value ?? 0);
        foreach (var skill in source.Skills.Union(destination.Skills))
        {
            bool before = source.Skills.Contains(skill), after = destination.Skills.Contains(skill);
            rows.Add(new("Class skill: " + skill, before ? "Yes" : "No", after ? "Yes" : "No",
                before == after ? "Unchanged" : after ? "Added" : "Removed"));
        }
        return rows.AsReadOnly();
    }
}
