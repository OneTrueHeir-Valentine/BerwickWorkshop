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
}
public static class Profiles
{
    public static IReadOnlyList<GameProfile> All { get; } = Array.AsReadOnly(new[] {
        new GameProfile("Japanese retail", "77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320",0x0CC0C04,0x08E9CED8) { UnitTable=0x0890E9F0,SecondUnitTable=0x08DD7830,GrowthTable=0x0CB4520,SecondGrowthTable=0x08E907F4,ClassTable=0x0CAE5CC,SecondClassTable=0x08E8A8A0,ExecutableSha256="bb6f98ecac7eba7cbcb5d618150df92d06c4a3e1e27b3e08c67c8adcde4be462",Data3Length=1073741824 },
        new GameProfile("English v2.3", "d7355857377114af3c7734f43250eea2a01cef755531aff04df0a4ccff215111",0x0CB7404,0x08EB46D8) { UnitTable=0x0891A9F0,SecondUnitTable=0x08DE3830,GrowthTable=0x0CAAD20,SecondGrowthTable=0x08EA7FF4,ClassTable=0x0CA4DCC,SecondClassTable=0x08EA20A0,ExecutableSha256="c9fa5ea707ac1074ed15f607998b084cee50fe09957ffe5cf066103d82fabb6f",Data3Length=1073741824 }
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
    public IReadOnlyDictionary<int, int> CharacterOffsets { get; init; } = new Dictionary<int, int>();
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
            var byExecutable=Profiles.All.FirstOrDefault(p=>p.ExecutableSha256==exeHash);
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
        string serial=Regex.Replace(bootName,"_","-").Replace(".","");
        string version=Regex.Match(config,@"(?im)^\s*VER\s*=\s*([^\r\n]+)").Groups[1].Value.Trim();
        return new(System.IO.Path.GetFullPath(path),stream.Length,serial,version,$"{crc:X8}",hash,exeHash,profile,iso.Entries,items.AsReadOnly(),tableDifferences) { Characters=characters.AsReadOnly(),CharacterCopiesMatch=characterCopiesMatch,Classes=classes.AsReadOnly(),ClassCopiesMatch=classCopiesMatch,ItemBytes=itemBytes,UnitBytes=unitBytes,GrowthBytes=growthBytes,ClassBytes=classBytes,CharacterOffsets=characterOffsets,IsModified=modified };
    }
}
