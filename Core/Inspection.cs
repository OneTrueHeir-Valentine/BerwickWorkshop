using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Berwick.Core;

public sealed record GameProfile(string Name, string Sha256, long ItemTable, long SecondItemTable)
{
    public long UnitTable { get; init; }
    public long SecondUnitTable { get; init; }
    public long GrowthTable { get; init; }
    public long SecondGrowthTable { get; init; }
    public string ExecutableSha256 { get; init; } = "";
    public long Data3Length { get; init; }
    public long ClassTable { get; init; }
    public long SecondClassTable { get; init; }
    /// <summary>Start of the terrain cost table header (terrain count, type count), followed by one row per movement type.</summary>
    public long TerrainTable { get; init; }
    public long SecondTerrainTable { get; init; }
    /// <summary>Partner table (ufavor.dat in basedata.ar): 17 rows of 34 bytes.</summary>
    public long PartnerTable { get; init; }
    public long SecondPartnerTable { get; init; }
}
public static class Profiles
{
    public static IReadOnlyList<GameProfile> All { get; } = Array.AsReadOnly(new[] {
        new GameProfile("Japanese retail", "77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320",0x0CC0C04,0x08E9CED8) { UnitTable=0x0890E9F0,SecondUnitTable=0x08DD7830,GrowthTable=0x0CB4520,SecondGrowthTable=0x08E907F4,ClassTable=0x0CAE5CC,SecondClassTable=0x08E8A8A0,TerrainTable=0x0CD7928,SecondTerrainTable=0x08EB3BFC,PartnerTable=0x0CC09C0,SecondPartnerTable=0x08E9CC94,ExecutableSha256="bb6f98ecac7eba7cbcb5d618150df92d06c4a3e1e27b3e08c67c8adcde4be462",Data3Length=1073741824 },
        new GameProfile("English v2.3", "d7355857377114af3c7734f43250eea2a01cef755531aff04df0a4ccff215111",0x0CB7404,0x08EB46D8) { UnitTable=0x0891A9F0,SecondUnitTable=0x08DE3830,GrowthTable=0x0CAAD20,SecondGrowthTable=0x08EA7FF4,ClassTable=0x0CA4DCC,SecondClassTable=0x08EA20A0,TerrainTable=0x0CCE128,SecondTerrainTable=0x08ECB3FC,PartnerTable=0x0CB71C0,SecondPartnerTable=0x08EB4494,ExecutableSha256="c9fa5ea707ac1074ed15f607998b084cee50fe09957ffe5cf066103d82fabb6f",Data3Length=1073741824 }
    });
    public static GameProfile? Match(string hash) => All.FirstOrDefault(p=>string.Equals(p.Sha256,hash,StringComparison.OrdinalIgnoreCase));
}
public sealed record InspectionProgress(double Fraction, string Message);
public sealed record InspectionResult(string Path, long Size, string Serial, string DiscVersion, string Crc, string Sha256, string ExecutableSha256, GameProfile? Profile, IReadOnlyList<IsoEntry> Files, IReadOnlyList<ItemRecord> Items, int DifferentTableRecords)
{
    public bool Supported => Profile is not null;
    public bool IsModified { get; init; }
    public string Edition => Profile is null ? "Unrecognized edition" : IsModified ? $"Modified {Profile.Name}" : Profile.Name;
    public IReadOnlyList<CharacterRecord> Characters { get; init; } = [];
    public bool CharacterCopiesMatch { get; init; } = true;
    public IReadOnlyList<ClassRecord> Classes { get; init; } = [];
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> ItemBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> UnitBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> GrowthBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> ClassBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyList<MovementTypeRecord> MovementTypes { get; init; } = [];
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> TerrainBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public bool TerrainCopiesMatch { get; init; } = true;
    public IReadOnlyList<TerrainKindRecord> TerrainKinds { get; init; } = [];
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> TerrainKindBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public bool TerrainKindCopiesMatch { get; init; } = true;
    public IReadOnlyDictionary<int, int> CharacterOffsets { get; init; } = new Dictionary<int, int>();
    /// <summary>Partner row of each character that has one (34 bytes), and the row's position in the table.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> PartnerBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, int> PartnerRows { get; init; } = new Dictionary<int, int>();
    public bool PartnerCopiesMatch { get; init; } = true;
    /// <summary>Starting bag contents of each character that has one, padded to the longest bag the editor allows.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> BagBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    /// <summary>Mercenary fee row of each hireable character (24 bytes in the game program), and the row's position.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> MercenaryBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, int> MercenaryRows { get; init; } = new Dictionary<int, int>();
    /// <summary>Atelier recipe of each item it makes (24 bytes in the game program), and the row's position.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> RecipeBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, int> RecipeRows { get; init; } = new Dictionary<int, int>();
    /// <summary>Enemy and guest armies, and each of their units in the starting record layout (key: army × 1000 + unit).</summary>
    public IReadOnlyList<Army> Armies { get; init; } = [];
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> ArmyUnitBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    /// <summary>Instruction word at each game rule site, for rules whose current instruction is recognised.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> RuleWords { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IsoEntry? ExecutableFile { get; init; }
    /// <summary>Stable price of each horse (4 bytes, little-endian), when the disc's stable event has the known layout.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> StablePriceBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    /// <summary>Furniture shop price of each piece (4 bytes, little-endian), when the disc's furniture event has the known layout.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> FurniturePriceBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    /// <summary>Battle animation rows of kirikae.db (12 bytes each, keyed by AnimationTable.RowId), set rows and palette rows,
    /// where each row is in DATA3.DAT, and the animation sets on the disc.</summary>
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> AnimationSetBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, ReadOnlyMemory<byte>> AnimationPaletteBytes { get; init; } = new Dictionary<int, ReadOnlyMemory<byte>>();
    public IReadOnlyDictionary<int, long> AnimationRowOffsets { get; init; } = new Dictionary<int, long>();
    public IReadOnlySet<int> AnimationSetsOnDisc { get; init; } = new HashSet<int>();
    public bool ClassCopiesMatch { get; init; } = true;
    public ClassRecord? ClassOf(CharacterRecord character) => Classes.FirstOrDefault(c => c.Id == character.ClassId);
}

