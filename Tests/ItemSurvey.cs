using Berwick.Core;

/// <summary>Read-only listing of item records on an original disc, for checking item groups. Run with --item-survey &lt;iso&gt;.</summary>
public static class ItemSurvey
{
    public static int Run(string iso)
    {
        var disc = Inspector.Inspect(iso);
        if (!disc.IsVerifiedOriginal) { Console.WriteLine($"{iso} is not a verified original disc."); return 2; }
        var session = new EditSession(disc);
        var fields = ItemEncoder.Fields;
        Console.WriteLine($"{disc.Edition}: {disc.Items.Count} items");
        Console.WriteLine("id\tname\ttype\tunused\tmight\tmin\tmax\tuses\tdurability\tprice\tlevel\teffectKind\teffectAmount\tflags\tshops\teffects");
        foreach (var i in disc.Items)
        {
            int V(string key) => session.Value(EditSession.Items, i.Id, key);
            var flags = fields.Where(f => f.Group == "Effects" && V(f.Key) == 1).Select(f => f.Label);
            Console.WriteLine(string.Join('\t', i.Id, i.Name, ItemDecoder.Types[V("type")], i.IsUnused, V("might"), V("minRange"), V("maxRange"), V("uses"),
                "SABCDEF"[V("durability")], V("price"), V("level"), FieldCodec.Format(ItemEncoder.Field("effectKind"), V("effectKind")), V("effectAmount"),
                string.Join(";", flags), "", i.Effects));
        }
        foreach (string group in BulkGroups.All)
        {
            var members = disc.Items.Where(i => BulkGroups.Contains(session, group, i.Id)).ToList();
            var usable = members.Where(i => !i.IsUnused && !BulkGroups.IsBrokenPlaceholder(session, i.Id)).ToList();
            Console.WriteLine($"group {group}: {usable.Count} usable [{string.Join(", ", usable.Select(i => $"{i.Id} {i.Name}"))}]; left out " +
                $"[{string.Join(", ", members.Except(usable).Select(i => $"{i.Id} {i.Name}"))}]");
        }
        Console.WriteLine($"broken placeholders: {string.Join(", ", disc.Items.Where(i => BulkGroups.IsBrokenPlaceholder(session, i.Id)).Select(i => $"{i.Id} {i.Name}"))}");
        return 0;
    }
}
