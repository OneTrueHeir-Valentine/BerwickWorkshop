using Berwick.Core;
using Berwick.Workshop;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using static UnitPlacementTests;

internal static class UnitPlacementIntegration
{
    public static int Run(string folder)
    {
        try
        {
            if (!Path.GetFileName(folder).StartsWith("berwick-placement-", StringComparison.Ordinal) || Directory.EnumerateFileSystemEntries(folder).Any())
                throw new Exception("Integration fixtures require their own empty temporary directory");
            string isolated = Path.Combine(folder, "isolated");
            string shared = Path.Combine(folder, "shared");
            Directory.CreateDirectory(isolated);
            Directory.CreateDirectory(shared);
            Verify(isolated, false);
            Verify(shared, true);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }

    private static void Verify(string folder, bool sharedMap)
    {
        var image = BuildImage(sharedMap);
        string source = Path.Combine(folder, "synthetic.iso");
        File.WriteAllBytes(source, image);
        string hash = Convert.ToHexString(SHA256.HashData(image)).ToLowerInvariant();
        var profile = new GameProfile("Synthetic", hash, 0, 0);
        var disc = new InspectionResult(source, image.Length, "", "", "", hash, "", profile, [], [], 0)
        {
            Armies = [new(5, "Army", [], 0, [new(100, 5, 0, 1, "First", 0, 180), new(101, 5, 1, 2, "Second", 256, 436)])],
            ArmyUnitBytes = new Dictionary<int, ReadOnlyMemory<byte>> { [100] = new byte[256], [101] = new byte[256] }
        };
        using var stream = File.OpenRead(source);
        using var iso = new IsoImage(stream);
        var archives = new GameArchives(iso);
        var library = new EventLibrary(archives);
        var session = new EditSession(disc);
        var map = BattleMaps.Load(archives, library, 9, "first.ar") ?? throw new Exception("Synthetic map did not load");
        var refs = BattlePlacementReferences.Read(library, session);
        Check(refs.Problems.Count == 0, string.Join("; ", refs.Problems));
        var originalArmy = session.EditedBytes(EditSession.ArmyUnits, 100);
        var candidate = StartingUnitPlacement.Candidates(map, refs, disc.Armies, session.EditedBytes(EditSession.Placements, 0)).Single(c => c.Unit.Key == 100);
        Check(candidate.CanPlace && candidate.Assignment.OpeningEvent == 12, "Existing unused record not eligible");
        VerifyAddAndUndo(session, library, map, sharedMap);
        var loaded = VerifyProjectExport(folder, session, archives, image);
        VerifyCurrentScripts(loaded, library);
        VerifyRemoval(loaded, library, map, Path.Combine(folder, "placement.bwproj"), originalArmy, sharedMap);
        Console.WriteLine("Placement add/remove, active scripts, undo/redo, save/load and synthetic export/reopen passed.");
    }

    private static void VerifyAddAndUndo(EditSession session, EventLibrary library, BattleMap map, bool sharedMap)
    {
        if (sharedMap)
        {
            bool refused = false;
            try
            {
                StartingUnitPlacement.Apply(session, library, map, 100, 2, false, 4, 5, false);
            }
            catch (EditException)
            {
                refused = true;
            }
            Check(refused && session.ChangeCount == 0, "Shared placement did not require acknowledgement");
        }
        StartingUnitPlacement.Apply(session, library, map, 100, 2, false, 4, 5, sharedMap);
        Check(session.ChangeCount == 2, "Placement changed more than two values");
        Check(Placement.Slot(session.EditedBytes(EditSession.Placements, 0), 2, 1) == (4, 5), "Placement was not applied");
        CheckDisplayedUnits(session.Disc, session, library, map, true);
        session.Undo();
        Check(Placement.Slot(session.EditedBytes(EditSession.Placements, 0), 2, 1) is null && !session.CanUndo, "Placement is not one undo step");
        CheckDisplayedUnits(session.Disc, session, library, map, false);
        session.Redo();
    }

    private static EditSession VerifyProjectExport(string folder, EditSession session, GameArchives archives, byte[] image)
    {
        var disc = session.Disc;
        string project = Path.Combine(folder, "placement.bwproj");
        session.Save(project);
        var loaded = EditSession.Load(disc, project);
        Check(Placement.Slot(loaded.EditedBytes(EditSession.Placements, 0), 2, 1) == (4, 5), "Project reload lost placement");
        string exported = Path.Combine(folder, "export.iso");
        Exporter.Export(disc.Path, disc.Sha256, exported, Exporter.PatchesFor(loaded, disc));
        using var exportedIso = new IsoImage(File.OpenRead(exported));
        var exportedArchives = new GameArchives(exportedIso);
        var exportedTop = exportedArchives.Top(0);
        var mapEntry = Tarc.Parse(exportedTop).Single(entry => entry.Name == "map");
        Check(Placement.Slot(Placement.Read(Tarc.Extract(exportedTop, mapEntry))!, 2, 1) == (4, 5), "Export reopen lost placement");
        Check(exportedArchives.Top(1).SequenceEqual(archives.Top(1)), "Unrelated map archive changed");
        Check(File.ReadAllBytes(disc.Path).SequenceEqual(image), "Synthetic source image changed");
        return loaded;
    }

    private static void VerifyCurrentScripts(EditSession loaded, EventLibrary library)
    {
        var numbers = ScriptValues.Numbers(12, ScriptParser.Parse(library.Bytes(12)), library.CommandNames);
        int groupIndex = numbers.Single(number => number.Value == 2).Index;
        loaded.Apply(EditSession.ScriptValues, ScriptValues.Key(12, groupIndex), new Dictionary<string, int> { [ScriptValues.Field.Key] = 3 });
        Check(BattlePlacementReferences.Read(library, loaded).Assignments.Single(a => a.Battle == 9).Group == 3, "Numeric project edits were ignored");
        loaded.Undo();
        loaded.SetScript(12, ScriptWriter.Write([Launch(9, "first.ar"), Appear(5, 4)]));
        Check(BattlePlacementReferences.Read(library, loaded).Assignments.Single(a => a.Battle == 9).Group == 4, "Whole project script was ignored");
        loaded.Undo();
    }

    private static void VerifyRemoval(EditSession loaded, EventLibrary library, BattleMap map, string project, byte[] originalArmy, bool sharedMap)
    {
        StartingUnitPlacement.Apply(loaded, library, map, 100, 2, true, 0, 0, sharedMap);
        Check(Placement.Slot(loaded.EditedBytes(EditSession.Placements, 0), 2, 1) is null, "Removal did not clear the slot");
        CheckDisplayedUnits(loaded.Disc, loaded, library, map, false);
        Check(loaded.EditedBytes(EditSession.ArmyUnits, 100).SequenceEqual(originalArmy), "Removal changed the underlying army record");
        loaded.Undo();
        Check(Placement.Slot(loaded.EditedBytes(EditSession.Placements, 0), 2, 1) == (4, 5), "Removal undo lost the unit");
        loaded.Redo();
        loaded.Save(project);
        Check(Placement.Slot(EditSession.Load(loaded.Disc, project).EditedBytes(EditSession.Placements, 0), 2, 1) is null, "Removal reload lost the empty slot");
        VerifyOriginalRemoval(loaded, library, map, Path.GetDirectoryName(project)!, sharedMap);
    }

    private static void VerifyOriginalRemoval(EditSession session, EventLibrary library, BattleMap map, string folder, bool sharedMap)
    {
        var originalArmy = session.EditedBytes(EditSession.ArmyUnits, 101);
        StartingUnitPlacement.Apply(session, library, map, 101, 2, true, 0, 0, sharedMap);
        var document = new MapEditorDocument(session.Disc, session, library, new Dictionary<int, List<int>>(), (_, _) => { }, (_, _) => null);
        Check(document.AllUnits(map).Count == 0, "Original occupied placement was retained in the display");
        string exported = Path.Combine(folder, "removed.iso");
        Exporter.Export(session.Disc.Path, session.Disc.Sha256, exported, Exporter.PatchesFor(session, session.Disc));
        using var iso = new IsoImage(File.OpenRead(exported));
        var archive = new GameArchives(iso).Top(0);
        var entry = Tarc.Parse(archive).Single(item => item.Name == "map");
        Check(Placement.Slot(Placement.Read(Tarc.Extract(archive, entry))!, 2, 2) is null, "Export retained the removed original placement");
        Check(session.EditedBytes(EditSession.ArmyUnits, 101).SequenceEqual(originalArmy), "Original removal changed its army record");
        session.Undo();
        Check(Placement.Slot(session.EditedBytes(EditSession.Placements, 0), 2, 2) == (3, 4), "Original removal undo failed");
    }

    private static void CheckDisplayedUnits(InspectionResult disc, EditSession session, EventLibrary library, BattleMap map, bool added)
    {
        var document = new MapEditorDocument(disc, session, library, new Dictionary<int, List<int>>(),
            (_, _) => throw new Exception("Unexpected script write"), (_, _) => throw new Exception("Unexpected script write"));
        var units = document.AllUnits(map);
        Check(units.Any(unit => unit.Key == 100) == added, "Map display retained a removed unit or omitted a new placement");
        Check(units.Count(unit => unit.Key == 101) == 1, "Existing map display unit changed");
    }

    private static byte[] BuildImage(bool sharedMap)
    {
        var map = new byte[0x23000];
        for (int y = 0; y < 12; y++)
            for (int x = 0; x < 12; x++)
                map[0x20144 + (y * BattleMaps.Side + x) * 2] = 1;
        map.AsSpan(0x22144, 0x800).Fill(255);
        Placement.Write(map, Placement.Move(EmptyRecord(), 2, 2, 3, 4)).CopyTo(map, 0);
        var events = new byte[32];
        for (int i = 0; i < 2; i++)
        {
            Write32(events, i * 16, 12);
            Write32(events, i * 16 + 4, (uint)((12 + i) << 16 | (9 + i)));
            Write32(events, i * 16 + 8, 1);
            Write32(events, i * 16 + 12, 13);
        }
        var children = new Dictionary<string, byte[]>
        {
            ["evdata.dat"] = events,
            ["e0000012.dat"] = ScriptWriter.Write([Launch(9, "first.ar"), Appear(5, 2)]),
            ["e0000013.dat"] = ScriptWriter.Write([Launch(10, sharedMap ? "first.ar" : "second.ar"), Appear(5, 2)])
        };
        byte[][] tops = [Archive(new() { ["map"] = map }), Archive(new() { ["map"] = map }), Archive(children)];
        string[] names = ["first.ar", "second.ar", "events.ar"];
        int dataLength = tops.Sum(top => (top.Length + 2047) / 2048 * 2048) + 4096;
        var iso = new byte[22 * 2048 + dataLength];
        int volume = 16 * 2048;
        iso[volume] = 1;
        Encoding.ASCII.GetBytes("CD001").CopyTo(iso, volume + 1);
        iso[volume + 6] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(iso.AsSpan(volume + 128), 2048);
        BinaryPrimitives.WriteUInt16BigEndian(iso.AsSpan(volume + 130), 2048);
        Both32(iso, volume + 80, (uint)(iso.Length / 2048));
        iso[volume + 156] = 34;
        Both32(iso, volume + 158, 20);
        Both32(iso, volume + 166, 2048);
        iso[volume + 181] = 2;
        iso[volume + 188] = 1;
        int dir = 20 * 2048;
        DirectoryEntry("DATA3.DAT;1", 22, dataLength);
        DirectoryEntry("DATA4.DAT;1", 21, 368 + tops.Length * 12);
        int index = 21 * 2048;
        Write32(iso, index, 2);
        Write32(iso, index + 4, (uint)tops.Length);
        Encoding.ASCII.GetBytes("bmap").CopyTo(iso, index + 8);
        Encoding.ASCII.GetBytes("event").CopyTo(iso, index + 28);
        int sector = 0;
        for (int i = 0; i < tops.Length; i++)
        {
            Write32(iso, index + 368 + i * 12, (i == 2 ? 1u << 27 : 0) | (GameArchives.NameHash(names[i]) & 0x07FFFFFF));
            Write32(iso, index + 372 + i * 12, (uint)sector);
            Write32(iso, index + 376 + i * 12, (uint)tops[i].Length);
            tops[i].CopyTo(iso, 22 * 2048 + sector * 2048);
            sector += (tops[i].Length + 2047) / 2048;
        }
        return iso;

        void DirectoryEntry(string name, int sectorNumber, int length)
        {
            var bytes = Encoding.ASCII.GetBytes(name);
            int size = 33 + bytes.Length + 1;
            iso[dir] = (byte)size;
            Both32(iso, dir + 2, (uint)sectorNumber);
            Both32(iso, dir + 10, (uint)length);
            iso[dir + 32] = (byte)bytes.Length;
            bytes.CopyTo(iso, dir + 33);
            dir += size;
        }
    }

    private static byte[] Archive(Dictionary<string, byte[]> files)
    {
        int names = 16 + files.Count * 12;
        int at = names + files.Keys.Sum(name => name.Length + 1);
        var data = new byte[at + files.Values.Sum(bytes => bytes.Length)];
        "TARC"u8.CopyTo(data);
        Write32(data, 4, (uint)files.Count);
        Write32(data, 8, (uint)names);
        int row = 16;
        foreach (var (name, bytes) in files)
        {
            names += Encoding.ASCII.GetBytes(name, data.AsSpan(names)) + 1;
            Write32(data, row, (uint)(at * 2));
            Write32(data, row + 4, (uint)bytes.Length);
            bytes.CopyTo(data, at);
            at += bytes.Length;
            row += 12;
        }
        return data;
    }
    private static void Write32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    private static void Both32(byte[] bytes, int offset, uint value)
    {
        Write32(bytes, offset, value);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), value);
    }
}
