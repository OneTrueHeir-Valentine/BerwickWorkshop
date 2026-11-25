using Berwick.Core;

public static class ManualParityPilotTests
{
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
    private static void Throws(Action action)
    {
        try { action(); } catch (EditException) { return; }
        throw new Exception("Expected guarded manual parity edit to fail closed.");
    }
    private static TableField Find(IEnumerable<TableField> fields, string key) =>
        fields.Single(f => f.Key == key);
    public static void Run(Action<string, Action> test)
    {
        test("Manual parity class growth encodings preserve unrelated packed fields", () =>
        {
            var fields = new[] { "hpGrowth", "strengthGrowth", "agilityGrowth", "defenseGrowth", "mindGrowth" }
                .Select(k => Find(ClassFields.Fields, k)).ToArray();
            var packed = new byte[ClassDecoder.RecordLength];
            for (int i = 0; i < packed.Length; i++) packed[i] = 0xA5;
            foreach (var (f, n) in fields.Select((f, i) => (f, i)))
            {
                byte[] before = packed.ToArray();
                int value = (n + 1) * 13;
                FieldCodec.Write(packed, f, value);
                Assert(FieldCodec.Read(packed, f) == value, "Class growth field failed round trip.");
                int startBit = f.Offset * 8 + f.Bit;
                for (int bit = 0; bit < before.Length * 8; bit++)
                    if (bit < startBit || bit >= startBit + f.Width)
                        Assert(((before[bit / 8] ^ packed[bit / 8]) & (1 << (bit % 8))) == 0, "Class growth write changed unrelated bits.");
            }
            Assert(!ClassFields.Fields.Any(f => f.Key is "mount" or "unitType"), "Unverified mount controls were enabled.");
        });
        test("Manual parity learned skill schedule preserves six independent ID and level bytes", () =>
        {
            var data = Enumerable.Repeat((byte)0xC7, 32).ToArray();
            for (int i = 1; i <= 6; i++)
            {
                var s = Find(CharacterFields.Growth, $"learned{i}Skill");
                var l = Find(CharacterFields.Growth, $"learned{i}Level");
                Assert(s.Offset == 25 + i && l.Offset == 19 + i, "Learned field location mismatch.");
                FieldCodec.Write(data, s, i - 1);
                FieldCodec.Write(data, l, i * 5);
            }
            for (int i = 1; i <= 6; i++)
            {
                Assert(FieldCodec.Read(data, Find(CharacterFields.Growth, $"learned{i}Skill")) == i - 1, "Wrong learned ID.");
                Assert(FieldCodec.Read(data, Find(CharacterFields.Growth, $"learned{i}Level")) == i * 5, "Wrong learned level.");
            }
            Assert(data[19] == 0xC7, "Learned edit damaged previous growth byte.");
        });
        test("Manual parity equipped slots and inventory flags stay in documented bit positions", () =>
        {
            var record = new byte[CharacterDecoder.RecordLength];
            var main = Find(CharacterFields.Record, "mainhandSlot");
            var off = Find(CharacterFields.Record, "offhandSlot");
            Assert(main.Offset == 27 && main.Bit == 0 && main.Width == 4, "Mainhand selector mapping mismatch.");
            Assert(off.Offset == 26 && off.Bit == 4 && off.Width == 4, "Offhand selector mapping mismatch.");
            FieldCodec.Write(record, main, 2);
            FieldCodec.Write(record, off, 3);
            Assert(record[27] == 2 && record[26] == 0x30, "Equipped selectors changed unwanted bits.");
            for (int i = 1; i <= CharacterFields.ItemSlots; i++)
            {
                var locked = Find(CharacterFields.Record, $"item{i}Lock");
                var drop = Find(CharacterFields.Record, $"item{i}Drop");
                Assert(locked.Offset == 0xBC + (i - 1) * 8 + 4 && locked.Bit == 2, "Lock bit location differs.");
                Assert(drop.Offset == 0xBC + (i - 1) * 8 + 3 && drop.Bit == 7, "Drop bit location differs.");
                FieldCodec.Write(record, locked, 1);
                FieldCodec.Write(record, drop, 1);
                Assert(FieldCodec.Read(record, locked) == 1 && FieldCodec.Read(record, drop) == 1, "Item flag roundtrip failed.");
            }
        });
        test("Manual parity equipment and learned guards reject incompatible edits", () =>
        {
            var source = CharacterFields.Record.ToDictionary(f => f.Key, _ => 0);
            source["mainhandSlot"] = 2;
            Throws(() => ManualParityGuards.ValidateEquipment(source, new HashSet<string>{"mainhandSlot"}));
            source["item2"] = 101;
            ManualParityGuards.ValidateEquipment(source, new HashSet<string>{"mainhandSlot"});
            source["item1Lock"] = 1;
            Throws(() => ManualParityGuards.ValidateEquipment(source, new HashSet<string>{"item1Lock"}));
            var growth = CharacterFields.Growth.ToDictionary(f => f.Key, _ => 0);
            for (int i = 1; i <= 6; i++) { growth[$"learned{i}Skill"] = 255; growth[$"learned{i}Level"] = 0; }
            growth["learned1Skill"] = 255; growth["learned1Level"] = 4;
            Throws(() => ManualParityGuards.ValidateLearned(growth, new HashSet<string>{"learned1Level"}));
            growth["learned1Level"] = 0;
            growth["learned1Skill"] = CharacterDecoder.LearnedSkillIds.Length;
            Throws(() => ManualParityGuards.ValidateLearned(growth, new HashSet<string>{"learned1Skill"}));
            growth["learned1Skill"] = 5; growth["learned1Level"] = 9;
            ManualParityGuards.ValidateLearned(growth, new HashSet<string>{"learned1Skill", "learned1Level"});
        });
        test("Manual parity changes remain source-observed experimental in evidence", () =>
        {
            Assert(ManualParityGuards.IsExperimentalField(EditSession.Growths, "learned1Skill"), "Learned edit not experimental.");
            Assert(ManualParityGuards.IsExperimentalField(EditSession.Classes, "mindGrowth"), "Class growth not experimental.");
            Assert(ManualParityGuards.IsExperimentalField(EditSession.Characters, "item1Lock"), "Inventory flag not experimental.");
            Assert(!ManualParityGuards.IsExperimentalField(EditSession.Characters, "strengthBonus"), "Original character stats mistakenly experimental.");
        });
    }
}
