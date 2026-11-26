namespace Berwick.Core;

public static class DependencyChecks
{
    private sealed record Unit(string Table, int Id, int Class, string Name, IReadOnlyList<int> Items);

    public static IReadOnlyList<ReviewIssue> Analyze(EditSession session)
    {
        var issues = new List<ReviewIssue>();
        var changed = session.EditedRecords.Where(r => r.Table is not (EditSession.Scripts or EditSession.Texts))
            .Where(r => !session.Table(r.Table).Records[r.Id].Span.SequenceEqual(session.EditedBytes(r.Table, r.Id))).ToHashSet();
        var units = new List<Unit>();
        foreach (string table in new[] { EditSession.Characters, EditSession.ArmyUnits })
        foreach (int id in session.Table(table).Records.Keys)
        {
            var fields = session.Table(table).Fields;
            units.Add(new(table, id, session.Value(table, id, "class"), session.RecordName(table, id) ?? $"{table} {id}",
                fields.Where(f => f.ItemReference).Select(f => session.Value(table, id, f.Key)).Where(i => i != 0).ToList()));
        }
        void Review(string code, string message, string table, int id) => issues.Add(new("Review", code, message, table, id));
        foreach (var (table, id) in changed.OrderBy(r => r.Table).ThenBy(r => r.Id))
        {
            var fields = table == EditSession.Rules ? new[] { GameRules.ById(id).Field } : session.Table(table).Fields;
            // Original army files contain several unnamed class and item references. Keep those legal.
            foreach (var field in fields.Where(f => f.ItemReference || f.ClassReference || f.Key == "class"))
            {
                int value = session.Value(table, id, field.Key);
                if (value == session.OriginalValue(table, id, field.Key)) continue;
                bool item = field.ItemReference;
                bool known = item ? value == 0 || session.Disc.ItemBytes.ContainsKey(value) : session.Disc.ClassBytes.ContainsKey(value);
                if (!known && table == EditSession.ArmyUnits)
                    known = units.Any(u => item ? session.Table(u.Table).Fields.Where(f => f.ItemReference)
                        .Any(f => session.OriginalValue(u.Table, u.Id, f.Key) == value) : session.OriginalValue(u.Table, u.Id, "class") == value);
                if (!known) issues.Add(new("Error", "missing-reference", $"{session.RecordName(table, id)}: {field.Label} refers to missing {(item ? "item" : "class")} {value}.", table, id));
            }
            // A unit given a mounted class keeps the horse HP it had; on foot that is 0, and a horse with 0 HP is lost on the first hit.
            if (table is EditSession.Characters or EditSession.ArmyUnits && session.Value(table, id, "class") != session.OriginalValue(table, id, "class") &&
                session.Disc.Classes.FirstOrDefault(c => c.Id == session.Value(table, id, "class")) is { Mount: 1 } && session.Value(table, id, "horseMaxHp") == 0)
                Review("mounted-without-horse-hp", $"{session.RecordName(table, id)} now has a mounted class, but its horse has no HP, so the horse is lost on the first hit. " +
                    "Set Horse HP and Horse most HP (the game's horses have 30 to 150).", table, id);
            if (table == EditSession.Classes)
                Shared("shared-class", units.Where(u => u.Class == id), "class", table, id);
            if (table == EditSession.Items)
                Shared("shared-item", units.Where(u => u.Items.Contains(id)), "item", table, id);
            if (table is EditSession.Rules or EditSession.Terrain or EditSession.TerrainKinds)
                Review("global-effect", $"{session.RecordName(table, id)} changes shared game behavior and can affect allied and enemy units.", table, id);
        }
        void Shared(string code, IEnumerable<Unit> users, string kind, string table, int id)
        {
            var list = users.ToList();
            int characters = list.Count(u => u.Table == EditSession.Characters), armies = list.Count - characters;
            var stored = new List<string>();
            if (table == EditSession.Items)
            {
                foreach (int bag in session.Table(EditSession.Bags).Records.Keys)
                    if (Enumerable.Range(0, session.Value(EditSession.Bags, bag, "bagCount")).Any(slot => session.Value(EditSession.Bags, bag, BagData.ItemKey(slot)) == id))
                        stored.Add($"Starting bag: {session.RecordName(EditSession.Bags, bag)}");
                foreach (int recipe in session.Table(EditSession.Recipes).Records.Keys)
                    if (recipe == id || RecipeData.Fields.Any(f => session.Value(EditSession.Recipes, recipe, f.Key) == id))
                        stored.Add($"Atelier recipe: {session.RecordName(EditSession.Recipes, recipe)} ({(recipe == id ? "output" : "ingredient")})");
            }
            Review(code, $"{session.RecordName(table, id)} is a shared {kind}: {characters} starting character records and {armies} army records use it. " +
                (list.Count > 0 ? "Users: " + string.Join("; ", list.Select(u => u.Name)) + ". " : "") +
                (stored.Count > 0 ? string.Join("; ", stored) + ". " : "") +
                "Scripted grants, later equipment and saved units may add other users.", table, id);
        }

        var items = session.EditedItems().ToDictionary(i => i.Id);
        foreach (var unit in units)
        {
            bool unitChanged = changed.Contains((unit.Table, unit.Id)), classChanged = changed.Contains((EditSession.Classes, unit.Class));
            if (!unitChanged && !classChanged && !unit.Items.Any(i => changed.Contains((EditSession.Items, i)))) continue;
            bool HasSkill(string name)
            {
                int bit = Array.IndexOf(CharacterDecoder.SkillBits, name);
                return bit >= 0 && (session.Value(unit.Table, unit.Id, $"skill{bit}") != 0 ||
                    session.Disc.ClassBytes.ContainsKey(unit.Class) && session.Value(EditSession.Classes, unit.Class, $"skill{bit}") != 0);
            }
            if ((unitChanged || classChanged) && HasSkill("Vulnerable") && (HasSkill("Robust") || HasSkill("Robust+")))
                Review("skill-interaction", $"{unit.Name} combines Vulnerable with Robust or Robust+ through personal and class skills. Review the injury and crippling behavior before playing.", unit.Table, unit.Id);
            if (!session.Disc.ClassBytes.ContainsKey(unit.Class)) continue;
            var cls = session.EditedClass(unit.Class);
            foreach (int itemId in unit.Items.Distinct())
            {
                if (!items.TryGetValue(itemId, out var item)) continue;
                string? reason = null;
                if (item.Type is >= 18 and <= 20 && (cls.ShieldSizes & (1 << (item.Type - 18))) == 0)
                    reason = "its class does not allow this shield size";
                // Only exact category matches are checked here; special weapon types need their own permission rules.
                var cap = ClassFields.Fields.FirstOrDefault(f => f.Group == "Weapon level caps" && f.Label.Equals(item.Category, StringComparison.OrdinalIgnoreCase));
                if (cap is not null && session.Value(EditSession.Classes, unit.Class, cap.Key) == 0) reason = "its class has a zero weapon cap for this category";
                if (reason is not null)
                    Review("equipment-permission", $"{unit.Name} carries {item.DisplayName}, but {reason}. Skills and equipment rules can override this; check the intended setup.", unit.Table, unit.Id);
            }
        }

        var sites = new Dictionary<long, (uint Word, int Rule)>();
        foreach (int id in changed.Where(r => r.Table == EditSession.Rules).Select(r => r.Id).Order())
        {
            var rule = GameRules.ById(id);
            if (rule.Decode(session.Table(EditSession.Rules).Records[id].Span) is null)
                issues.Add(new("Error", "rule-original", $"The original instructions for {rule.Name} are not recognized.", EditSession.Rules, id));
            var words = rule.Encode(session.Value(EditSession.Rules, id, rule.Key));
            for (int i = 0; i < rule.Sites.Count; i++)
            {
                long offset = rule.Sites[i].FileOffset;
                if (sites.TryGetValue(offset, out var other) && other.Word != words[i])
                    issues.Add(new("Error", "rule-conflict", $"{rule.Name} and {GameRules.ById(other.Rule).Name} require different instructions at program offset 0x{offset:X}.", EditSession.Rules, id));
                else sites[offset] = (words[i], id);
            }
        }
        foreach (var (tableName, priceTable) in new[] { (EditSession.StablePrices, StablePrices.Table), (EditSession.FurniturePrices, FurniturePrices.Table) })
        {
            var prices = changed.Where(r => r.Table == tableName).Select(r => r.Id).ToHashSet();
            if (session.HasScript(priceTable.EventNumber))
                issues.Add(new("Error", "shop-script-replacement", $"Event {priceTable.EventNumber} is managed by the shop price editor. Remove its whole-script replacement before exporting.", EditSession.Scripts, priceTable.EventNumber));
            var numbers = changed.Where(r => r.Table == EditSession.ScriptValues && ScriptValues.Split(r.Id).Event == priceTable.EventNumber).ToList();
            if (prices.Count == 0 || numbers.Count == 0) continue;
            var offsets = NumberOffsets(ScriptParser.Parse(session.OriginalEventScript(priceTable.EventNumber)!)).ToList();
            foreach (var number in numbers)
            {
                int offset = offsets[ScriptValues.Split(number.Id).Index] + 8;
                foreach (var slot in priceTable.Slots.Where(s => s.Offset == offset && prices.Contains(s.Item)))
                    if (session.Value(tableName, slot.Item, priceTable.Field.Key) != session.Value(EditSession.ScriptValues, number.Id, ScriptValues.Field.Key))
                        issues.Add(new("Error", "script-price-conflict", $"Event {priceTable.EventNumber} and the {priceTable.Field.Label} editor set different prices for entry {slot.Item}. Keep the change in one place, or set both to the same value.", tableName, slot.Item));
            }
        }
        if (changed.Any(r => r.Table == EditSession.FurniturePrices) && session.EditedRecords.Any(r => r.Table == EditSession.Texts))
            issues.Add(new("Review", "shop-text", "Furniture price changes also rewrite the shop's price text. Review any shop text edits alongside these prices."));
        return issues.AsReadOnly();
    }

    private static IEnumerable<int> NumberOffsets(IEnumerable<ScriptNode> nodes)
    {
        foreach (var node in nodes)
        {
            if (node.Command == ScriptParser.Number) yield return node.Offset;
            foreach (int offset in NumberOffsets(node.Args.SelectMany(a => a))) yield return offset;
        }
    }
}