public static class Inspector
{
    public static InspectionResult Inspect(string path, IProgress<InspectionProgress>? progress=null, CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();
        using var stream = new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan);
        using var iso = new IsoImage(stream);
        var config=Encoding.ASCII.GetString(iso.ReadFile("SYSTEM.CNF",64*1024));
        var boot=Regex.Match(config,@"(?im)^\s*BOOT2\s*=\s*cdrom0:[\\/]*([^;\r\n]+)");
        if (!boot.Success) throw new InvalidDataException("This image does not contain a PlayStation 2 boot file.");
        string bootName=boot.Groups[1].Value.Trim();
        if (bootName.Contains('/') || bootName.Contains('\\')) throw new InvalidDataException("This boot file layout is not supported.");
        var exe=iso.ReadFile(bootName,32*1024*1024);
        if (exe.Length < 52 || !exe.AsSpan(0,4).SequenceEqual(new byte[]{127,69,76,70}) || exe[4]!=1 || exe[5]!=1 || BinaryPrimitives.ReadUInt16LittleEndian(exe.AsSpan(18))!=8)
            throw new InvalidDataException("The disc does not contain a supported PS2 executable.");
        uint crc=0;for(int i=0;i+4<=exe.Length;i+=4)crc^=BinaryPrimitives.ReadUInt32LittleEndian(exe.AsSpan(i));
        string exeHash=Convert.ToHexString(SHA256.HashData(exe)).ToLowerInvariant();
        string normalExeHash=Convert.ToHexString(SHA256.HashData(GameRules.Normalize(exe))).ToLowerInvariant();
        var exeEntry=iso.Entries.First(e=>e.Name.Equals(bootName.Split(';')[0],StringComparison.OrdinalIgnoreCase));
        stream.Position=0;
        using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer=new byte[4*1024*1024];long done=0;int bytes;
        progress?.Report(new(0,"Checking disc"));
        while((bytes=stream.Read(buffer))>0)
        {
            cancellation.ThrowIfCancellationRequested();sha.AppendData(buffer,0,bytes);done+=bytes;
            progress?.Report(new((double)done/stream.Length,"Checking disc"));
        }
        cancellation.ThrowIfCancellationRequested();
        string hash=Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        var profile=Profiles.Match(hash);bool modified=false;
        if(profile is null)
        {
            // Rule sites are put back to the game's own instructions first, so a disc exported with rules is still recognised.
            var byExecutable=Profiles.All.FirstOrDefault(p=>p.ExecutableSha256==exeHash || p.ExecutableSha256==normalExeHash);
            var data3=iso.Entries.FirstOrDefault(e=>e.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase));
            if(byExecutable is not null && data3 is not null && data3.Length==byExecutable.Data3Length){profile=byExecutable;modified=true;}
        }
        var items=new List<ItemRecord>();int tableDifferences=0;var itemBytes=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null)
        {
            var data=iso.Entries.Single(e=>e.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase));
            var names=Catalogue.Load(); int length=names.Keys.Max()*56;
            if(profile.ItemTable+length>data.Length || profile.SecondItemTable+length>data.Length) throw new InvalidDataException("The item tables are incomplete.");
            var table=iso.ReadRange(data.Offset+profile.ItemTable,length,64*1024);
            var second=iso.ReadRange(data.Offset+profile.SecondItemTable,length,64*1024);
            foreach(var name in names)
            {
                cancellation.ThrowIfCancellationRequested();int start=(name.Key-1)*56;
                var raw=table.AsSpan(start,56).ToArray();itemBytes[name.Key]=raw;
                var item=ItemDecoder.Decode(name.Key,name.Value,raw.ToArray());
                var mirror=ItemDecoder.Decode(name.Key,name.Value,second.AsSpan(start,56).ToArray());
                if(ItemComparison.Compare(new[]{item},new[]{mirror}).Count!=0)tableDifferences++;
                items.Add(item);
            }
        }
        var characters=new List<CharacterRecord>();bool characterCopiesMatch=true;
        var classes=new List<ClassRecord>();bool classCopiesMatch=true;
        var unitBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var growthBytes=new Dictionary<int,ReadOnlyMemory<byte>>();
        var classBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var characterOffsets=new Dictionary<int,int>();
        var terrainBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var movementTypes=new List<MovementTypeRecord>();bool terrainCopiesMatch=true;
        var terrainKindBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var terrainKinds=new List<TerrainKindRecord>();bool terrainKindCopiesMatch=true;
        if (profile is not null)
        {
            var data=iso.Entries.Single(e=>e.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase));
            var roster=CharacterDecoder.LoadRoster();
            int unitLength=roster.Max(c=>c.RecordOffset)+CharacterDecoder.RecordLength;
            int growthLength=roster.Max(c=>c.Id)*CharacterDecoder.GrowthLength;
            foreach(long start in new[]{profile.UnitTable,profile.SecondUnitTable}) if(start+unitLength>data.Length) throw new InvalidDataException("The character tables are incomplete.");
            foreach(long start in new[]{profile.GrowthTable,profile.SecondGrowthTable}) if(start+growthLength>data.Length) throw new InvalidDataException("The character tables are incomplete.");
            var units=iso.ReadRange(data.Offset+profile.UnitTable,unitLength,64*1024);
            var growths=iso.ReadRange(data.Offset+profile.GrowthTable,growthLength,64*1024);
            characterCopiesMatch=units.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondUnitTable,unitLength,64*1024))
                && growths.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondGrowthTable,growthLength,64*1024));
            var classList=ClassDecoder.LoadList();
            int classLength=classList.Max(c=>c.Id)*ClassDecoder.RecordLength;
            foreach(long start in new[]{profile.ClassTable,profile.SecondClassTable}) if(start+classLength>data.Length) throw new InvalidDataException("The class tables are incomplete.");
            var classData=iso.ReadRange(data.Offset+profile.ClassTable,classLength,64*1024);
            classCopiesMatch=classData.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondClassTable,classLength,64*1024));
            foreach(var (id,name) in classList)
            {
                var raw=classData.AsSpan((id-1)*ClassDecoder.RecordLength,ClassDecoder.RecordLength).ToArray();classBytes[id]=raw;
                classes.Add(ClassDecoder.Decode(id,name,raw));
            }
            foreach(long start in new[]{profile.TerrainTable,profile.SecondTerrainTable}) if(start+TerrainData.TableLength>data.Length) throw new InvalidDataException("The terrain tables are incomplete.");
            var terrain=iso.ReadRange(data.Offset+profile.TerrainTable,TerrainData.TableLength,64*1024);
            if(TerrainData.HeaderValid(terrain))
            {
                terrainCopiesMatch=terrain.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondTerrainTable,TerrainData.TableLength,64*1024));
                for(int t=0;t<TerrainData.Types;t++)
                {
                    var row=terrain.AsSpan(TerrainData.HeaderLength+t*TerrainData.RowLength,TerrainData.RowLength).ToArray();
                    terrainBytes[t]=row;movementTypes.Add(TerrainData.Decode(t,row));
                }
                var kinds=iso.ReadRange(data.Offset+profile.TerrainTable+TerrainKindData.OffsetFromCosts,TerrainKindData.TableLength,64*1024);
                if(TerrainKindData.HeaderValid(kinds))
                {
                    terrainKindCopiesMatch=kinds.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondTerrainTable+TerrainKindData.OffsetFromCosts,TerrainKindData.TableLength,64*1024));
                    for(int t=0;t<TerrainData.Terrains;t++)
                    {
                        var row=kinds.AsSpan(TerrainKindData.HeaderLength+t*TerrainKindData.RowLength,TerrainKindData.RowLength).ToArray();
                        terrainKindBytes[t]=row;terrainKinds.Add(TerrainKindData.Decode(t,row));
                    }
                }
            }
            var classNames=classes.ToDictionary(c=>c.Id,c=>c.Name);
            var itemNames=items.ToDictionary(i=>i.Id,i=>i.DisplayName);
            foreach(var entry in roster)
            {
                cancellation.ThrowIfCancellationRequested();
                var unit=units.AsSpan(entry.RecordOffset,CharacterDecoder.RecordLength).ToArray();
                var growth=growths.AsSpan((entry.Id-1)*CharacterDecoder.GrowthLength,CharacterDecoder.GrowthLength).ToArray();
                unitBytes[entry.Id]=unit;growthBytes[entry.Id]=growth;characterOffsets[entry.Id]=entry.RecordOffset;
                var character=CharacterDecoder.Decode(entry,unit,growth,itemNames);
                characters.Add(character with { ClassName=classNames.TryGetValue(character.ClassId,out var cn)?cn:$"Class 0x{character.ClassId:X2}" });
            }
        }
        var partnerBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var partnerRows=new Dictionary<int,int>();bool partnerCopiesMatch=true;
        var bagBytes=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null && characters.Count>0)
        {
            var data=iso.Entries.Single(e=>e.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase));
            var ids=characters.Select(c=>c.Id).ToHashSet();
            if(profile.PartnerTable+PartnerData.TableLength<=data.Length && profile.SecondPartnerTable+PartnerData.TableLength<=data.Length)
            {
                var table=iso.ReadRange(data.Offset+profile.PartnerTable,PartnerData.TableLength,64*1024);
                partnerCopiesMatch=table.AsSpan().SequenceEqual(iso.ReadRange(data.Offset+profile.SecondPartnerTable,PartnerData.TableLength,64*1024));
                foreach(var (id,row,rowBytes) in PartnerData.Read(table,ids)){partnerBytes[id]=rowBytes;partnerRows[id]=row;}
            }
            foreach(var (id,offset) in characterOffsets)
            {
                long at=profile.UnitTable+offset+BagData.OffsetFromRecord;
                if(at+BagData.ReadLength>data.Length)continue;
                if(BagData.Read(iso.ReadRange(data.Offset+at,BagData.ReadLength,64*1024)) is { } bag) bagBytes[id]=bag;
            }
        }
        var mercenaryBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var mercenaryRows=new Dictionary<int,int>();var recipeBytes=new Dictionary<int,ReadOnlyMemory<byte>>();var recipeRows=new Dictionary<int,int>();
        if (profile is not null)
            foreach(var (id,row,rowBytes) in MercenaryData.Read(exe)){mercenaryBytes[id]=rowBytes;mercenaryRows[id]=row;}
            foreach(var (id,row,rowBytes) in RecipeData.Read(exe)){recipeBytes[id]=rowBytes;recipeRows[id]=row;}
        var armies=new List<Army>();var armyUnitBytes=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null)
        {
            try
            {
                var archives=new GameArchives(iso);
                // The starting characters' file has the same format; it is edited on the Characters page, so it is left out here.
                var data=iso.Entries.Single(e=>e.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase));
                var startingTables=new[]{data.Offset+profile.UnitTable,data.Offset+profile.SecondUnitTable};
                foreach(var (army,team) in ArmyData.Load(archives,ArmyData.Names))
                {
                    if(army.Locations.Any(l=>startingTables.Any(t=>t>=l && t<l+army.Length)))continue;
                    armies.Add(army);
                    foreach(var unit in army.Units) armyUnitBytes[unit.Key]=ArmyData.Present(team,unit);
                }
                // Name each army after the battles it appears in, where the events show it (one or two battles).
                var battleNames=LiveGame.Battles(exe,ArmyData.LoadBattleTitles(archives)).GroupBy(b=>b.Battle).ToDictionary(g=>g.Key,g=>g.First().Text);
                var armyBattles=new EventLibrary(archives).ArmyBattles();
                for(int i=0;i<armies.Count;i++)
                    if(armyBattles.TryGetValue(armies[i].Id,out var found) && found.Count is 1 or 2)
                        armies[i]=armies[i] with { Label=$"{armies[i].Label} ({string.Join(" and ",found.Select(b=>battleNames.TryGetValue(b,out var n)?n:$"battle {b}"))})" };
            }
            catch (InvalidDataException) { armies.Clear();armyUnitBytes.Clear(); }
        }
        var ruleWords=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null)
            foreach(var rule in GameRules.All)
                if(rule.Decode(GameRules.WordsAt(exe,rule)) is int setting) ruleWords[rule.Id]=rule.EncodeBytes(setting);
        var stablePrices=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null)
        {
            try
            {
                foreach(var (item,price) in StablePrices.Load(new GameArchives(iso)).Prices)
                {
                    var priceBytes=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(priceBytes,price);stablePrices[item]=priceBytes;
                }
            }
            catch (InvalidDataException) { stablePrices.Clear(); }
        }
        var furniturePrices=new Dictionary<int,ReadOnlyMemory<byte>>();
        if (profile is not null)
        {
            try
            {
                foreach(var (piece,price) in FurniturePrices.Table.Load(new GameArchives(iso)).Prices)
                {
                    var priceBytes=new byte[4];System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(priceBytes,price);furniturePrices[piece]=priceBytes;
                }
            }
            catch (InvalidDataException) { furniturePrices.Clear(); }
        }
        var animationSets=new Dictionary<int,ReadOnlyMemory<byte>>();var animationPalettes=new Dictionary<int,ReadOnlyMemory<byte>>();var animationOffsets=new Dictionary<int,long>();var setsOnDisc=new HashSet<int>();
        if (profile is not null)
        {
            try
            {
                var archives=new GameArchives(iso);
                int top=archives.Find(AnimationTable.Path);
                if(top>=0)
                {
                    long start=archives.Row(top).Sector*(long)GameArchives.Sector;
                    foreach(var (id,offset,rowBytes) in AnimationTable.Records(archives.Top(top)))
                    {
                        (AnimationTable.SplitId(id).Palette?animationPalettes:animationSets)[id]=rowBytes;
                        animationOffsets[id]=start+offset;
                    }
                    setsOnDisc=AnimationSet.OnDisc(archives).Keys.ToHashSet();
                }
            }
            catch (InvalidDataException) { animationSets.Clear();animationPalettes.Clear();animationOffsets.Clear(); }
        }
        string serial=Regex.Replace(bootName,"_","-").Replace(".","");
        string version=Regex.Match(config,@"(?im)^\s*VER\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        return new(System.IO.Path.GetFullPath(path),stream.Length,serial,version,$"{crc:X8}",hash,exeHash,profile,iso.Entries,items.AsReadOnly(),tableDifferences) { Characters=characters.AsReadOnly(),CharacterCopiesMatch=characterCopiesMatch,Classes=classes.AsReadOnly(),ClassCopiesMatch=classCopiesMatch,ItemBytes=itemBytes,UnitBytes=unitBytes,GrowthBytes=growthBytes,ClassBytes=classBytes,MovementTypes=movementTypes.AsReadOnly(),TerrainBytes=terrainBytes,TerrainCopiesMatch=terrainCopiesMatch,TerrainKinds=terrainKinds.AsReadOnly(),TerrainKindBytes=terrainKindBytes,TerrainKindCopiesMatch=terrainKindCopiesMatch,CharacterOffsets=characterOffsets,PartnerBytes=partnerBytes,PartnerRows=partnerRows,PartnerCopiesMatch=partnerCopiesMatch,BagBytes=bagBytes,MercenaryBytes=mercenaryBytes,MercenaryRows=mercenaryRows,RecipeBytes=recipeBytes,RecipeRows=recipeRows,Armies=armies.AsReadOnly(),ArmyUnitBytes=armyUnitBytes,IsModified=modified,RuleWords=ruleWords,ExecutableFile=exeEntry,StablePriceBytes=stablePrices,FurniturePriceBytes=furniturePrices,AnimationSetBytes=animationSets,AnimationPaletteBytes=animationPalettes,AnimationRowOffsets=animationOffsets,AnimationSetsOnDisc=setsOnDisc };
    }
}
