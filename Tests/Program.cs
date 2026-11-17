using Berwick.Core;
using System.Buffers.Binary;
using System.Text;

int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual} (line {line})"); }
void Throws<T>(Action action) where T:Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
Test("Every skill has an explanation in plain text",()=>{
    var names=CharacterDecoder.SkillBits.Where(n=>n.Length>0).ToList();
    Equal(names.Count,SkillCatalog.All.Count);
    Equal(true,names.All(n=>SkillCatalog.Find(n) is not null));
    Equal(SkillCatalog.All.Count,SkillCatalog.All.Select(s=>s.Name).Distinct().Count());
    foreach(var s in SkillCatalog.All)
    {
        string text=s.Summary+s.Details+s.Source+s.Kind;
        Equal(false,text.Contains('—')||text.Contains('–'));
        Equal(true,s.Summary.Length>0 && s.Source.Length>0 && s.Kind.Length>0);
    }
    // Every skill with changeable numbers on the Skills page has an explanation too.
    Equal(true,GameRules.All.Where(r=>r.Section==GameRules.SkillsSection).Select(r=>r.Group).Distinct().All(g=>SkillCatalog.Find(g) is not null));
});
Test("ISO reads named files and ignores version suffix", () => {
    using var iso = new IsoImage(new MemoryStream(Fixture()));
    Equal("abc", Encoding.ASCII.GetString(iso.ReadFile("SYSTEM.CNF", 1024)));
});
Test("ISO rejects non ISO input", () => Throws<InvalidDataException>(() => new IsoImage(new MemoryStream(new byte[40000]))));
Test("ISO rejects truncated image", () => Throws<InvalidDataException>(() => new IsoImage(new MemoryStream(Fixture()[..40000]))));
Test("ISO bounds file extents before reading", () => {
    var data=Fixture(); U32(data,20*2048+2,1000);
    Throws<InvalidDataException>(() => new IsoImage(new MemoryStream(data)));
});
Test("ISO rejects malformed directory records", () => {
    var data=Fixture(); data[20*2048]=5;
    Throws<InvalidDataException>(() => new IsoImage(new MemoryStream(data)));
});
Test("ISO caps requested allocation", () => {
    using var iso = new IsoImage(new MemoryStream(Fixture()));
    Throws<InvalidDataException>(() => iso.ReadFile("SYSTEM.CNF", 2));
});
Test("ISO handles missing files", () => {
    using var iso = new IsoImage(new MemoryStream(Fixture()));
    Throws<InvalidDataException>(() => iso.ReadFile("NOFILE",1024));
});
Test("Bit fields cross bytes correctly", () => {
    Equal(53,ItemDecoder.Bits(new byte[]{0xA0,0x06},0,5,6));
    Equal(0x1234,ItemDecoder.Bits(new byte[]{0x34,0x12},0,0,16));
});
Test("Bit reads reject truncated input",()=>Throws<InvalidDataException>(()=>ItemDecoder.Bits(new byte[]{0},0,7,7)));
Test("Item record decodes packed combat fields", () => {
    var b=new byte[56]; b[0]=0xA0;b[1]=0x81;b[2]=0x23;b[3]=0x09;b[4]=1;b[5]=7;b[8]=0x34;b[9]=0x12;
    var i=ItemDecoder.Decode(34,"Bronze Sword",b);
    Equal(13,i.Might);Equal(71,i.Accuracy);Equal(4,i.Weight);Equal(4660,i.Price);Equal(1,i.MinRange);Equal(1,i.MaxRange);Equal(7,i.Critical);
});
Test("Item records require complete data",()=>Throws<InvalidDataException>(()=>ItemDecoder.Decode(1,"Dagger",new byte[12])));
Test("Catalogue preserves names shared by distinct IDs",()=>{
    var names=Catalogue.Load(); Equal("Pickaxe",names[0xB0]);Equal("Pickaxe",names[0x1FA]);Equal(646,names.Count);
});
Test("Unknown image cannot select a profile",()=>Equal<GameProfile?>(null,Profiles.Match(new string('0',64))));
Test("Japanese and English use separate item tables",()=>{
    Equal(0x0CC0C04L,Profiles.Match("77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320")!.ItemTable);
    Equal(0x0CB7404L,Profiles.Match("d7355857377114af3c7734f43250eea2a01cef755531aff04df0a4ccff215111")!.ItemTable);
});
Test("Comparison joins by ID instead of label or position",()=>{
    var a=new ItemRecord(176,"Pickaxe",5,70,4,200,1,1,0,20,1,2,0,0,"");
    var b=a with{Id=506,Price=90};
    var changes=ItemComparison.Compare(new[]{a,b},new[]{b with{Price=95},a});
    Equal(1,changes.Count);Equal(506,changes[0].Id);Equal("Price",changes[0].Field);Equal("90",changes[0].First);Equal("95",changes[0].Second);
});
Test("Character record decodes signed bonuses, growths and inventory",()=>{
    var r=new byte[0x100];var g=new byte[32];
    r[20]=22;                                   // level 22
    r[22]=8<<4;                                 // HP +8
    r[23]=(byte)((32-2)<<3);                    // Strength -2 in five signed bits
    r[24]=3;                                    // Agility +3
    r[25]=3<<2;                                 // Defense +3
    r[56+6/8]|=1<<(6%8);                        // Shieldfaire bit
    r[0xBC]=0x22;r[0xBE]=(byte)(101<<4&0xFF);r[0xBF]=(byte)(101>>4);
    g[0]=40|(30&1)<<7;g[1]=(byte)(30>>1|(18&3)<<6);g[2]=(byte)(18>>2);
    g[4]=(byte)(30<<2);g[8]=6<<4;g[13]=8<<4;
    g[20]=11;g[26]=6;for(int i=27;i<32;i++)g[i]=0xFF;
    var c=CharacterDecoder.Decode(new(0x53,"Test",0),r,g,new Dictionary<int,string>{[0x22]="Bronze Sword"});
    Equal(22,c.Level);Equal(8,c.HpBonus);Equal(-2,c.StrengthBonus);Equal(3,c.AgilityBonus);Equal(3,c.DefenseBonus);
    Equal(40,c.HpGrowth);Equal(30,c.StrengthGrowth);Equal(18,c.DefenseGrowth);Equal(30,c.AgilityGrowth);
    Equal("Sword:60,Shield:80",string.Join(",",c.WeaponGrowths.Select(w=>$"{w.Weapon}:{w.Value}")));
    Equal("Shieldfaire",c.Skills.Single());Equal("Shieldfaire",c.Learned.Single().Skill);Equal(11,c.Learned.Single().Level);
    Equal("Bronze Sword",c.Items.Single().Name);Equal(101,c.Items.Single().Durability);
});
Test("Character records require complete data",()=>Throws<InvalidDataException>(()=>CharacterDecoder.Decode(new(1,"x",0),new byte[16],new byte[32],new Dictionary<int,string>())));
Test("Learned skill numbering skips unused bits",()=>{
    Equal("Axe Guard",CharacterDecoder.LearnedSkillIds[30]);Equal("Throw",CharacterDecoder.LearnedSkillIds[40]);
    Equal("Expert",CharacterDecoder.LearnedSkillIds[66]);Equal("Cavalry",CharacterDecoder.SkillBits[64]);
});
Test("Weapon levels show tenths only when present",()=>{Equal("21",CharacterRecord.WeaponLevel(210));Equal("0.8",CharacterRecord.WeaponLevel(8));});
Test("Roster has 35 distinct characters",()=>{var r=CharacterDecoder.LoadRoster();Equal(35,r.Count);Equal(35,r.Select(c=>c.Id).Distinct().Count());Equal("Reese",r[0].Name);});
Test("Class record decodes base stats in disc order",()=>{
    var c=new byte[100];
    c[0]=7|(6&7)<<5;c[1]=(byte)(6>>3|7<<2);c[2]=(byte)(0|(30&7)<<5);c[3]=(byte)(30>>3);
    c[4]=25;c[8]=7;c[12+1/8]|=1<<1;c[29]=(byte)((40&7)<<5);c[30]=(byte)(40>>3|(40&31)<<3);c[31]=(byte)(40>>5);
    var r=ClassDecoder.Decode(0x1E,"Veteran Knight (mounted)",c);
    Equal(30,r.Hp);Equal(7,r.Strength);Equal(7,r.Defense);Equal(6,r.Agility);Equal(0,r.Mind);Equal(7,r.Move);Equal(25,r.Experience);
    Equal("Canto",r.Skills.Single());Equal("Sword:40,Spear:40",string.Join(",",r.WeaponCaps.Select(w=>$"{w.Weapon}:{w.Value}")));
});
Test("Class names read naturally",()=>{
    Equal("Veteran Knight (mounted)",ClassDecoder.DisplayName("VeteranKnight_R"));Equal("Thief (female)",ClassDecoder.DisplayName("Thief_F"));
    Equal("Wyvern Knight (Larentia) (mounted)",ClassDecoder.DisplayName("WyvernKnightLarentia_R"));Equal("Chevalier (mounted)",ClassDecoder.DisplayName("Chevalire_R"));
    Equal("Verian Knight 2",ClassDecoder.DisplayName("VerianKnight2"));
});
Test("Class list has one entry per ID",()=>{var l=ClassDecoder.LoadList();Equal(l.Count,l.Select(c=>c.Id).Distinct().Count());Equal("Thief (male)",l.Single(c=>c.Id==0x57).Name);});
Test("Item encoder writes are exact inverses of the decoder",()=>{
    var rng=new Random(7);
    foreach(var f in ItemEncoder.Fields)
        foreach(int v in new[]{f.Min,f.Max,(f.Min+f.Max)/2})
        {
            var before=new byte[56];rng.NextBytes(before);var after=(byte[])before.Clone();
            ItemEncoder.Write(after,f,v);
            Equal(v,ItemEncoder.Read(after,f));
            for(int bit=0;bit<56*8;bit++)
            {
                bool changed=((before[bit/8]^after[bit/8])>>(bit%8)&1)!=0;
                bool inside=bit>=f.Offset*8+f.Bit && bit<f.Offset*8+f.Bit+f.Width;
                if(changed && !inside) throw new Exception($"{f.Key}={v} changed bit {bit} outside its field");
            }
        }
});
Test("Item encoder fields agree with decoded record properties",()=>{
    var b=new byte[56];new Random(3).NextBytes(b);var r=ItemDecoder.Decode(1,"Dagger",b);
    var expected=new Dictionary<string,int>{["type"]=r.Type,["might"]=r.Might,["accuracy"]=r.Accuracy,["weight"]=r.Weight,["minRange"]=r.MinRange,["maxRange"]=r.MaxRange,["critical"]=r.Critical,["level"]=r.Level,["price"]=r.Price,["durability"]=r.Durability,["uses"]=r.Uses};
    foreach(var f in ItemEncoder.Fields.Where(f=>f.Group.Length==0)) Equal($"{f.Key}={expected[f.Key]}",$"{f.Key}={ItemEncoder.Read(b,f)}");
    var rec=new byte[56];
    foreach(var f in ItemEncoder.Fields.Where(f=>f.Group.Length>0))
        foreach(int v in new[]{f.Min,f.Max,0,f.Min/2})
        {
            Array.Clear(rec);ItemEncoder.Write(rec,f,v);Equal($"{f.Key}={v}",$"{f.Key}={ItemEncoder.Read(rec,f)}");
            foreach(var g in ItemEncoder.Fields.Where(g=>g!=f && g.Group.Length>0)) Equal($"{g.Key}=0",$"{g.Key}={ItemEncoder.Read(rec,g)}");
        }
    var kind=ItemEncoder.Field("effectKind");Equal("None/Sword break/Other (125)/Other (50)",$"{FieldCodec.Format(kind,0)}/{FieldCodec.Format(kind,108)}/{FieldCodec.Format(kind,125)}/{FieldCodec.Format(kind,50)}");
    Equal(true,FieldCodec.TryParse(kind,"injury",out int injury,out _));Equal(131,injury);Equal(-1,kind.ChoiceIndex(125));Equal(0,kind.ChoiceIndex(0));Equal(108,kind.ChoiceValue(kind.ChoiceIndex(108)));
    Equal(33,ItemEncoder.Fields.Count(f=>f.Group=="Effects"));Equal(ItemEncoder.Fields.Count,ItemEncoder.Fields.Select(f=>f.Key).Distinct().Count());
});
Test("Archive decompression and script parsing on built samples",()=>{
    // literal run: header (size 5, stored), control byte with five literal bits
    byte[] lz=[5,0,0,0,0,0,0,0,0xF8,(byte)'h',(byte)'e',(byte)'l',(byte)'l',(byte)'o'];
    Equal("hello",System.Text.Encoding.ASCII.GetString(BerwickLz.Decompress(lz)));
    // event with one assignment v2 = 7 and one call command32(517)
    var ms=new MemoryStream();var w=new BinaryWriter(ms);
    void Num(int v){w.Write(12);w.Write(1);w.Write(v);}
    w.Write(20+16+12+12+12);w.Write(5);w.Write(0);w.Write(3);w.Write(16+12+12+12);
    w.Write(16+12);w.Write(4);w.Write(2);w.Write(12);Num(7);
    w.Write(12+12);w.Write(0x20);w.Write(12);Num(517);
    var parsed=ScriptParser.Parse(ms.ToArray());
    Equal("event (3 local values)|    v2 = 7|    command32(517)",string.Join("|",ScriptText.Format(parsed).TrimEnd().Split("\r\n")));
    var someNames=Enumerable.Range(0,40).Select(k=>$"Name{k}").ToList();
    Equal("event (3 local values)|    v2 = 7|    Name32(517)",string.Join("|",ScriptText.Format(parsed,someNames).TrimEnd().Split("\r\n")));
    Equal("wait",ScriptText.Name(ScriptParser.Wait,someNames));Equal("command9",ScriptText.Name(9,someNames));Equal("command50",ScriptText.Name(50,someNames));
});
Test("Compression round trips literals, repeats, control boundaries and distant copies",()=>{
    var random=new Random(48);
    var noise=new byte[9000];random.NextBytes(noise);
    var distant=noise.Concat(noise.Take(256)).ToArray();
    foreach(var data in new[]{new byte[]{42},Encoding.ASCII.GetBytes("abcdefghi"),Enumerable.Repeat((byte)7,800).ToArray(),
        Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("abcabcxyz",100))),noise,distant})
    {
        var packed=BerwickLz.Compress(data);
        Equal(data.Length,BinaryPrimitives.ReadInt32LittleEndian(packed));
        Equal(true,BerwickLz.Decompress(packed).AsSpan().SequenceEqual(data));
    }
    foreach(var data in new[]{new byte[]{42},Encoding.ASCII.GetBytes("abcdefghi"),Enumerable.Repeat((byte)7,800).ToArray(),noise,distant})
    {
        var packed=BerwickLz.Compress(data);var (game,read)=BerwickLz.DecompressToEndMarker(packed,data.Length);
        Equal(true,game.AsSpan().SequenceEqual(data));Equal(packed.Length-8,read);Equal(packed.Length-8,BinaryPrimitives.ReadInt32LittleEndian(packed.AsSpan(4)));
    }
    Throws<InvalidDataException>(()=>BerwickLz.DecompressToEndMarker([5,0,0,0,6,0,0,0,0xF8,104,101,108,108,111],100));
    Equal(true,BerwickLz.Compress(Enumerable.Repeat((byte)7,800).ToArray()).Length<40);
    Throws<ArgumentException>(()=>BerwickLz.Compress([]));
});
Test("Archive replacement preserves neighbors and rejects growth or ambiguous storage",()=>{
    // Two children: a literal-only compressed 'hello' in 14 bytes, and three untouched bytes.
    var archive=new byte[88];Encoding.ASCII.GetBytes("TARC").CopyTo(archive,0);
    void Put(int at,int value)=>BinaryPrimitives.WriteInt32LittleEndian(archive.AsSpan(at),value);
    Put(4,2);Put(8,40);Put(16,64*2+1);Put(20,21);Put(28,85*2);Put(32,3);
    Encoding.ASCII.GetBytes("a.dat\0b.dat\0").CopyTo(archive,40);
    byte[] original=[5,0,0,0,6,0,0,0,0xF8,104,101,108,108,111];original.CopyTo(archive,64);
    archive[85]=91;archive[86]=92;archive[87]=93;
    Equal(true,ArchiveEditing.Replace(archive,"a.dat",Encoding.ASCII.GetBytes("hello")).AsSpan().SequenceEqual(archive));
    var edited=ArchiveEditing.Replace(archive,"a.dat",Encoding.ASCII.GetBytes("world"));
    Equal(archive.Length,edited.Length);
    Equal("world",Encoding.ASCII.GetString(Tarc.Extract(edited,Tarc.Parse(edited)[0])));
    Equal(true,edited.AsSpan(81).SequenceEqual(archive.AsSpan(81)));
    Equal(true,edited.AsSpan(0,20).SequenceEqual(archive.AsSpan(0,20)));
    Equal(true,edited.AsSpan(24,40).SequenceEqual(archive.AsSpan(24,40)));
    Throws<EditException>(()=>ArchiveEditing.Replace(archive,"missing",[1]));
    Throws<EditException>(()=>ArchiveEditing.Replace(archive,"a.dat",Enumerable.Range(0,80).Select(i=>(byte)i).ToArray()));
    Put(28,68*2);Throws<EditException>(()=>ArchiveEditing.Replace(archive,"a.dat",[1]));Put(28,85*2);
    archive[46]=(byte)'a';Throws<EditException>(()=>ArchiveEditing.Replace(archive,"a.dat",[1]));
});
Test("Item encoder rejects out of range values",()=>{
    Throws<EditException>(()=>ItemEncoder.Write(new byte[56],ItemEncoder.Field("might"),64));
    Throws<EditException>(()=>ItemEncoder.Write(new byte[56],ItemEncoder.Field("durability"),-1));
    Throws<EditException>(()=>ItemEncoder.Field("nonsense"));
    Equal("Minimum range cannot be greater than maximum range.",ItemEncoder.Validate(new Dictionary<string,int>{["minRange"]=2,["maxRange"]=1}));
});
Test("Export refuses a differing packed byte in a table copy",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[0x01,0x21];
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");File.WriteAllBytes(src,data);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,[new DiscPatch(0,[2],[1]),new DiscPatch(1,[2],[1])]));
        Equal(false,File.Exists(dst));Equal(1,Directory.GetFiles(dir).Length);
        Equal(true,File.ReadAllBytes(src).AsSpan().SequenceEqual(data));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,[new DiscPatch(0,[2],[])]));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export copies the source and applies patches across buffer edges",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        var data=new byte[10*1024*1024+123];new Random(11).NextBytes(data);
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");File.WriteAllBytes(src,data);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        var patches=new[]{new DiscPatch(0,new byte[]{1,2,3}),new DiscPatch(4*1024*1024-10,Enumerable.Repeat((byte)0xAB,30).ToArray()),new DiscPatch(data.Length-5,new byte[]{9,9,9,9,9})};
        patches=patches.Select(p=>p with {Expected=data.AsSpan((int)p.Offset,p.Bytes.Length).ToArray()}).ToArray();
        var expected=(byte[])data.Clone();foreach(var p in patches)p.Bytes.CopyTo(expected,p.Offset);
        int differing=expected.Zip(data).Count(z=>z.First!=z.Second);
        var result=Exporter.Export(src,sha,dst,patches);
        var written=File.ReadAllBytes(dst);
        Equal(true,written.AsSpan().SequenceEqual(expected));Equal(differing,result.ChangedBytes);Equal(3,result.PatchCount);
        Equal(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(expected)).ToLowerInvariant(),result.Sha256);
        Equal(true,File.ReadAllBytes(src).AsSpan().SequenceEqual(data));
        Equal(false,File.Exists(dst+".partial"));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export preserves an existing partial file",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[1,2,3,4], previous=[8,9,10];
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");
        File.WriteAllBytes(src,data);File.WriteAllBytes(dst+".partial",previous);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        Exporter.Export(src,sha,dst,[new DiscPatch(0,[5])]);
        Equal(true,File.Exists(dst+".partial"));
        Equal(true,File.ReadAllBytes(dst+".partial").AsSpan().SequenceEqual(previous));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export preserves a source named like a partial file",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[1,2,3,4];
        string dst=Path.Combine(dir,"out.iso"),src=dst+".partial";
        File.WriteAllBytes(src,data);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        try{Exporter.Export(src,sha,dst,[new DiscPatch(0,[5])]);}catch(IOException){}
        Equal(true,File.Exists(src));
        Equal(true,File.ReadAllBytes(src).AsSpan().SequenceEqual(data));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export never replaces an existing destination",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[1,2,3,4], previous=[8,9,10];
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");
        File.WriteAllBytes(src,data);File.WriteAllBytes(dst,previous);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,[new DiscPatch(0,[5])]));
        Equal(true,File.ReadAllBytes(dst).AsSpan().SequenceEqual(previous));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export cancellation during verification leaves no output",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[1,2,3,4];
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");File.WriteAllBytes(src,data);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data));
        using var cancel=new CancellationTokenSource();
        var progress=new ImmediateProgress(p=>{if(p.Message=="Checking exported disc")cancel.Cancel();});
        Throws<OperationCanceledException>(()=>Exporter.Export(src,sha,dst,[new DiscPatch(0,[5])],progress,cancel.Token));
        Equal(false,File.Exists(dst));Equal(1,Directory.GetFiles(dir).Length);
    }
    finally{Directory.Delete(dir,true);}
});
Test("Project files reject null entries with a readable error",()=>{
    var disc=new InspectionResult("source.iso",4,"","","","hash","",Profiles.All[0],[],[],0);
    string json=new EditSession(disc).ToJson();
    foreach(string field in new[]{"changes","copies","names"})
    {
        var document=System.Text.Json.Nodes.JsonNode.Parse(json)!;
        document[field]=System.Text.Json.Nodes.JsonNode.Parse("[null]");
        Throws<EditException>(()=>EditSession.FromJson(disc,document.ToJsonString()));
    }
});
Test("Export patches refuse a different disc from the project",()=>{
    var disc=new InspectionResult("source.iso",4,"","","","first","",Profiles.All[0],[],[],0);
    var session=new EditSession(disc);
    Throws<EditException>(()=>Exporter.PatchesFor(session,disc with {Sha256="second",Profile=Profiles.All[1]}));
});
Test("Project saves preserve discs and existing temporary files",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-save-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        byte[] data=[1,2,3,4], previous=[8,9,10];
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"project.bwproj");
        File.WriteAllBytes(src,data);File.WriteAllBytes(dst+".tmp",previous);
        var disc=new InspectionResult(src,data.Length,"","","","hash","",Profiles.All[0],[],[],0);
        var session=new EditSession(disc);
        Throws<EditException>(()=>session.Save(src));
        Equal(true,File.ReadAllBytes(src).AsSpan().SequenceEqual(data));
        session.Save(dst);
        Equal(true,File.ReadAllBytes(dst+".tmp").AsSpan().SequenceEqual(previous));
        Equal(0,EditSession.Load(disc,dst).ChangeCount);
        session.Save(dst);
        Equal(0,EditSession.Load(disc,dst).ChangeCount);
        string disguised=Path.Combine(dir,"source.bwproj");File.WriteAllBytes(disguised,data);
        Throws<EditException>(()=>new EditSession(disc with {Path=disguised}).Save(disguised));
        Equal(true,File.ReadAllBytes(disguised).AsSpan().SequenceEqual(data));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Export refuses unsafe requests and leaves no partial file",()=>{
    string dir=Path.Combine(Path.GetTempPath(),$"bw-export-{Guid.NewGuid():N}");Directory.CreateDirectory(dir);
    try
    {
        var data=new byte[100000];new Random(5).NextBytes(data);
        string src=Path.Combine(dir,"source.iso"),dst=Path.Combine(dir,"out.iso");File.WriteAllBytes(src,data);
        string sha=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(data)).ToLowerInvariant();
        var one=new[]{new DiscPatch(10,new byte[]{1})};
        Throws<EditException>(()=>Exporter.Export(src,sha,src,one));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,[]));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,new[]{new DiscPatch(10,new byte[4]),new DiscPatch(12,new byte[4])}));
        Throws<EditException>(()=>Exporter.Export(src,sha,dst,new[]{new DiscPatch(data.Length-1,new byte[2])}));
        Throws<EditException>(()=>Exporter.Export(src,new string('0',64),dst,one));
          Equal(false,File.Exists(dst));Equal(false,File.Exists(dst+".partial"));
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        Throws<OperationCanceledException>(()=>Exporter.Export(src,sha,dst,one,null,cancel.Token));
        Equal(false,File.Exists(dst));Equal(false,File.Exists(dst+".partial"));
        Equal(true,File.ReadAllBytes(src).AsSpan().SequenceEqual(data));
    }
    finally{Directory.Delete(dir,true);}
});
Test("Character and class encoders are exact inverses, including signed and scaled fields",()=>{
    var rng=new Random(17);
    foreach(var (fields,size) in new[]{(CharacterFields.Record,0x100),(CharacterFields.Growth,32),(ClassFields.Fields,100)})
        foreach(var f in fields)
            foreach(int v in new[]{f.Min,f.Max,(f.Min+f.Max)/2,f.Signed?-1:f.Min})
            {
                var before=new byte[size];rng.NextBytes(before);var after=(byte[])before.Clone();
                FieldCodec.Write(after,f,v);Equal($"{f.Key}={v}",$"{f.Key}={FieldCodec.Read(after,f)}");
                for(int bit=0;bit<size*8;bit++)
                {
                    bool changed=((before[bit/8]^after[bit/8])>>(bit%8)&1)!=0;
                    bool inside=bit>=f.Offset*8+f.Bit && bit<f.Offset*8+f.Bit+f.Width;
                    if(changed && !inside) throw new Exception($"{f.Key}={v} changed bit {bit} outside its field");
                }
            }
});
Test("Character and class fields read the same values as the decoders",()=>{
    var rng=new Random(23);
    for(int round=0;round<20;round++)
    {
        var u=new byte[0x100];var g=new byte[32];var k=new byte[100];rng.NextBytes(u);rng.NextBytes(g);rng.NextBytes(k);
        var c=CharacterDecoder.Decode(new(1,"x",0),u,g,new Dictionary<int,string>());
        int F(IReadOnlyList<TableField> fs,byte[] b,string key)=>FieldCodec.Read(b,fs.Single(f=>f.Key==key));
        Equal($"{c.Level}/{c.HpBonus}/{c.StrengthBonus}/{c.DefenseBonus}/{c.AgilityBonus}/{c.MindBonus}",
              $"{F(CharacterFields.Record,u,"level")}/{F(CharacterFields.Record,u,"hpBonus")}/{F(CharacterFields.Record,u,"strengthBonus")}/{F(CharacterFields.Record,u,"defenseBonus")}/{F(CharacterFields.Record,u,"agilityBonus")}/{F(CharacterFields.Record,u,"mindBonus")}");
        Equal($"{c.HpGrowth}/{c.StrengthGrowth}/{c.DefenseGrowth}/{c.AgilityGrowth}/{c.MindGrowth}",
              $"{F(CharacterFields.Growth,g,"hpGrowth")}/{F(CharacterFields.Growth,g,"strengthGrowth")}/{F(CharacterFields.Growth,g,"defenseGrowth")}/{F(CharacterFields.Growth,g,"agilityGrowth")}/{F(CharacterFields.Growth,g,"mindGrowth")}");
        foreach(var w in c.WeaponLevels) Equal(w.Value,F(CharacterFields.Record,u,CharacterFields.Key(w.Weapon,"Level")));
        foreach(var w in c.WeaponGrowths) Equal(w.Value,F(CharacterFields.Growth,g,CharacterFields.Key(w.Weapon,"Growth"))*10);
        var cl=ClassDecoder.Decode(1,"x",k);
        Equal($"{cl.Hp}/{cl.Strength}/{cl.Defense}/{cl.Agility}/{cl.Mind}/{cl.Move}/{cl.Experience}",
              $"{F(ClassFields.Fields,k,"hp")}/{F(ClassFields.Fields,k,"strength")}/{F(ClassFields.Fields,k,"defense")}/{F(ClassFields.Fields,k,"agility")}/{F(ClassFields.Fields,k,"mind")}/{F(ClassFields.Fields,k,"move")}/{F(ClassFields.Fields,k,"experience")}");
        foreach(var w in cl.WeaponCaps) Equal(w.Value,F(ClassFields.Fields,k,CharacterFields.Key(w.Weapon,"Cap")));
    }
});
Test("Skill, starting item and bracket fields agree with the decoders",()=>{
    var rng=new Random(29);
    for(int round=0;round<20;round++)
    {
        var u=new byte[0x100];var g=new byte[32];var k=new byte[100];rng.NextBytes(u);rng.NextBytes(g);rng.NextBytes(k);
        var names=Enumerable.Range(0,65536).ToDictionary(i=>i,i=>$"I{i}");
        var c=CharacterDecoder.Decode(new(1,"x",0),u,g,names);
        var set=CharacterFields.Record.Where(f=>f.Group=="Personal skills" && FieldCodec.Read(u,f)==1).Select(f=>f.Label).ToList();
        Equal(string.Join(",",c.Skills.Where(s=>!s.StartsWith("Skill bit"))),string.Join(",",set));
        for(int slot=1;slot<=CharacterFields.ItemSlots;slot++)
        {
            int id=FieldCodec.Read(u,CharacterFields.Record.Single(f=>f.Key==$"item{slot}"));
            int uses=FieldCodec.Read(u,CharacterFields.Record.Single(f=>f.Key==$"item{slot}Uses"));
            var match=c.Items.Where(i=>i.ItemId==id).ToList();
            if(id!=0) Equal(true,match.Any(i=>i.Durability==uses));
        }
        Equal(c.Bracket,FieldCodec.Read(g,CharacterFields.Growth.Single(f=>f.Key=="bracket")));
        var cl=ClassDecoder.Decode(1,"x",k);
        var classSet=ClassFields.Fields.Where(f=>f.Group=="Class skills" && FieldCodec.Read(k,f)==1).Select(f=>f.Label).ToList();
        Equal(string.Join(",",cl.Skills.Where(s=>!s.StartsWith("Skill bit"))),string.Join(",",classSet));
    }
    var bracket=CharacterFields.Growth.Single(f=>f.Key=="bracket");
    Equal("Loose (±3)",FieldCodec.Format(bracket,2));Equal(true,FieldCodec.TryParse(bracket,"tight",out int b,out _));Equal(3,b);
    Equal("Yes",FieldCodec.Format(CharacterFields.Record.First(f=>f.IsFlag),1));
    Equal("S",FieldCodec.Format(ItemEncoder.Field("durability"),0));
});
Test("Game rules encode and decode every setting, and reject unknown code",()=>{
    foreach(var rule in GameRules.All)
    {
        for(int s=rule.Min;s<=rule.Max;s++)
        {
            uint[] words;
            try{words=rule.Encode(s);}catch(EditException){if(rule.Kind==RuleKind.Range)continue;throw;}
            Equal($"{rule.Key}={s}",$"{rule.Key}={rule.Decode(words)}");
        }
        Equal<int?>(null,rule.Decode(Enumerable.Repeat(0xDEADBEEFu,rule.Sites.Count).ToArray()));
        Throws<EditException>(()=>rule.Encode(rule.Max+1));
        Equal($"{rule.Key}={rule.Default}",$"{rule.Key}={rule.Decode(rule.Original)}");
    }
    var parry=GameRules.ById(1);Equal(0x1020000Cu,parry.Original[0]);Equal(0x14000000u,parry.Encode(1)[0]);Equal(0x1000000Cu,parry.Encode(2)[0]);
    Equal("Parry",parry.Name);Equal("139,142,143,144,145,146,147,148,154,157,158,159,160,161,165,168",string.Join(",",GameRules.All.Where(r=>!r.Confirmed).Select(r=>r.Id)));
    var block=GameRules.ById(9);Equal(0x10200019u,block.Original[0]);Equal(0x14000019u,block.Encode(1)[0]);Equal(0x10000019u,block.Encode(2)[0]);
    var training=GameRules.ById(11);Equal(0x001716A4u,training.Sites[0].Address);Equal(0x24090004u,training.Original[0]);Equal(0x24090032u,training.Encode(50)[0]);
    Equal(0x24090001u,GameRules.ById(12).Original[0]);Equal<int?>(null,training.Decode(new[]{0x24080004u}));
    var crit=GameRules.ById(13);Equal("2405000A,24060014",string.Join(",",crit.Original.Select(x=>x.ToString("X8"))));Equal("10 to 20 (normal)",crit.Describe(crit.Default));
    Equal("24050019,24060019",string.Join(",",crit.Encode(GameRules.RangeSetting(25,25)).Select(x=>x.ToString("X8"))));
    Throws<EditException>(()=>crit.Encode(GameRules.RangeSetting(30,20)));Equal<int?>(null,crit.Decode(new[]{0x2405001Eu,0x24060014u}));
    var failed=GameRules.ById(14);Equal(0x28A10002u,failed.Original[0]);Equal(0x28A10006u,failed.Encode(1)[0]);
    var guard=GameRules.ById(10);Equal(26,guard.Sites.Count);Equal(0x0022A640u,guard.Sites[0].Address);Equal(0x0022A6A4u,guard.Sites[25].Address);
    Equal(0x24020001u,guard.Original[0]);Equal(0x10000003u,guard.Original[22]);Equal(0xAFA20130u,guard.Original[25]);
    Equal("00031883,00031843,00031803",string.Join(",",new[]{1,2,3}.Select(x=>guard.Encode(x)[20].ToString("X8"))));
    Equal(20,Enumerable.Range(0,26).Count(i=>guard.Encode(2)[i]!=guard.Original[i]));
    var mixed=guard.Encode(2);mixed[20]=guard.Original[20];Equal<int?>(null,guard.Decode(mixed));
    mixed=guard.Original.ToArray();mixed[20]=guard.Encode(3)[20];Equal<int?>(null,guard.Decode(mixed));
    var vantage=GameRules.ById(15);Equal(36,vantage.Sites.Count);Equal(0x00224E68u,vantage.Sites[0].Address);Equal(0x00224EF4u,vantage.Sites[35].Address);
    Equal(0x12600005u,vantage.Original[0]);Equal(0xAE6211E0u,vantage.Original[35]);Equal(0x0C08B878u,vantage.Encode(1)[2]);
    Equal("24090000,24090001",$"{vantage.Encode(1)[12]:X8},{vantage.Encode(2)[12]:X8}");Equal(34,Enumerable.Range(0,36).Count(i=>vantage.Encode(2)[i]!=vantage.Original[i]));
    Equal<int?>(1,vantage.Decode(vantage.Encode(1)));Equal<int?>(2,vantage.Decode(vantage.Encode(2)));
    mixed=vantage.Encode(2);mixed[12]=vantage.Original[12];Equal<int?>(null,vantage.Decode(mixed));
    mixed=vantage.Original.ToArray();mixed[12]=vantage.Encode(2)[12];Equal<int?>(null,vantage.Decode(mixed));
    var exp=GameRules.ById(2);Equal(0x0040982Du,exp.Original[0]);Equal(0x24130064u,exp.Encode(100)[0]);Equal<int?>(null,exp.Decode(new[]{0x24130065u}));
    Equal(0x1275ECL,parry.Sites[0].FileOffset);
    var str=GameRules.ById(4);Equal("2411000F,2412000F",string.Join(",",str.Original.Select(x=>x.ToString("X8"))));
    Equal<int?>(null,str.Decode(new[]{0x2411000Au,0x2412000Fu}));Equal("2411000A,2412000A",string.Join(",",str.Encode(10).Select(x=>x.ToString("X8"))));
    var exe=new byte[0x400000];
    void Put(GameRule r,int s){var ws=r.Encode(s);for(int i=0;i<ws.Length;i++)BitConverter.GetBytes(ws[i]).CopyTo(exe,r.Sites[i].FileOffset);}
    foreach(var r in GameRules.All) Put(r,r.Default);
    Put(parry,1);Put(exp,40);Put(str,5);
    var norm=GameRules.Normalize(exe);foreach(var r in GameRules.All) Equal($"{r.Key}={r.Default}",$"{r.Key}={r.Decode(GameRules.WordsAt(norm,r))}");
    BitConverter.GetBytes(0xDEADBEEFu).CopyTo(exe,parry.Sites[0].FileOffset);Equal(0xDEADBEEFu,GameRules.WordAt(GameRules.Normalize(exe),parry.Sites[0]));
});
Test("Shown values parse back to stored values",()=>{
    var level=CharacterFields.Record.Single(f=>f.Key=="swordLevel");var growth=CharacterFields.Growth.Single(f=>f.Key=="swordGrowth");
    Equal("21",FieldCodec.Format(level,210));Equal("0.8",FieldCodec.Format(level,8));Equal("60",FieldCodec.Format(growth,6));
    Equal(true,FieldCodec.TryParse(level,"21.5",out int v,out _));Equal(215,v);
    Equal(false,FieldCodec.TryParse(level,"21.55",out _,out _));
    Equal(true,FieldCodec.TryParse(growth,"70",out v,out _));Equal(7,v);
    Equal(false,FieldCodec.TryParse(growth,"75",out _,out _));Equal(false,FieldCodec.TryParse(growth,"160",out _,out _));
    var bonus=CharacterFields.Record.Single(f=>f.Key=="strengthBonus");
    Equal(true,FieldCodec.TryParse(bonus,"-3",out v,out _));Equal(-3,v);Equal(false,FieldCodec.TryParse(bonus,"16",out _,out _));
});
Test("Terrain effects decode signed values and the flyer flag, and 32-bit fields round trip",()=>{
    var row=new byte[TerrainKindData.RowLength];
    FieldCodec.Write(row,TerrainKindData.Fields[1],-20);Equal("EC-FF-FF-FF",BitConverter.ToString(row,0x10,4));Equal(-20,FieldCodec.Read(row,TerrainKindData.Fields[1]));
    FieldCodec.Write(row,TerrainKindData.Fields[1],30);Equal("1E-00-00-00",BitConverter.ToString(row,0x10,4));
    row[0x14]=0xC1;FieldCodec.Write(row,TerrainKindData.Fields[2],0);Equal(0x81,(int)row[0x14]);
    FieldCodec.Write(row,TerrainKindData.Fields[0],-5);var k=TerrainKindData.Decode(36,row);
    Equal("-5/+30%/False/Yes",$"{k.Avoid}/{k.RecoveryText}/{k.FlyersCannotStop}/{k.FlyersText}");
    Throws<EditException>(()=>FieldCodec.Write(row,TerrainKindData.Fields[1],101));
    Equal(4+64*24,TerrainKindData.TableLength);
});
Test("Experience formula values encode as data and as instructions",()=>{
    var gap=GameRules.ById(20);Equal(0x00429F10u,gap.Sites[0].Address);Equal(0xFFFFFFD8u,gap.Original[0]);Equal(0xFFFFFFFDu,gap.Encode(-3)[0]);
    Equal(0x00429F4Cu,GameRules.ById(35).Sites[0].Address);Equal(15,GameRules.ById(35).Default);Equal("8 or more below/Same level/1 above/7 or more above",string.Join("/",new[]{20,28,29,35}.Select(i=>GameRules.ById(i).Short)));
    Equal<int?>(null,gap.Decode(new[]{0x00000065u}));Equal<int?>(-1,gap.Decode(new[]{0xFFFFFFFFu}));Equal<int?>(-2,gap.Decode(new[]{0xFFFFFFFEu}));Equal<int?>(-100,gap.Decode(new[]{0xFFFFFF9Cu}));
    var highest=GameRules.ById(36);Equal("28410065,24020064",string.Join(",",highest.Original.Select(x=>x.ToString("X8"))));Equal("28410033,24020032",string.Join(",",highest.Encode(50).Select(x=>x.ToString("X8"))));
    Equal<int?>(null,highest.Decode(new[]{0x28410033u,0x24020064u}));
    Equal(0x26940006u,GameRules.ById(37).Original[0]);
    var random=GameRules.ById(38);Equal("24060003,28410004,24020003",string.Join(",",random.Original.Select(x=>x.ToString("X8"))));Equal("2406000A,2841000B,2402000A",string.Join(",",random.Encode(10).Select(x=>x.ToString("X8"))));
    Equal(142,GameRules.All.Count(r=>r.Group.Length>0));
    var iaido=GameRules.ById(40);Equal(0x24700028u,iaido.Original[0]);Equal(0x24700064u,iaido.Encode(100)[0]);Equal(0x2475001Eu,GameRules.ById(41).Original[0]);
    var spearbane=GameRules.ById(42);Equal("2A010021,24100021",string.Join(",",spearbane.Original.Select(x=>x.ToString("X8"))));Equal("2A010032,24100032",string.Join(",",spearbane.Encode(50).Select(x=>x.ToString("X8"))));
    Equal<int?>(null,spearbane.Decode(new[]{0x2A010032u,0x24100021u}));
    Equal("0x2275F0,0x227694,0x227734,0x227550,0x2277D4",string.Join(",",new[]{42,43,44,45,46}.Select(i=>$"0x{GameRules.ById(i).Sites[0].Address:X}")));
    Equal(107,GameRules.All.Count(r=>r.Section==GameRules.SkillsSection));Equal(0x246300FFu,GameRules.ById(94).Original[0]);Equal(0x24630000u,GameRules.ById(94).Encode(1)[0]);
    Equal(0x24040004u,GameRules.ById(90).Original[0]);Equal(0x24040001u,GameRules.ById(90).Encode(1)[0]);Throws<EditException>(()=>GameRules.ById(90).Encode(0));Equal<int?>(null,GameRules.ById(90).Decode(new[]{0x24040000u}));
    var swim=GameRules.ById(93);Equal(26,swim.Sites.Count);Equal(0x10400019u,swim.Original[0]);Equal(0x0C05F7ECu,swim.Encode(1)[2]);Equal<int?>(1,swim.Decode(swim.Encode(1)));Equal("On foot only",swim.Describe(1));
    var sunder=GameRules.ById(53);Equal("24420019,24420019,24620019",string.Join(",",sunder.Original.Select(x=>x.ToString("X8"))));Equal("24420046,24420046,24620046",string.Join(",",sunder.Encode(70).Select(x=>x.ToString("X8"))));
    Equal<int?>(70,sunder.Decode(new[]{0x24420046u,0x24420046u,0x24620019u}));Equal<int?>(null,sunder.Decode(new[]{0x24420046u,0x24420046u,0x24620020u}));
    Equal("24420021,24420021,24620021",string.Join(",",GameRules.ById(55).Original.Select(x=>x.ToString("X8"))));Equal("2442FFF1,2462FFF1",string.Join(",",GameRules.ById(56).Original.Select(x=>x.ToString("X8"))));Equal("2442FFCE,2462FFCE",string.Join(",",GameRules.ById(56).Encode(-50).Select(x=>x.ToString("X8"))));Equal<int?>(-1,GameRules.ById(56).Decode(new[]{0x2442FFFFu,0x2462FFF1u}));Equal<int?>(null,GameRules.ById(56).Decode(new[]{0x2442FFFFu,0x2462FFFEu}));
    var aimMove=GameRules.ById(60);Equal(0x30420001u,aimMove.Original[0]);Equal(0x30420000u,aimMove.Encode(0)[0]);Equal("Not allowed",aimMove.Describe(aimMove.Default));Equal<int?>(null,aimMove.Decode(new[]{0x30420003u}));
    Equal(0x30624282u,GameRules.ById(71).Encode(0)[0]);Equal(0x30424283u,GameRules.ById(76).Encode(1)[0]);Equal(0x30420003u,GameRules.ById(78).Encode(1)[0]);Equal("Allowed",GameRules.ById(76).Describe(GameRules.ById(76).Default));
    var deathmatch=GameRules.ById(85);Equal("30424282,30420001,30420001",string.Join(",",deathmatch.Original.Select(x=>x.ToString("X8"))));Equal("30424282,30420000,30420000",string.Join(",",deathmatch.Encode(0).Select(x=>x.ToString("X8"))));
    Equal("30424283,30420001,30420001",string.Join(",",deathmatch.Encode(1).Select(x=>x.ToString("X8"))));Equal<int?>(0,deathmatch.Decode(new[]{0x30424282u,0x30420000u,0x30420000u}));Equal("Weapon decides",deathmatch.Describe(2));Equal(0x24420016u,GameRules.ById(54).Original[0]);Equal(0x24420064u,GameRules.ById(54).Encode(100)[0]);
    Equal(0x2443000Au,GameRules.ById(50).Original[0]);Equal(0x0002B040u,GameRules.ById(52).Original[0]);Equal(0x0002B080u,GameRules.ById(52).Encode(2)[0]);Equal("Times 4",GameRules.ById(52).Describe(2));Equal(0x240200BEu,GameRules.ById(51).Original[0]);Equal(0x2402012Cu,GameRules.ById(51).Encode(300)[0]);
    Equal("28420005,28630005",string.Join(",",GameRules.ById(47).Original.Select(x=>x.ToString("X8"))));Equal(0x26100012u,GameRules.ById(48).Original[0]);
    var paragon=GameRules.ById(49);Equal("3C026666,34426667,00021043",string.Join(",",paragon.Original.Select(x=>x.ToString("X8"))));Equal("40%",paragon.Describe(5));
    Equal("3C026666,34426667,00021003",string.Join(",",paragon.Encode(5).Select(x=>x.ToString("X8"))));Equal<int?>(null,paragon.Decode(new[]{0x3C026666u,0x34426667u,0x00021083u|0x40u}));Equal(true,GameRules.All.Where(r=>r.Group.Length>0).All(r=>GameRules.GroupDescriptions.ContainsKey(r.Group) && r.Short.Length>0));
    Equal(GameRules.All.Count,GameRules.All.Select(r=>r.Id).Distinct().Count());Equal(GameRules.All.Count,GameRules.All.Select(r=>r.Key).Distinct().Count());
});
Test("Terrain costs show tenths and the impassable mark, and parse back",()=>{
    var forest=TerrainData.Fields[4];
    Equal("4. Forest",forest.Label);Equal("2",FieldCodec.Format(forest,20));Equal("1.5",FieldCodec.Format(forest,15));Equal("×",FieldCodec.Format(forest,255));
    Equal(true,FieldCodec.TryParse(forest,"1.5",out int v,out _));Equal(15,v);
    Equal(true,FieldCodec.TryParse(forest,"x",out v,out _));Equal(255,v);Equal(true,FieldCodec.TryParse(forest,"×",out v,out _));Equal(255,v);
    Equal(false,FieldCodec.TryParse(forest,"25.5",out _,out _));Equal(false,FieldCodec.TryParse(forest,"0",out _,out _));Equal(false,FieldCodec.TryParse(forest,"1.55",out _,out _));
    Equal("0.1 to 25.4, or ×",FieldCodec.RangeText(forest));
    Equal(TerrainData.Terrains,TerrainData.Fields.Count);Equal(TerrainData.Terrains,TerrainData.TerrainNames.Length);Equal(TerrainData.Types,TerrainData.TypeNames.Length);
    var row=new byte[TerrainData.RowLength];row[0]=2;row[4+50]=70;var m=TerrainData.Decode(2,row);Equal("Flying/Flying/7",$"{m.Name}/{m.MountText}/{m.Cost(50)}");
});
Test("Live game: units, record and item edits, rules and undo over a stand-in for PCSX2",()=>{
    var memory=new byte[0x02000000];
    void Put(uint a,uint v)=>BinaryPrimitives.WriteUInt32LittleEndian(memory.AsSpan((int)a),v);
    uint Get(uint a)=>BinaryPrimitives.ReadUInt32LittleEndian(memory.AsSpan((int)a));
    const uint unit=0x01D00100,battle=0x01D10000,list=0x01D20000;
    BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)unit+8),5);BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)unit+10),5);
    memory[unit+0xF]=0x20;BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)unit+0x10),(ushort)(7|30<<6));
    Put(unit+0xA8,battle);Put(battle,0);Put(battle+4,3);Put(battle+8,4);
    Put(unit+0x7C,list);Put(list,1);Put(list+4,0x22u|40u<<20);
    // An enemy whose script ID (2) is not its name ID (1025), as reinforcements and many story enemies have.
    const uint enemy=0x01D00400,enemyBattle=0x01D10100;
    BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)enemy+8),2);BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)enemy+10),1025);
    memory[enemy+0xF]=0x69;BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)enemy+0x10),(ushort)(2|26<<6));
    Put(enemy+0xA8,enemyBattle);Put(enemyBattle,1);Put(enemyBattle+4,1);Put(enemyBattle+8,6);
    var program=new byte[0x004B4300-(int)GameRules.ProgramToFile];new Random(3).NextBytes(program);
    program.AsSpan(0x0041BE00-(int)GameRules.ProgramToFile).CopyTo(memory.AsSpan(0x0041BE00,program.Length-(0x0041BE00-(int)GameRules.ProgramToFile)));
    var rule=GameRules.ById(117);
    var original=rule.Original;for(int i=0;i<original.Length;i++)Put(rule.Sites[i].Address,original[i]);
    var listener=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listener.Start();
    int port=((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    var server=new Thread(()=>{
        using var client=listener.AcceptTcpClient();using var stream=client.GetStream();
        var head=new byte[4];
        while(true)
        {
            int got=0;while(got<4){int n=stream.Read(head,got,4-got);if(n==0)return;got+=n;}
            var body=new byte[BinaryPrimitives.ReadInt32LittleEndian(head)-4];got=0;while(got<body.Length)got+=stream.Read(body,got,body.Length-got);
            var reply=new List<byte>{0};
            for(int at=0;at<body.Length;)
            {
                byte op=body[at];uint a=at+5<=body.Length?BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at+1)):0;
                switch(op)
                {
                    case 2:reply.AddRange(memory.AsSpan((int)a,4).ToArray());at+=5;break;
                    case 3:reply.AddRange(memory.AsSpan((int)a,8).ToArray());at+=5;break;
                    case 6:Put(a,BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(at+5)));at+=9;break;
                    case 12:{var id=Encoding.ASCII.GetBytes("SLPS-25497\0");reply.AddRange(BitConverter.GetBytes(id.Length));reply.AddRange(id);at+=1;break;}
                    case 15:reply.AddRange(BitConverter.GetBytes(0));at+=1;break;
                    default:throw new Exception($"op {op}");
                }
            }
            stream.Write(BitConverter.GetBytes(reply.Count+4));stream.Write(reply.ToArray());
        }
    }){IsBackground=true};
    server.Start();
    using(var game=LiveGame.Connect(port))
    {
        Equal("SLPS-25497",game.GameId());Equal(true,game.Running());
        Equal(true,game.MatchesProgram(program));
        var units=game.Units(new Dictionary<int,string>{[5]="Ward",[1025]="Muthay Gang"});
        Equal(2,units.Count);var ward=units[0];
        Equal("Muthay Gang 1 1,6 2",$"{units[1].Name} {units[1].Army} {units[1].X},{units[1].Y} {units[1].Level}");
        Equal("Ward 0 3,4 7 30 32",$"{ward.Name} {ward.Army} {ward.X},{ward.Y} {ward.Level} {ward.Hp} {ward.ClassId}");
        var record=game.ReadRecord(ward);var level=CharacterFields.Record.First(f=>f.Key=="level");
        Equal(7,FieldCodec.Read(record,level));Equal(0x20,(int)record[4+0xF]);
        FieldCodec.Write(record,level,9);Equal(1,game.WriteRecord(ward,record));
        ward=game.Units(new Dictionary<int,string>{[5]="Ward"})[0];Equal("9 30",$"{ward.Level} {ward.Hp}");
        Equal(new LiveItem(0,0x22,40),game.Items(ward)[0]);
        game.SetItem(ward,0,0x30,12);Equal(new LiveItem(0,0x30,12),game.Items(ward)[0]);
        int setting=rule.Default==rule.Min?rule.Max:rule.Min;
        Equal(true,game.WriteRule(rule,setting));Equal(setting,rule.Decode(rule.Sites.Select(x=>Get(x.Address)).ToArray()));
        Equal(true,game.ChangedWords>=3);
        game.UndoAll();Equal(0,game.ChangedWords);
        Equal(7,memory[unit+0x10]&0x3F);Equal(0x22u|40u<<20,Get(list+4));Equal(rule.Default,rule.Decode(rule.Sites.Select(x=>Get(x.Address)).ToArray()));
        // Undo leaves a word the game has changed since, and finds an item slot again after the game moves the item list.
        FieldCodec.Write(record,level,11);game.WriteRecord(ward,record);
        BinaryPrimitives.WriteUInt16LittleEndian(memory.AsSpan((int)unit+0x10),(ushort)(12|30<<6));
        game.SetItem(ward,0,0x31,7);
        const uint moved=0x01D30000;Put(moved,1);Put(moved+4,Get(list+4));Put(list+4,0);Put(unit+0x7C,moved);
        Equal(2,game.ChangedWords);Equal(1,game.UndoAll());
        Equal(12,memory[unit+0x10]&0x3F);Equal(0x22u|40u<<20,Get(moved+4));Equal(0u,Get(list+4));
        Put(unit+0x7C,list);Put(list+4,0x22u|40u<<20);memory[unit+0x10]=(byte)((memory[unit+0x10]&0xC0)|7);
        // A rule is left alone where the running program has other code, and a different program is noticed.
        Put(rule.Sites[0].Address,0xFFFFFFFF);Equal(false,game.WriteRule(rule,setting));Equal(0xFFFFFFFFu,Get(rule.Sites[0].Address));Equal(0,game.ChangedWords);
        Put(0x0041BE54,Get(0x0041BE54)^1);Equal(false,game.MatchesProgram(program));
        // Sorties: every mission and quest battle loads the chosen battle until put back; quests that are not battles stay as they are.
        void Both(uint a,uint v){Put(a,v);BinaryPrimitives.WriteUInt32LittleEndian(program.AsSpan((int)(a-GameRules.ProgramToFile)),v);}
        for(int k=0;k<15;k++)Both(0x0046D8F0+(uint)k*16,0x01000000u|(uint)(k+20)<<12|(uint)(k+1));
        for(int k=0;k<218;k++){Both(0x0046D9E0+(uint)k*52,0x32000000u|(k%4==0?0u:(uint)(100+k)<<12)|(uint)((k+1)&0xFF));Both(0x0046D9E4+(uint)k*52,1);}
        var battles=LiveGame.Battles(program);
        Equal(15+218-55,battles.Count);Equal(new LiveBattle("Story mission 3",22),battles[2]);Equal(new LiveBattle("Quest 6",105),battles.First(b=>b.Name=="Quest 6"));
        Equal(0,LiveGame.Battles(program.AsSpan(0,0x00360000)).Count);
        Equal(false,game.SortiesChanged);Equal(178,game.SendSorties(13));Equal(true,game.SortiesChanged);
        Equal(13u,Get(0x0046D8F0)>>12&0xFFF);Equal(0x01000001u,Get(0x0046D8F0)&~(0xFFFu<<12));Equal(0x32000001u,Get(0x0046D9E0));Equal(13u,Get(0x0046D9E0+52)>>12&0xFFF);
        Equal(178,game.SendSorties(31));Equal(31u,Get(0x0046D9E0+52)>>12&0xFFF);
        Equal(0,game.RestoreSorties());Equal(false,game.SortiesChanged);Equal(0x01014001u,Get(0x0046D8F0));Equal(0x32065002u,Get(0x0046D9E0+52));
        game.SendSorties(9);game.UndoAll();Equal(0x01014001u,Get(0x0046D8F0));Equal(false,game.SortiesChanged);
        Put(0x0046D8F0,0);Throws<IOException>(()=>game.SendSorties(9));Equal(0,game.ChangedWords);Throws<ArgumentOutOfRangeException>(()=>game.SendSorties(0));
    }
    listener.Stop();
    Throws<IOException>(()=>LiveGame.Connect(port));
});
Console.WriteLine($"{passed} passed, {failed} failed");
if(args.Length>=2)
{
    Test("Japanese secondary table matches all primary records",()=>{
        using var iso=new IsoImage(File.OpenRead(args[0]));
        var profile=Profiles.Match("77a280686e34d26053ce03873eb6cd0f7fd9730c0b4d4e104f6eb63ac5bd7320")!;
        long start=iso.Entries.Single(e=>e.Name=="DATA3.DAT").Offset;
        var a=iso.ReadRange(start+profile.ItemTable,36176,40000);
        var b=iso.ReadRange(start+profile.SecondItemTable,36176,40000);
        Equal(true,a.SequenceEqual(b));
    });
    InspectionResult? jp=null,en=null;
    Test("Japanese disc identity and item values",()=>{
        jp=Inspector.Inspect(args[0]);Equal("Japanese retail",jp.Edition);Equal("2FCA8492",jp.Crc);Equal(646,jp.Items.Count);
        var bronze=jp.Items.Single(i=>i.Id==34);Equal(6,bronze.Might);Equal(50,bronze.Accuracy);Equal(400,bronze.Price);Equal("Sword",bronze.Category);
        Equal(66,jp.Items.Single(i=>i.Id==2).EffectValue);
        Console.WriteLine($"Japanese mirror differences: {jp.DifferentTableRecords}");
    });
    Test("English disc identity and item values",()=>{
        en=Inspector.Inspect(args[1]);Equal("English v2.3",en.Edition);Equal("C8CFA4E1",en.Crc);Equal(646,en.Items.Count);
        Equal(600,en.Items.Single(i=>i.Id==1).Price);Equal(100,en.Items.Single(i=>i.Id==506).Price);
        Console.WriteLine($"English mirror differences: {en.DifferentTableRecords}");
    });
    Test("Edition comparison preserves all distinct IDs",()=>{
        var changes=ItemComparison.Compare(jp!.Items,en!.Items);
        Console.WriteLine($"Compared 646 item IDs: {changes.Count} field differences");
        Equal(0,changes.Count);
    });
    Test("Character tables: both copies match on both discs",()=>{Equal(true,jp!.CharacterCopiesMatch);Equal(true,en!.CharacterCopiesMatch);Equal(35,en.Characters.Count);});
    Test("Character values match published tables",()=>{
        CharacterRecord C(string n)=>en!.Characters.Single(c=>c.Name==n);
        var ward=C("Ward");Equal(22,ward.Level);Equal("Sword:210,Spear:280,Medium shield:250",string.Join(",",ward.WeaponLevels.Where(w=>w.Value>=100).Select(w=>$"{w.Weapon}:{w.Value}")));
        var reese=C("Reese");Equal("40/30/18/30",$"{reese.HpGrowth}/{reese.StrengthGrowth}/{reese.DefenseGrowth}/{reese.AgilityGrowth}");
        Equal("Adept,Commander,Robust,Cavalry,Expert",string.Join(",",reese.Skills));
        var adel=C("Adel");var leon=C("Leon");
        Equal("1,-2,-1,1",$"{adel.HpBonus-leon.HpBonus},{adel.StrengthBonus-leon.StrengthBonus},{adel.DefenseBonus-leon.DefenseBonus},{adel.AgilityBonus-leon.AgilityBonus}");
        Equal("Throw 11",$"{leon.Learned.Single().Skill} {leon.Learned.Single().Level}");
        Equal("Aim 15,Pursuit 20",string.Join(",",C("Esteban").Learned.Select(l=>$"{l.Skill} {l.Level}")));
        Equal("Expert 6",$"{C("Izerna").Learned.Single().Skill} {C("Izerna").Learned.Single().Level}");
        Equal("Fortune 20",$"{C("Dean").Learned.Single().Skill} {C("Dean").Learned.Single().Level}");
        Equal("Sword:60,Shield:80",string.Join(",",reese.WeaponGrowths.Select(w=>$"{w.Weapon}:{w.Value}")));
    });
    Test("Class tables: both copies match on both discs",()=>{Equal(true,jp!.ClassCopiesMatch);Equal(true,en!.ClassCopiesMatch);});
    Test("Class base plus personal bonus equals published base stats",()=>{
        var expected=new Dictionary<string,(string Class,int Hp,int Str,int Def,int Agi,int Mind)>{
            ["Reese"]=("Lord (mounted)",26,5,5,6,0),["Ward"]=("Veteran Knight (mounted)",38,11,10,9,0),["Elbert"]=("Cavalier (mounted)",33,8,7,9,0),
            ["Christine"]=("Lady Knight (mounted)",25,3,4,8,0),["Adel"]=("Spear Knight (mounted)",29,4,3,8,0),["Leon"]=("Spear Knight (mounted)",28,6,4,7,0),
            ["Sherlock"]=("Horseman (mounted)",27,4,2,10,0),["Izerna"]=("Cleric",21,0,0,1,5),["Czene"]=("Thief (female)",20,1,0,12,0),
            ["Aegina"]=("Wind Mage",21,0,0,4,3),["Daoud"]=("Axe Soldier",36,11,9,8,0),["Enid"]=("Fencer",22,4,3,9,2)};
        foreach(var (name,e) in expected)
        {
            var c=en!.Characters.Single(x=>x.Name==name);var k=en.ClassOf(c)!;
            Equal($"{name} {e.Class} {e.Hp}/{e.Str}/{e.Def}/{e.Agi}/{e.Mind}",$"{name} {c.ClassName} {k.Hp+c.HpBonus}/{k.Strength+c.StrengthBonus}/{k.Defense+c.DefenseBonus}/{k.Agility+c.AgilityBonus}/{k.Mind+c.MindBonus}");
        }
    });
    Test("Class move, experience and skills match published class table",()=>{
        ClassRecord K(int id)=>en!.Classes.Single(c=>c.Id==id);
        Equal("4/7/18",$"{K(0x02).Move}/{K(0x01).Move}/{K(0x01).Experience}");
        Equal("4/7/22",$"{K(0x97).Move}/{K(0x96).Move}/{K(0x96).Experience}");
        Equal("4/7/15",$"{K(0xE0).Move}/{K(0xDF).Move}/{K(0xDF).Experience}");
        Equal("4/13/10",$"{K(0x57).Move}/{K(0x57).Experience}/{K(0x58).Experience}");
        Equal(true,K(0x01).Skills.Contains("Canto"));Equal(true,K(0x57).Skills.Contains("Locktouch") && K(0x57).Skills.Contains("Hide"));
    });
    Test("Movement type and mount agree for every class in both editions",()=>{
        ClassRecord K(int id)=>en!.Classes.Single(c=>c.Id==id);
        Equal("Mounted/On foot/Thief",$"{K(0x01).MountText}/{K(0x02).MountText}/{K(0x57).MovementTypeText}");
        foreach(var k in en!.Classes.Concat(jp!.Classes))
        {
            int expected=k.MovementType switch {0 or 1=>1,2=>2,_=>0};
            Equal($"{k.Name} {expected}",$"{k.Name} {k.Mount}");
        }
        Equal(true,Mechanics.Terrain.All(t=>t.Costs.Length==Mechanics.TerrainColumns.Length));
    });
    Test("Terrain tables: both copies match and the costs match the published list",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal(true,disc.TerrainCopiesMatch);Equal(TerrainData.Types,disc.MovementTypes.Count);
            int[] published=[0,1,2,3,4,5,11,6,12,7,8];
            string Row(int terrain)=>string.Join(" ",published.Select(t=>disc.MovementTypes[t].Cost(terrain)));
            Equal("7 6 1 4 4 4 3 2 3 3 2",Row(50));Equal("4 3 1 2 2 2 2 1 2 3 2",Row(4));Equal("6 4 1 4 × 3 2 2 3 3 2",Row(6));
            Equal("4 3 1 3 × 2 1 1 1 2 ×",Row(3));Equal("1 1 1 1 1 1 1 1 1 1 1",Row(24));
            Equal(true,disc.Classes.All(k=>k.MovementType<TerrainData.Types && disc.MovementTypes[k.MovementType].Mount==k.Mount));
        }
        Equal(true,Enumerable.Range(0,TerrainData.Types).All(t=>jp!.TerrainBytes[t].Span.SequenceEqual(en!.TerrainBytes[t].Span)));
    });
    Test("Terrain effect tables: both copies match, avoid matches the published list, flyers cannot stop on buildings",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal(true,disc.TerrainKindCopiesMatch);Equal(TerrainData.Terrains,disc.TerrainKinds.Count);
            Equal("2 3 5 9 8 10",string.Join(" ",new[]{0,1,2,4,6,50}.Select(t=>disc.TerrainKinds[t].Avoid)));
            Equal(-20,disc.TerrainKinds[51].Recovery);Equal(30,disc.TerrainKinds[45].Recovery);
            Equal("18,25,36,38,39,40,44,45,46,47,53,54,55",string.Join(",",disc.TerrainKinds.Where(k=>k.FlyersCannotStop).Select(k=>k.Id)));
            Equal(true,new[]{36,38,39,44,45,46,47,53,54,55}.All(t=>disc.MovementTypes[2].Costs[t]!=TerrainData.Blocked));
        }
        Equal(true,Enumerable.Range(0,TerrainData.Terrains).All(t=>jp!.TerrainKindBytes[t].Span.SequenceEqual(en!.TerrainKindBytes[t].Span)));
    });
    Test("Class shield sizes agree with shield caps, and a character's class can be changed",()=>{
        foreach(var k in en!.Classes)
        {
            int caps=(k.WeaponCaps.Any(c=>c.Weapon=="Small shield")?1:0)|(k.WeaponCaps.Any(c=>c.Weapon=="Medium shield")?2:0)|(k.WeaponCaps.Any(c=>c.Weapon=="Large shield")?4:0);
            Equal($"{k.Name} {caps}",$"{k.Name} {k.ShieldSizes}");
        }
        var s=new EditSession(en!);var reese=en!.Characters.Single(c=>c.Name=="Reese");Equal(0x01,reese.ClassId);
        s.Apply(EditSession.Characters,reese.Id,new Dictionary<string,int>{["class"]=0x02});
        var moved=s.EditedCharacter(reese.Id);Equal(0x02,moved.ClassId);Equal(en.Classes.Single(k=>k.Id==0x02).Name,moved.ClassName);
        Equal(1,s.EditedBytes(EditSession.Characters,reese.Id).Zip(en.UnitBytes[reese.Id].ToArray()).Count(p=>p.First!=p.Second));
        Throws<EditException>(()=>s.Apply(EditSession.Characters,reese.Id,new Dictionary<string,int>{["class"]=0xFF}));
        s.Apply(EditSession.Classes,0x01,new Dictionary<string,int>{["shieldLarge"]=1});Equal(true,(s.EditedClass(0x01).ShieldSizes&4)!=0);
    });
    Test("No character starts above a class weapon cap",()=>{
        foreach(var c in en!.Characters)
        {
            var caps=en.ClassOf(c)!.WeaponCaps.ToDictionary(w=>w.Weapon,w=>w.Value);
            foreach(var w in c.WeaponLevels) if(caps.TryGetValue(w.Weapon,out int cap) && w.Value>cap*10) throw new Exception($"{c.Name} {w.Weapon} {w.Value} over {cap}");
        }
    });
    Test("Class data is identical between editions",()=>Equal(0,ClassComparison.Compare(jp!.Classes,en!.Classes).Count));
    Test("Edit session: apply, undo, redo and dirty state",()=>{
        var s=new EditSession(en!);
        Equal(false,s.IsDirty);int might=s.OriginalValue(34,"might");
        Equal(true,s.Apply(34,new Dictionary<string,int>{["might"]=might+2,["price"]=999}));
        Equal(true,s.IsDirty);Equal(might+2,s.EditedItem(34).Might);Equal(999,s.EditedItem(34).Price);Equal(2,s.ChangeCount);
        Equal(false,s.Apply(34,new Dictionary<string,int>{["might"]=might+2}));
        s.Undo();Equal(false,s.IsDirty);Equal(might,s.EditedItem(34).Might);Equal(false,s.IsEdited(34));
        s.Redo();Equal(might+2,s.EditedItem(34).Might);
        Throws<EditException>(()=>s.Apply(34,new Dictionary<string,int>{["minRange"]=5,["maxRange"]=1}));
        Equal(might+2,s.EditedItem(34).Might);
        s.Revert(34);Equal(false,s.IsEdited(34));s.Undo();Equal(true,s.IsEdited(34));
        Equal(en!.Items.Count,s.EditedItems().Count);
        Equal(0,ItemComparison.Compare(en.Items.Where(i=>i.Id!=34),s.EditedItems().Where(i=>i.Id!=34)).Count);
    });
    Test("Edits change only the edited field bytes on both editions",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            var s=new EditSession(disc);s.Apply(0x22,new Dictionary<string,int>{["accuracy"]=99});
            var a=disc.ItemBytes[0x22].ToArray();var b=s.EditedBytes(0x22);
            var f=ItemEncoder.Field("accuracy");
            for(int bit=0;bit<448;bit++) if(((a[bit/8]^b[bit/8])>>(bit%8)&1)!=0 && (bit<f.Offset*8+f.Bit || bit>=f.Offset*8+f.Bit+f.Width)) throw new Exception($"bit {bit} changed");
            Equal(99,s.EditedItem(0x22).Accuracy);
        }
    });
    Test("Project files round trip and refuse the wrong disc",()=>{
        var disc=en!;var s=new EditSession(disc);s.Apply(1,new Dictionary<string,int>{["price"]=1234,["durability"]=0});s.Apply(506,new Dictionary<string,int>{["might"]=9});
        string json=s.ToJson("0.4.0");
        var back=EditSession.FromJson(disc,json);
        Equal(3,back.ChangeCount);Equal(1234,back.EditedItem(1).Price);Equal(9,back.EditedItem(506).Might);Equal(false,back.IsDirty);
        Equal(false,back.CanUndo);
        try{EditSession.FromJson(jp!,json);throw new Exception("loaded on wrong disc");}catch(EditException e){Equal(true,e.Message.Contains("English v2.3"));}
        Throws<EditException>(()=>EditSession.FromJson(disc,"not json"));
        Throws<EditException>(()=>EditSession.FromJson(disc,json.Replace("\"original\": "+disc.Items.Single(i=>i.Id==1).Price,"\"original\": 1")));
        Throws<EditException>(()=>EditSession.FromJson(disc,json.Replace("\"value\": 9","\"value\": 99")));
        var path=Path.Combine(Path.GetTempPath(),$"bw-test-{Guid.NewGuid():N}.bwproj");
        try{s.Save(path,"0.4.0");Equal(false,s.IsDirty);Equal(3,EditSession.Load(disc,path).ChangeCount);}
        finally{File.Delete(path);}
    });
foreach(var exportDisc in new[]{jp!,en!})
    Test($"Real export: edited {exportDisc.Edition} opens with exactly the project's values",()=>{
        var disc=exportDisc;
        var drive=new DriveInfo(Path.GetPathRoot(Path.GetTempPath())!);
        if(drive.AvailableFreeSpace<disc.Size*2){Console.WriteLine("  skipped: not enough free space");return;}
        var s=new EditSession(disc);
        s.Apply(34,new Dictionary<string,int>{["might"]=s.OriginalValue(34,"might")+2,["price"]=777});
        s.Apply(506,new Dictionary<string,int>{["durability"]=0,["accuracy"]=99});
        string dst=Path.Combine(Path.GetTempPath(),$"bw-real-export-{Guid.NewGuid():N}.iso");
        try
        {
            s.Apply(new FieldEdit[]{new(EditSession.Characters,0x54,"level",30),new(EditSession.Characters,0x54,"strengthBonus",-3),new(EditSession.Characters,0x54,"spearLevel",350),
                new(EditSession.Growths,0x54,"hpGrowth",55),new(EditSession.Growths,0x54,"axeGrowth",7),new(EditSession.Classes,0x1E,"hp",31),new(EditSession.Classes,0x1E,"move",8),new(EditSession.Classes,0x1E,"axeCap",45),
                new(EditSession.Characters,0x54,"skill15",1),new(EditSession.Characters,0x54,"item5Uses",77),new(EditSession.Growths,0x54,"bracket",1),new(EditSession.Classes,0x1E,"skill19",1)});
            int newSlot=s.FreeItemSlots()[0];s.CreateItem(newSlot,0x22,"Test Blade");s.Apply(newSlot,new Dictionary<string,int>{["might"]=33});
            s.Apply(new FieldEdit[]{new(EditSession.Rules,1,"shieldParry",1),new(EditSession.Rules,2,"battleExperience",50),new(EditSession.Rules,4,"strengthLimit",12),
                new(EditSession.Rules,9,"shieldBlock",2),new(EditSession.Rules,10,"shieldGuard",2),new(EditSession.Rules,15,"vantageTraining",2)});
            s.Apply(EditSession.Characters,0x54,new Dictionary<string,int>{["item5"]=newSlot});
            s.Apply(new FieldEdit[]{new(EditSession.Terrain,5,"cost4",15),new(EditSession.Terrain,5,"cost15",10),new(EditSession.Classes,0x1E,"movementType",2)});
            s.Apply(new FieldEdit[]{new(EditSession.TerrainKinds,36,"flyersCannotStop",0),new(EditSession.TerrainKinds,51,"recovery",10),new(EditSession.TerrainKinds,0,"avoid",7),
                new(EditSession.Rules,20,"expLevelGap0",-3),new(EditSession.Rules,36,"expHighest",60),new(EditSession.Rules,40,"iaidoBase",55),new(EditSession.Rules,44,"axebaneCap",70)});
            s.Apply(new FieldEdit[]{new(EditSession.Partners,0x54,"partner1Bond",400),new(EditSession.Partners,0x55,"partner2",0x58),new(EditSession.Partners,0x55,"partner2Bond",70),
                new(EditSession.Bags,0x53,"bagItem2",0x1CD),new(EditSession.Bags,0x53,"bagItem2Uses",3),
                new(EditSession.Mercenaries,0x90,"mercenaryBase",2000),new(EditSession.Mercenaries,0x95,"mercenaryPerLevel",300),
                new(EditSession.ArmyUnits,ArmyData.Key(380,0),"level",20),new(EditSession.ArmyUnits,ArmyData.Key(380,0),"item1",0x1CD),new(EditSession.ArmyUnits,ArmyData.Key(380,1),"item1Drop",1)});
            Equal(11,disc.StablePriceBytes.Count);Equal(1000,s.Value(EditSession.StablePrices,0x205,"stablePrice"));
            s.Apply(new FieldEdit[]{new(EditSession.StablePrices,0x205,"stablePrice",1234),new(EditSession.StablePrices,0x20E,"stablePrice",30000),
                new(EditSession.FurniturePrices,13,"furniturePrice",777),new(EditSession.FurniturePrices,8,"furniturePrice",50000)});
            Throws<EditException>(()=>s.Apply(new FieldEdit[]{new(EditSession.StablePrices,0x205,"stablePrice",0)}));
            Equal(1234,EditSession.FromJson(disc,s.ToJson()).Value(EditSession.StablePrices,0x205,"stablePrice"));
            Equal(true,EditSession.FromJson(disc,s.ToJson()).EditedMovementType(5).Costs[4]==15);
            var patches=Exporter.PatchesFor(s,disc);Equal(true,patches.Count>0);
            var result=Exporter.Export(disc.Path,disc.Sha256,dst,patches);
            Console.WriteLine($"  exported {result.Size:N0} bytes, {result.ChangedBytes} bytes changed");
            var back=Inspector.Inspect(dst);
            Equal(true,back.IsModified);Equal($"Modified {disc.Edition}",back.Edition);Equal(result.Sha256,back.Sha256);
            Equal(0,back.DifferentTableRecords);
            Equal(0,ItemComparison.Compare(s.EditedItems(),back.Items).Count);
            Equal(0,RecordComparison.Compare(s.EditedCharacters(),back.Characters).Count);
            Equal(0,ClassComparison.Compare(s.EditedClasses(),back.Classes).Count);
            var ward=back.Characters.Single(c=>c.Id==0x54);var vk=back.Classes.Single(c=>c.Id==0x1E);
            Equal("30/-3/350/55/Axe:70",$"{ward.Level}/{ward.StrengthBonus}/{ward.WeaponLevels.Single(w=>w.Weapon=="Spear").Value}/{ward.HpGrowth}/{string.Join(",",ward.WeaponGrowths.Where(w=>w.Weapon=="Axe").Select(w=>$"{w.Weapon}:{w.Value}"))}");
            Equal("31/8/45",$"{vk.Hp}/{vk.Move}/{vk.WeaponCaps.Single(c=>c.Weapon=="Axe").Value}");
            Equal(true,ward.Skills.Contains("Astra"));Equal(true,ward.Items.Any(i=>i.ItemId==newSlot && i.Durability==77));Equal("None",ward.BracketText);
            var created=back.Items.Single(i=>i.Id==newSlot);Equal(33,created.Might);
            Equal(true,back.ItemBytes[newSlot].Span.SequenceEqual(s.EditedBytes(newSlot)));
            var expected=s.EditedBytes(0x22);FieldCodec.Write(expected,ItemEncoder.Field("might"),33);
            Equal(true,back.ItemBytes[newSlot].Span.SequenceEqual(expected));
            Equal(true,vk.Skills.Contains("Fortune"));
            Equal(0,RecordComparison.Compare(disc.Characters.Where(c=>c.Id!=0x54),back.Characters.Where(c=>c.Id!=0x54)).Count);
            Equal(0,ClassComparison.Compare(disc.Classes.Where(c=>c.Id!=0x1E),back.Classes.Where(c=>c.Id!=0x1E)).Count);
            Equal(true,back.CharacterCopiesMatch);Equal(true,back.ClassCopiesMatch);Equal(true,back.TerrainCopiesMatch);
            Equal("1.5/1/Flying",$"{back.MovementTypes[5].Cost(4)}/{back.MovementTypes[5].Cost(15)}/{vk.MovementTypeText}");
            Equal(true,Enumerable.Range(0,TerrainData.Types).Where(t=>t!=5).All(t=>back.TerrainBytes[t].Span.SequenceEqual(disc.TerrainBytes[t].Span)));
            Equal(true,back.TerrainKindCopiesMatch);Equal("False/10/7/True",$"{back.TerrainKinds[36].FlyersCannotStop}/{back.TerrainKinds[51].Recovery}/{back.TerrainKinds[0].Avoid}/{back.TerrainKinds[38].FlyersCannotStop}");
            Equal(0x81,back.TerrainKinds[36].Flags);
            Equal(true,Enumerable.Range(0,TerrainData.Terrains).Where(t=>t is not (0 or 36 or 51)).All(t=>back.TerrainKindBytes[t].Span.SequenceEqual(disc.TerrainKindBytes[t].Span)));
            Equal(-3,GameRules.ById(20).Decode(back.RuleWords[20].Span));Equal(60,GameRules.ById(36).Decode(back.RuleWords[36].Span));Equal(55,GameRules.ById(40).Decode(back.RuleWords[40].Span));Equal(70,GameRules.ById(44).Decode(back.RuleWords[44].Span));
            Equal(1,GameRules.ById(1).Decode(back.RuleWords[1].Span));Equal(50,GameRules.ById(2).Decode(back.RuleWords[2].Span));Equal(12,GameRules.ById(4).Decode(back.RuleWords[4].Span));
            Equal(2,GameRules.ById(9).Decode(back.RuleWords[9].Span));Equal(2,GameRules.ById(10).Decode(back.RuleWords[10].Span));Equal(2,GameRules.ById(15).Decode(back.RuleWords[15].Span));
            Equal(false,back.ExecutableSha256==disc.ExecutableSha256);
            Equal(1234,BinaryPrimitives.ReadInt32LittleEndian(back.StablePriceBytes[0x205].Span));Equal(30000,BinaryPrimitives.ReadInt32LittleEndian(back.StablePriceBytes[0x20E].Span));
            Equal(2500,BinaryPrimitives.ReadInt32LittleEndian(back.StablePriceBytes[0x206].Span));
            Equal("777/50000/23000",$"{BinaryPrimitives.ReadInt32LittleEndian(back.FurniturePriceBytes[13].Span)}/{BinaryPrimitives.ReadInt32LittleEndian(back.FurniturePriceBytes[8].Span)}/{BinaryPrimitives.ReadInt32LittleEndian(back.FurniturePriceBytes[1].Span)}");
            Equal(true,back.PartnerCopiesMatch);Equal(16,back.PartnerBytes.Count);
            var reread=new EditSession(back);
            Equal(400,reread.Value(EditSession.Partners,0x54,"partner1Bond"));Equal(0x53,reread.Value(EditSession.Partners,0x54,"partner1"));
            Equal(0x58,reread.Value(EditSession.Partners,0x55,"partner2"));Equal(70,reread.Value(EditSession.Partners,0x55,"partner2Bond"));
            Equal(true,back.PartnerBytes.Where(p=>p.Key is not (0x54 or 0x55)).All(p=>p.Value.Span.SequenceEqual(disc.PartnerBytes[p.Key].Span)));
            Equal(true,back.BagBytes[0x53].Span.SequenceEqual(s.EditedBytes(EditSession.Bags,0x53)));
            Equal(0x1CD,reread.Value(EditSession.Bags,0x53,"bagItem2"));Equal(3,reread.Value(EditSession.Bags,0x53,"bagItem2Uses"));Equal(0x21,reread.Value(EditSession.Bags,0x53,"bagItem1"));
            Equal(true,back.BagBytes.Where(p=>p.Key!=0x53).All(p=>p.Value.Span.SequenceEqual(disc.BagBytes[p.Key].Span)));
            Equal(2000,reread.Value(EditSession.Mercenaries,0x90,"mercenaryBase"));Equal(300,reread.Value(EditSession.Mercenaries,0x95,"mercenaryPerLevel"));
            Equal(true,back.MercenaryBytes.Where(p=>p.Key is not (0x90 or 0x95)).All(p=>p.Value.Span.SequenceEqual(disc.MercenaryBytes[p.Key].Span)));
            Equal(20,reread.Value(EditSession.ArmyUnits,ArmyData.Key(380,0),"level"));Equal(0x1CD,reread.Value(EditSession.ArmyUnits,ArmyData.Key(380,0),"item1"));
            Equal(1,reread.Value(EditSession.ArmyUnits,ArmyData.Key(380,1),"item1Drop"));
            Equal(true,back.ArmyUnitBytes.Where(p=>p.Key is not (380000 or 380001)).All(p=>p.Value.Span.SequenceEqual(disc.ArmyUnitBytes[p.Key].Span)));
            Equal(disc.Armies.Count,back.Armies.Count);
            using(var a=File.OpenRead(disc.Path))using(var b=File.OpenRead(dst))
            {
                var exeA=new byte[disc.ExecutableFile!.Length];a.Position=disc.ExecutableFile.Offset;a.ReadExactly(exeA);
                var exeB=new byte[exeA.Length];b.Position=back.ExecutableFile!.Offset;b.ReadExactly(exeB);
                Equal(4+1+20+34+1+2+1+2+2,Enumerable.Range(0,exeA.Length/4).Count(i=>BitConverter.ToUInt32(exeA,i*4)!=BitConverter.ToUInt32(exeB,i*4)));
                Equal(true,GameRules.Normalize(exeB).AsSpan().SequenceEqual(exeA));
            }
            var original=Inspector.Inspect(disc.Path);Equal(disc.Sha256,original.Sha256);
        }
        finally{File.Delete(dst);}
    });
    if(args.Length>=3)
        Test("A modified disc built on English v2.3 is recognized",()=>{
            var redux=Inspector.Inspect(args[2]);
            Equal(true,redux.IsModified);Equal("Modified English v2.3",redux.Edition);
            Console.WriteLine($"  {Path.GetFileName(args[2])}: {ItemComparison.Compare(en!.Items,redux.Items).Select(d=>d.Id).Distinct().Count()} items, {RecordComparison.Compare(en.Characters,redux.Characters).Select(d=>d.Id).Distinct().Count()} characters, {ClassComparison.Compare(en.Classes,redux.Classes).Select(d=>d.Id).Distinct().Count()} classes differ from English v2.3; item copies differing: {redux.DifferentTableRecords}");
        });
    Test("Character and class edits: undo spans tables, projects round trip, both editions",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            var s=new EditSession(disc);
            Equal(true,s.Apply(new FieldEdit[]{new(EditSession.Characters,0x53,"level",5),new(EditSession.Growths,0x53,"hpGrowth",90),new(EditSession.Classes,0x01,"move",9)}));
            var reese=s.EditedCharacter(0x53);Equal("5/90",$"{reese.Level}/{reese.HpGrowth}");Equal(9,s.EditedClass(0x01).Move);
            Equal(3,s.ChangeCount);s.Undo();Equal(0,s.ChangeCount);Equal(1,s.EditedCharacter(0x53).Level);s.Redo();
            var back=EditSession.FromJson(disc,s.ToJson());Equal(3,back.ChangeCount);Equal(90,back.EditedCharacter(0x53).HpGrowth);
            Throws<EditException>(()=>s.Apply(EditSession.Classes,0x01,new Dictionary<string,int>{["move"]=16}));
            Throws<EditException>(()=>s.Apply(EditSession.Characters,0x999,new Dictionary<string,int>{["level"]=2}));
            s.Revert((EditSession.Characters,0x53),(EditSession.Growths,0x53));Equal(1,s.ChangeCount);
            Throws<EditException>(()=>s.Apply(EditSession.Characters,0x53,new Dictionary<string,int>{["item1"]=0x999}));
            Equal(true,s.Apply(EditSession.Characters,0x53,new Dictionary<string,int>{["item4"]=0x22,["item4Uses"]=50,["skill15"]=1}));
            var r2=s.EditedCharacter(0x53);Equal(true,r2.Skills.Contains("Astra"));Equal(true,r2.Items.Any(i=>i.ItemId==0x22 && i.Durability==50));
        }
    });
    Test("New items: full copy into a free slot, rename, undo, project round trip",()=>{
        var disc=en!;var s=new EditSession(disc);
        var free=s.FreeItemSlots();Equal(true,free.Count>100);
        var carried=disc.Characters.SelectMany(c=>c.Items).Select(i=>i.ItemId).ToHashSet();
        Equal(false,free.Any(carried.Contains));Equal(true,free.All(id=>disc.Items.Single(i=>i.Id==id).IsUnused));
        int slot=free[0],template=0x22;
        s.CreateItem(slot,template,"Flame Lance");
        Equal(true,s.EditedBytes(slot).AsSpan().SequenceEqual(disc.ItemBytes[template].Span));
        var item=s.EditedItem(slot);Equal("Flame Lance",item.DisplayName);Equal(false,item.IsUnused);
        Equal(disc.Items.Single(i=>i.Id==template).Might,item.Might);
        Equal(false,s.FreeItemSlots().Contains(slot));
        s.Apply(slot,new Dictionary<string,int>{["might"]=20,["type"]=4});
        Equal("20/Spear",$"{s.EditedItem(slot).Might}/{s.EditedItem(slot).Category}");
        s.Rename(EditSession.Items,slot,"Flame Spear");Equal("Flame Spear",s.EditedItem(slot).DisplayName);
        s.Undo();Equal("Flame Lance",s.EditedItem(slot).DisplayName);
        s.Undo();Equal(item.Might,s.EditedItem(slot).Might);
        s.Undo();Equal(true,s.EditedItem(slot).IsUnused);Equal(0,s.ChangeCount);
        s.Redo();s.Redo();s.Redo();Equal("Flame Spear",s.EditedItem(slot).DisplayName);Equal(20,s.EditedItem(slot).Might);
        string json=s.ToJson();Equal(true,json.Contains("\"formatVersion\": 2"));
        var back=EditSession.FromJson(disc,json);
        Equal(true,back.EditedBytes(slot).AsSpan().SequenceEqual(s.EditedBytes(slot)));Equal("Flame Spear",back.EditedItem(slot).DisplayName);
        Throws<EditException>(()=>EditSession.FromJson(disc,json.Replace($"\"from\": {template}","\"from\": 99999")));
        Throws<EditException>(()=>s.CreateItem(slot,template,"Again"));
        Throws<EditException>(()=>s.CreateItem(free[1],template,"  "));
        Throws<EditException>(()=>s.CreateItem(0x22,template,"Not free"));
        s.Revert(EditSession.Items,slot);Equal(true,s.EditedItem(slot).IsUnused);Equal(true,s.FreeItemSlots().Contains(slot));
        var chain=new EditSession(disc);chain.Apply(0x22,new Dictionary<string,int>{["might"]=25});
        int a1=chain.FreeItemSlots()[0];chain.CreateItem(a1,0x22,"First");Equal(25,chain.EditedItem(a1).Might);
        int a2=chain.FreeItemSlots()[0];chain.CreateItem(a2,a1,"Second");Equal(25,chain.EditedItem(a2).Might);
        Equal(true,chain.EditedBytes(a2).AsSpan().SequenceEqual(chain.EditedBytes(0x22)));
        var chainBack=EditSession.FromJson(disc,chain.ToJson());Equal(true,chainBack.EditedBytes(a2).AsSpan().SequenceEqual(chain.EditedBytes(a2)));
        var plain=new EditSession(disc);plain.Apply(1,new Dictionary<string,int>{["price"]=5});Equal(true,plain.ToJson().Contains("\"formatVersion\": 1"));
    });
    Test("Item bonuses, resistances and effects read as the items describe them",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            int V(int id,string key)=>ItemEncoder.Read(disc.ItemBytes[id].Span,ItemEncoder.Field(key));
            Equal("5/2/-2/-10/10/12/30/-8",$"{V(429,"hitBonus")}/{V(442,"strengthBonus")}/{V(442,"agilityBonus")}/{V(446,"avoidBonus")}/{V(446,"hitBonus")}/{V(456,"criticalEvade")}/{V(456,"fireResist")}/{V(456,"windResist")}");
            Equal("5/18/18/18/18/0/3/1",$"{V(340,"fireResist")}/{V(388,"fireResist")}/{V(388,"thunderResist")}/{V(388,"windResist")}/{V(388,"holyResist")}/{V(388,"darkResist")}/{V(349,"mindBonus")}/{V(432,"defenseBonus")}");
            Equal("108/33/131/12/0/0",$"{V(0x0D,"effectKind")}/{V(0x0D,"effectAmount")}/{V(0x7B,"effectKind")}/{V(0x7B,"effectAmount")}/{V(1,"effectKind")}/{V(1,"effectAmount")}");
            Equal("1/1/1/0/1/1/1",$"{V(335,"effect1")}/{V(5,"effect2")}/{V(5,"effect10")}/{V(1,"effect2")}/{V(0x2A,"effect12")}/{V(0xF1,"effect20")}/{V(0xF1,"effect21")}");
        }
    });
    Test("Event scripts: every event on both discs reads, parses, writes back and compiles from its text to the same bytes",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var library=new EventLibrary(new GameArchives(iso));
            Equal(3983,library.Numbers.Count);Equal(1,library.Numbers[0]);Equal(3986,library.Numbers[^1]);
            int commands=0;
            foreach(int n in library.Numbers)
            {
                var bytes=library.Bytes(n);var nodes=ScriptParser.Parse(bytes);
                Equal(1,nodes.Count);Equal(ScriptParser.Event,nodes[0].Command);commands+=nodes[0].Args[0].Count;
                // The writer gives back the original bytes for every event.
                if(!ScriptWriter.Write(nodes).AsSpan().SequenceEqual(bytes))throw new Exception($"Event {n} does not write back to its original bytes.");
                // The text the Events page shows compiles back to the same bytes.
                string shown=ScriptText.Format(nodes,library.CommandNames);
                byte[] compiled;
                try{compiled=ScriptWriter.Write(ScriptCompiler.Compile(shown,library.CommandNames));}
                catch(EditException ex){throw new Exception($"Event {n} text does not compile: {ex.Message}");}
                if(!compiled.AsSpan().SequenceEqual(bytes))throw new Exception($"Event {n} text compiles to different bytes.");
            }
            Equal(true,commands>20000);
            string text=library.Text(172);
            Equal(true,text.StartsWith("event ("));Equal(true,text.Contains("517"));
            var names=library.CommandNames!;
            Equal(0x4EF,names.Count);Equal("SetEventFlag",names[0x77]);Equal("GUnitCalcMercenaryCost",names[0x1C0]);Equal("LaunchMapEvent",names[0x41F]);
            Equal(false,System.Text.RegularExpressions.Regex.IsMatch(text,@"\bcommand\d"));
        }
    });
    Test("Script numbers: listed, edited through a project and written to every copy",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var archives=new GameArchives(iso);var library=new EventLibrary(archives);
            // Quest 6's every-turn event brings reinforcements on turns 5 to 8.
            var bytes=library.Bytes(3276);
            var numbers=ScriptValues.Numbers(3276,ScriptParser.Parse(bytes),library.CommandNames);
            var turn=numbers.First(n=>n.Command=="==" && n.Value==5);
            Equal(true,ScriptValues.Apply(bytes,new Dictionary<int,int>()).AsSpan().SequenceEqual(bytes));
            var edited=ScriptValues.Apply(bytes,new Dictionary<int,int>{[turn.Index]=3});
            Equal(3,ScriptValues.Numbers(3276,ScriptParser.Parse(edited))[turn.Index].Value);
            Throws<EditException>(()=>ScriptValues.Apply(bytes,new Dictionary<int,int>{[numbers.Count]=1}));
            // Through a session and a project file, then into the disc patches for every copy.
            var session=new EditSession(disc);
            int key=ScriptValues.Key(3276,turn.Index);
            Equal(5,session.Value(EditSession.ScriptValues,key,"value"));
            Equal(true,session.Apply(EditSession.ScriptValues,key,new Dictionary<string,int>{["value"]=3}));
            var reopened=EditSession.FromJson(disc,session.ToJson());
            Equal(3,reopened.Value(EditSession.ScriptValues,key,"value"));
            var patches=Exporter.PatchesFor(reopened,disc);
            var copies=EventCopies.Locate(archives,3276);Equal(1,copies.Count);
            var top=archives.Top(copies[0].Top);long at=archives.TopOffset(copies[0].Top);
            foreach(var p in patches){Equal(true,p.Offset>=at && p.Offset+p.Bytes.Length<=at+top.Length);p.Bytes.CopyTo(top,(int)(p.Offset-at));}
            Equal(true,EventCopies.Extract(top,3276,copies[0]).AsSpan().SequenceEqual(edited));
            // A value that makes the script larger than its space now moves the files after it instead of being refused.
            var big=ScriptValues.Apply(bytes,numbers.ToDictionary(n=>n.Index,n=>unchecked(n.Value*0x3D2E1A9 ^ 0x5A5A5A5)));
            EventCopies.CheckFits(archives,3276,big,ArchiveEditing.FixedTops(archives,disc.Profile));
            // Event 602 (stable prices) has three copies; a number there goes to all of them.
            Equal(3,EventCopies.Locate(archives,602).Count);
        }
    });
    Test("Shops: every shop and its items on both discs, map shops named by their battle, and items changed, removed and added",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var library=new EventLibrary(new GameArchives(iso));
            var shops=Shops.Read(library);
            Equal(true,shops.Count>=20);
            // The base's sword shop (event 549) and shield shop (event 546, three ways of selling).
            var shields=shops.Single(s=>s.Event==546);Equal(0,shields.Battle);
            Equal("347,369,382",string.Join(",",shields.Items.Where(i=>i.Sale==ShopSale.Always).Select(i=>i.Item)));
            Equal(true,shields.Items.Any(i=>i.Sale==ShopSale.Stock && i.Item==331 && i.Extra==174));
            Equal(true,shields.Items.Any(i=>i.Sale==ShopSale.Special && i.Item==335 && i.Extra==3523));
            // Map shops are reached from their spot: battle 112's weapon shop is event 3405.
            var weapons=shops.Single(s=>s.Event==3405);Equal((112,"Weapon shop"),(weapons.Battle,weapons.Spot));
            Equal(true,shops.Count(s=>s.Battle>0)>=8);
            // Every item's number and line are where the editors expect them.
            foreach(var shop in shops)
            {
                var bytes=library.Bytes(shop.Event);var numbers=ScriptValues.Numbers(shop.Event,ScriptParser.Parse(bytes));
                foreach(var item in shop.Items)Equal(item.Item,numbers[item.NumberIndex].Value);
                var fewer=Shops.Items(ScriptParser.Parse(ScriptLines.Remove(bytes,shop.Items[0].Path)));
                Equal(shop.Items.Count-1,fewer.Count);
            }
            // Change, add and remove in the shield shop's script.
            var script=library.Bytes(546);
            var changed=ScriptValues.Apply(script,new Dictionary<int,int>{[shields.Items[0].NumberIndex]=0x0A0});
            Equal(0x0A0,Shops.Items(ScriptParser.Parse(changed))[0].Item);
            var added=ScriptLines.InsertAfter(script,shields.Items[^1].Path,ScriptLines.Command(Shops.AddItem,"160"));
            var after=Shops.Items(ScriptParser.Parse(added));
            Equal(shields.Items.Count+1,after.Count);Equal((160,ShopSale.Always),(after[^1].Item,after[^1].Sale));
        }
    });
    Test("Script text: an edited text compiles to the changed script, and mistakes are reported with their line",()=>{
        using var stream=new FileStream(en!.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
        using var iso=new IsoImage(stream);
        var library=new EventLibrary(new GameArchives(iso));var names=library.CommandNames;
        var bytes=library.Bytes(3276);
        string text=ScriptText.Format(ScriptParser.Parse(bytes),names);
        // Change a turn and add a line in the text.
        string changed=text.Replace("if (v0 == 5)","if (v0 == 3)").Replace("        run event 3277","        run event 3277\r\n        AppearUnit(5, \"ud370\", 1, 5, 21, 5, 19, 0)");
        var compiled=ScriptWriter.Write(ScriptCompiler.Compile(changed,names));
        Equal(true,ScriptText.Format(ScriptParser.Parse(compiled),names)==changed);
        var expected=ScriptLines.InsertAfter(ScriptValues.Apply(bytes,new Dictionary<int,int>{[ScriptValues.Numbers(3276,ScriptParser.Parse(bytes)).First(n=>n.Command=="==" && n.Value==5).Index]=3}),
            ScriptLines.Lines(ScriptParser.Parse(bytes),names).First(l=>l.Text=="run event 3277").Path,ScriptLines.Command(0x32B,"5, \"ud370\", 1, 5, 21, 5, 19, 0"));
        Equal(true,compiled.AsSpan().SequenceEqual(expected));
        // Texts keep quotes and line breaks; minus on a plain number reads back differently from a negative number.
        var quoted=new ScriptNode{Command=ScriptParser.Event,Locals=0,Args=[[new ScriptNode{Command=0x77,Args=[[new ScriptNode{Command=ScriptParser.TextValue,Text="a \"b\"\r\nc\\d"}],[new ScriptNode{Command=0x15,Args=[[new ScriptNode{Command=1,Value=5}]]}],[new ScriptNode{Command=1,Value=-5}]]}]]};
        var quotedBytes=ScriptWriter.Write([quoted]);
        Equal(true,ScriptWriter.Write(ScriptCompiler.Compile(ScriptText.Format(ScriptParser.Parse(quotedBytes),names),names)).AsSpan().SequenceEqual(quotedBytes));
        // Mistakes name their line.
        string Problem(string t){try{ScriptCompiler.Compile(t,names);return "";}catch(EditException e){return e.Message;}}
        Equal(true,Problem("event (0 local values)\r\n   SetEventFlag(1, 1)").StartsWith("Line 2:"));
        Equal(true,Problem("event (0 local values)\r\n    NoSuchCommand(1)").Contains("no command named NoSuchCommand"));
        Equal(true,Problem("event (0 local values)\r\n    SetEventFlag(\"open, 1)").StartsWith("Line 2:"));
        Equal(true,Problem("event (0 local values)\r\n    SetEventFlag(1, 1)\r\n        wait 5 frames").StartsWith("Line 3:"));
        Equal(true,Problem("event (0 local values)\r\n    if (v0 = 1)\r\n        break").StartsWith("Line 2:"));
        Equal("",Problem("event (0 local values)\r\n    SetEventFlag(1, 1)\r\n    wait 5 frames"));
    });
    Test("Script lines: listed, duplicated, removed, moved and added, kept in a project and written to the disc",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var archives=new GameArchives(iso);var library=new EventLibrary(archives);
            var bytes=library.Bytes(3276);
            var lines=ScriptLines.Lines(ScriptParser.Parse(bytes),library.CommandNames);
            Equal("SetEventFlag(1311, 1)",lines[0].Text);Equal(0,lines[0].Depth);
            var appear=lines.First(l=>l.Text.StartsWith("AppearUnit(4, \"ud370\""));Equal(1,appear.Depth);
            // Duplicate, then remove the copy: back to the disc's bytes.
            var doubled=ScriptLines.Duplicate(bytes,appear.Path);
            Equal(2,ScriptLines.Lines(ScriptParser.Parse(doubled),library.CommandNames).Count(l=>l.Text==appear.Text));
            var copyPath=appear.Path.Take(appear.Path.Count-1).Append((appear.Path[^1].Arg,appear.Path[^1].Item+1)).ToList();
            Equal(true,ScriptLines.Remove(doubled,copyPath).AsSpan().SequenceEqual(bytes));
            Equal(true,ScriptLines.Move(ScriptLines.Move(bytes,appear.Path,1),copyPath,-1).AsSpan().SequenceEqual(bytes));
            Throws<EditException>(()=>ScriptLines.Move(bytes,lines[0].Path,-1));
            // A new command typed as text.
            var added=ScriptLines.InsertAfter(bytes,appear.Path,ScriptLines.Command(0x32B,"8, \"ud370\", 1, 2, -1, 2, 1, 0"));
            Equal(true,ScriptText.Format(ScriptParser.Parse(added),library.CommandNames).Contains("AppearUnit(8, \"ud370\", 1, 2, -1, 2, 1, 0)"));
            Equal(true,ScriptText.Format([ScriptLines.Command(0x2C,"v1, 0x1F, -3")],null).Contains("v1"));
            Throws<EditException>(()=>ScriptLines.Command(1,"\"open"));Throws<EditException>(()=>ScriptLines.Command(1,"4,"));Throws<EditException>(()=>ScriptLines.Command(1,"four"));
            // In a session: earlier number edits become part of the new script; undo, project and export.
            var session=new EditSession(disc);
            var turn=ScriptValues.Numbers(3276,ScriptParser.Parse(bytes)).First(n=>n.Command=="==" && n.Value==5);
            session.Apply(EditSession.ScriptValues,ScriptValues.Key(3276,turn.Index),new Dictionary<string,int>{["value"]=4});
            var withTurn=ScriptLines.Duplicate(session.EventScript(3276)!,appear.Path);
            Equal(true,session.SetScript(3276,withTurn));
            Equal(false,session.IsEdited(EditSession.ScriptValues,ScriptValues.Key(3276,turn.Index)));Equal(true,session.HasScript(3276));
            Equal(4,ScriptValues.Numbers(3276,ScriptParser.Parse(session.EventScript(3276)!))[turn.Index].Value);
            session.Undo();Equal(false,session.HasScript(3276));Equal(4,session.Value(EditSession.ScriptValues,ScriptValues.Key(3276,turn.Index),"value"));
            session.Redo();Equal(true,session.HasScript(3276));
            var reopened=EditSession.FromJson(disc,session.ToJson());
            Equal(true,reopened.EventScript(3276)!.AsSpan().SequenceEqual(withTurn));
            Equal(true,session.ToJson().Contains("\"formatVersion\": 3"));
            var patches=Exporter.PatchesFor(reopened,disc);
            var copy=EventCopies.Locate(archives,3276).Single();
            var top=archives.ReadData3(archives.Row(copy.Top).Sector*2048L,(int)((archives.Row(copy.Top).Size+2047)/2048*2048));long at=archives.TopOffset(copy.Top);
            foreach(var p in patches.Where(p=>p.Offset>=at && p.Offset<at+top.Length))p.Bytes.CopyTo(top,(int)(p.Offset-at));
            uint newSize=patches.FirstOrDefault(p=>p.Offset==archives.RowOffset(copy.Top)+8) is { } sizePatch?BitConverter.ToUInt32(sizePatch.Bytes,0):archives.Row(copy.Top).Size;
            Equal(true,EventCopies.Extract(top[..(int)newSize],3276,copy).AsSpan().SequenceEqual(withTurn));
            // Restoring the event removes the change; the shop price events are left to their own pages.
            session.Revert(EditSession.Scripts,3276);Equal(false,session.HasScript(3276));
            Throws<EditException>(()=>session.SetScript(602,ScriptLines.Duplicate(library.Bytes(602),ScriptLines.Lines(ScriptParser.Parse(library.Bytes(602))).First().Path)));
            // The argument counts seen on the disc: AppearUnit takes 8.
            var counts=ScriptLines.ArgumentCounts(library.Numbers.Take(400).Select(library.Bytes).Append(bytes));
            Equal("8",string.Join(",",counts[0x32B]));
        }
    });
    Test("Archives: every archive holding events lays out again to the same bytes, grows in place or moves, and keeps every other file",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var archives=new GameArchives(iso);var library=new EventLibrary(archives);
            // The layout rule holds for every archive with events (and ev_all.ar inside top 14): laying one out again with its own bytes changes nothing.
            var tops=library.Numbers.Select(library.TopOf).Append(14).Distinct().ToList();
            foreach(int t in tops)
            {
                var top=archives.Top(t);var entries=Tarc.Parse(top);
                Equal(true,ArchiveEditing.Relayout(top,entries,0,top.AsSpan(entries[0].Offset,entries[0].Size)).AsSpan().SequenceEqual(top));
            }
            var top14=archives.Top(14);var evAll=Tarc.Extract(top14,Tarc.Parse(top14).Single(e=>e.Name=="ev_all.ar"));var evEntries=Tarc.Parse(evAll);
            Equal(true,ArchiveEditing.Relayout(evAll,evEntries,5,evAll.AsSpan(evEntries[5].Offset,evEntries[5].Size)).AsSpan().SequenceEqual(evAll));
            // The tables read at fixed places sit in archives that are never moved.
            var fixedTops=ArchiveEditing.FixedTops(archives,disc.Profile);
            Equal(true,fixedTops.Contains(14));Equal(4,fixedTops.Count);
            // Grow quest 6's first event far past its space: the files after it move, every one still reads the same.
            int quest=library.TopOf(3276);var original=archives.Top(quest);var first=Tarc.Parse(original)[0];
            var bigger=new byte[Tarc.Extract(original,first).Length+600];new Random(7).NextBytes(bigger);
            var grown=ArchiveEditing.Repack(original,first.Name,bigger);
            Equal(true,grown.Length>original.Length);
            foreach(var e in Tarc.Parse(original).Skip(1))Equal(true,Tarc.Extract(grown,Tarc.Parse(grown).Single(x=>x.Name==e.Name)).AsSpan().SequenceEqual(Tarc.Extract(original,e)));
            Equal(true,Tarc.Extract(grown,Tarc.Parse(grown)[0]).AsSpan().SequenceEqual(bigger));
            // Within its sector the archive grows in place and its index size changes; past it, it moves to the free end of DATA3.DAT.
            var (sector,size)=archives.Row(quest);long capacity=(size+2047)/2048*2048;
            var small=ArchiveEditing.Patches(archives,[(quest,(Func<byte[],byte[]>)(t=>{var r=new byte[size+1];t.CopyTo(r,0);return r;}))],fixedTops);
            if(size+1<=capacity){Equal(true,small.Any(p=>p.Offset==archives.RowOffset(quest)+8));Equal(true,small.All(p=>p.Offset<archives.Data3Offset+(sector+capacity/2048)*2048 || p.Offset>=archives.RowOffset(0)));}
            var moved=ArchiveEditing.Patches(archives,[(quest,(Func<byte[],byte[]>)(_=>grown.Length>capacity?grown:[..grown,..new byte[capacity]]))],fixedTops);
            var row=moved.Single(p=>p.Offset==archives.RowOffset(quest)+4);
            uint newSector=BitConverter.ToUInt32(row.Bytes,0);
            Equal(archives.FreeSector(),newSector);
            // The free end is one repeated filler byte: zero on the English disc, 0x80 on the Japanese one.
            var filler=iso.ReadRange(archives.Data3Offset+newSector*2048L,65536,65536);
            Equal(true,filler.All(b=>b==filler[0]));Equal(disc==en?(byte)0:(byte)0x80,filler[0]);
            Equal(true,moved.Any(p=>p.Offset==archives.Data3Offset+newSector*2048L && p.Expected!.All(b=>b==filler[0])));
            // Two events in one archive: the first rewritten in place (shorter, leaving a gap), then the second grown past its space.
            int a1=Tarc.Parse(original).Count;
            var shrunk=ArchiveEditing.Replace(original,Tarc.Parse(original)[1].Name,new byte[8]);
            var both=ArchiveEditing.Repack(shrunk,Tarc.Parse(original)[2].Name,bigger);
            Equal(true,Tarc.Extract(both,Tarc.Parse(both)[1]).AsSpan().SequenceEqual(new byte[8]));Equal(true,Tarc.Extract(both,Tarc.Parse(both)[2]).AsSpan().SequenceEqual(bigger));
            foreach(var e in Tarc.Parse(original).Skip(3))Equal(true,Tarc.Extract(both,Tarc.Parse(both).Single(x=>x.Name==e.Name)).AsSpan().SequenceEqual(Tarc.Extract(original,e)));
            // An archive with the base data cannot move.
            Throws<EditException>(()=>ArchiveEditing.Patches(archives,[(14,(Func<byte[],byte[]>)(t=>[..t,..new byte[4096]]))],fixedTops));
        }
    });
    Test("Partners and bags read the same on both discs, and edits are checked",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal(16,disc.PartnerBytes.Count);Equal(true,disc.PartnerCopiesMatch);
            var s=new EditSession(disc);
            string Partners(EditSession session,int id)=>string.Join(",",Enumerable.Range(0,PartnerData.Slots).Where(k=>session.Value(EditSession.Partners,id,PartnerData.PartnerKey(k))!=0)
                .Select(k=>$"{session.Value(EditSession.Partners,id,PartnerData.PartnerKey(k)):X}:{session.Value(EditSession.Partners,id,PartnerData.BondKey(k))}"));
            Equal("53:40",Partners(s,0x54));Equal("98:50,60:10",Partners(s,0x86));Equal("53:100",Partners(s,0x7D));Equal("84:100",Partners(s,0x74));
            Equal((byte)0x06,disc.PartnerBytes[0x54].Span[6]);
            Equal("53,59,7C",string.Join(",",disc.BagBytes.Keys.Order().Select(k=>k.ToString("X"))));
            string Bag(int id)=>string.Join(",",Enumerable.Range(0,s.Value(EditSession.Bags,id,"bagCount")).Select(k=>$"{s.Value(EditSession.Bags,id,BagData.ItemKey(k)):X}:{s.Value(EditSession.Bags,id,BagData.UsesKey(k))}"));
            Equal("21:101,21:101",Bag(0x53));Equal("1CD:1,1CD:1,1CD:1,1CD:1",Bag(0x7C));Equal("192:20,192:20,192:20",Bag(0x59));
            Equal(2,s.Value(EditSession.Bags,0x53,"bagSlot"));
            Throws<EditException>(()=>s.Apply(EditSession.Partners,0x54,new Dictionary<string,int>{["partner2"]=0x54}));
            Throws<EditException>(()=>s.Apply(EditSession.Partners,0x54,new Dictionary<string,int>{["partner2"]=0x53}));
            Throws<EditException>(()=>s.Apply(EditSession.Partners,0x54,new Dictionary<string,int>{["partner2"]=0x999}));
            Throws<EditException>(()=>s.Apply(EditSession.Partners,0x54,new Dictionary<string,int>{["partner1Bond"]=1001}));
            Throws<EditException>(()=>s.Apply(EditSession.Bags,0x53,new Dictionary<string,int>{["bagItem3"]=0x1CD}));
            Throws<EditException>(()=>s.Apply(EditSession.Bags,0x53,new Dictionary<string,int>{["bagItem1"]=0x7FF}));
            Equal(false,s.IsDirty);
            s.Apply(new FieldEdit[]{new(EditSession.Partners,0x54,"partner2",0x55),new(EditSession.Partners,0x54,"partner2Bond",30),new(EditSession.Bags,0x53,"bagItem1",0)});
            var back=EditSession.FromJson(disc,s.ToJson());
            Equal("53:40,55:30",Partners(back,0x54));
            Equal(0,back.Value(EditSession.Bags,0x53,"bagItem1"));
            s.Undo();Equal(false,s.IsEdited(EditSession.Partners,0x54));Equal(false,s.IsEdited(EditSession.Bags,0x53));
            // Bag records are padded past the end of the block on the disc; nothing in the padding is ever written.
            Equal(BagData.RecordLength,disc.BagBytes[0x53].Length);Equal(true,disc.BagBytes[0x53].Span[(BagData.HeaderLength+2*BagData.EntryLength)..].ToArray().All(b=>b==0));
        }
        Equal(true,jp!.PartnerBytes.Keys.Order().SequenceEqual(en!.PartnerBytes.Keys.Order()));
        Equal(true,jp.PartnerBytes.All(p=>p.Value.Span.SequenceEqual(en.PartnerBytes[p.Key].Span)));
        Equal(true,jp.BagBytes.All(p=>p.Value.Span.SequenceEqual(en.BagBytes[p.Key].Span)));
    });
    Test("Mercenary fees read from the program on both discs, and an edited program is still recognised",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal(18,disc.MercenaryBytes.Count);
            var s=new EditSession(disc);
            Equal("200/150",$"{s.Value(EditSession.Mercenaries,0x90,"mercenaryBase")}/{s.Value(EditSession.Mercenaries,0x90,"mercenaryPerLevel")}");
            Equal("1200/50",$"{s.Value(EditSession.Mercenaries,0x6F,"mercenaryBase")}/{s.Value(EditSession.Mercenaries,0x6F,"mercenaryPerLevel")}");
            Equal("0/0",$"{s.Value(EditSession.Mercenaries,0x87,"mercenaryBase")}/{s.Value(EditSession.Mercenaries,0x87,"mercenaryPerLevel")}");
            Equal(true,disc.MercenaryBytes.Keys.All(id=>disc.Characters.Any(c=>c.Id==id)));
            Throws<EditException>(()=>s.Apply(EditSession.Mercenaries,0x90,new Dictionary<string,int>{["mercenaryBase"]=100000}));
            s.Apply(EditSession.Mercenaries,0x90,new Dictionary<string,int>{["mercenaryBase"]=5000,["mercenaryPerLevel"]=0});
            Equal(5000,EditSession.FromJson(disc,s.ToJson()).Value(EditSession.Mercenaries,0x90,"mercenaryBase"));
            var patches=Exporter.PatchesFor(s,disc);
            Equal(true,patches.All(p=>p.Offset>=disc.ExecutableFile!.Offset && p.Offset<disc.ExecutableFile.Offset+disc.ExecutableFile.Length));
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var exe=new byte[disc.ExecutableFile!.Length];stream.Position=disc.ExecutableFile.Offset;stream.ReadExactly(exe);
            var edited=exe.ToArray();
            foreach(var p in patches) p.Bytes.CopyTo(edited,(int)(p.Offset-disc.ExecutableFile.Offset));
            Equal(false,edited.AsSpan().SequenceEqual(exe));
            Equal(true,GameRules.Normalize(edited).AsSpan().SequenceEqual(exe));
            Equal(5000,BinaryPrimitives.ReadInt32LittleEndian(MercenaryData.Read(edited).Single(r=>r.Id==0x90).Bytes.AsSpan(8)));
        }
    });
    Test("Armies: every team file on both discs, names, bosses and edits written to every copy",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            // The starting characters' file (36 unit blocks) is edited on the Characters page and is not an army.
            Equal(275,disc.Armies.Count);Equal(3042,disc.Armies.Sum(a=>a.Units.Count));Equal(530,disc.Armies.Sum(a=>a.Locations.Count));
            Equal(false,disc.Armies.Any(a=>a.Locations.Contains(disc.Files.Single(f=>f.Name.Equals("DATA3.DAT",StringComparison.OrdinalIgnoreCase)).Offset+disc.Profile!.UnitTable)));
            Equal(disc.ArmyUnitBytes.Count,disc.Armies.Sum(a=>a.Units.Count));
            var army=disc.Armies.Single(a=>a.Id==380);Equal(2,army.Locations.Count);
            var baldur=army.Units[0];Equal("Baldur",baldur.Name);Equal(true,baldur.Named);Equal(false,army.Units[1].Named);
            var s=new EditSession(disc);int key=baldur.Key;
            Equal("145/11/47/487/1",$"{s.Value(EditSession.ArmyUnits,key,"class")}/{s.Value(EditSession.ArmyUnits,key,"level")}/{s.Value(EditSession.ArmyUnits,key,"item1")}/{s.Value(EditSession.ArmyUnits,key,"item3")}/{s.Value(EditSession.ArmyUnits,key,"item3Drop")}");
            Equal("Murtagh",disc.Armies.Single(a=>a.Id==376).Units[0].Name);
            Throws<EditException>(()=>s.Apply(EditSession.ArmyUnits,key,new Dictionary<string,int>{["item2"]=0x7FF}));
            s.Apply(EditSession.ArmyUnits,key,new Dictionary<string,int>{["level"]=15,["item3Drop"]=0,["item1Drop"]=1,["strengthBonus"]=5});
            var patches=Exporter.PatchesFor(s,disc);
            // Level, drop flags and strength bonus: four changed bytes, each written to both copies of the army file.
            Equal(8,patches.Count);
            Equal(true,patches.All(p=>army.Locations.Any(l=>p.Offset>=l && p.Offset<l+army.Length)));
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            foreach(var p in patches){var b=new byte[1];stream.Position=p.Offset;stream.ReadExactly(b);Equal(p.Expected![0],b[0]);}
            Equal(15,EditSession.FromJson(disc,s.ToJson()).Value(EditSession.ArmyUnits,key,"level"));
            // Units whose class or items are not in the lists can still be edited, keeping those values.
            var odd=disc.ArmyUnitBytes.First(p=>!disc.ClassBytes.ContainsKey(p.Value.Span[19])).Key;
            Equal(true,s.Apply(EditSession.ArmyUnits,odd,new Dictionary<string,int>{["level"]=(s.Value(EditSession.ArmyUnits,odd,"level")%30)+1}));
            Throws<EditException>(()=>s.Apply(EditSession.ArmyUnits,key,new Dictionary<string,int>{["class"]=0xFF}));
        }
        Equal(true,jp!.Armies.Select(a=>a.Id).SequenceEqual(en!.Armies.Select(a=>a.Id)));
        // Armies are named after the battles their events place them in (checked in game for 65, 164 and 371).
        Equal("Army 371 (Quest 6: Cruel Brothers)",en.Armies.Single(a=>a.Id==371).Label);Equal("Army 371 (Quest 6: Cruel Brothers)",jp.Armies.Single(a=>a.Id==371).Label);
        Equal("Army 065 (Story mission 9: Breaking Bridges)",en.Armies.Single(a=>a.Id==65).Label);
        Equal("Army 164 (Story mission 5: Hold the Fort)",en.Armies.Single(a=>a.Id==164).Label);
        Equal("Army 053 (Story mission 3: The Three Priests and Story mission 11: Bloodstained Valley)",en.Armies.Single(a=>a.Id==53).Label);
        Equal(true,en.Armies.Count(a=>a.Label.Contains('('))>=150);
        Equal(true,jp.Armies.Select(a=>a.Label.Contains('(')).SequenceEqual(en.Armies.Select(a=>a.Label.Contains('('))));
        Equal(true,jp.ArmyUnitBytes.All(p=>p.Value.Span.SequenceEqual(en.ArmyUnitBytes[p.Key].Span)));
        using(var stream=new FileStream(en.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            var names=ArmyData.LoadNames(new GameArchives(new IsoImage(stream)));
            Equal(1566,names.Count);Equal(true,names.All(n=>ArmyData.Names[n.Key]==n.Value));
        }
    });
    Test("Battle stat choices: game code on both discs, each choice writes the matching stat call, and the program is still recognised",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            foreach(int id in new[]{95,96,97,98,99,100,101,102}) Equal(true,disc.RuleWords.ContainsKey(id));
            var s=new EditSession(disc);
            Equal("0/3/2/3/1/0/1/3",string.Join("/",new[]{95,96,97,98,99,100,101,102}.Select(id=>s.Value(EditSession.Rules,id,GameRules.ById(id).Key))));
            s.Apply(new FieldEdit[]{new(EditSession.Rules,95,"physicalAttackStat",4),new(EditSession.Rules,97,"physicalDefenseStat",4),new(EditSession.Rules,100,"weightStat",4),new(EditSession.Rules,101,"weightShare",3)});
            var patches=Exporter.PatchesFor(s,disc);
            uint Word(long fileOffset)=>BitConverter.ToUInt32(patches.Single(p=>p.Offset==disc.ExecutableFile!.Offset+fileOffset).Bytes);
            uint hp=0x0C000000u|(0x001808B0u>>2);
            Equal(hp,Word(0x0017C00C-GameRules.ProgramToFile));Equal(hp,Word(0x0017B9F8-GameRules.ProgramToFile));Equal(hp,Word(0x0017BB48-GameRules.ProgramToFile));Equal(hp,Word(0x0017C4E0-GameRules.ProgramToFile));
            Equal(0x000218C3u,Word(0x0017C4EC-GameRules.ProgramToFile));Equal(0x000218C3u,Word(0x0017C4F4-GameRules.ProgramToFile));
            Equal(6,patches.Count);
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var exe=new byte[disc.ExecutableFile!.Length];stream.Position=disc.ExecutableFile.Offset;stream.ReadExactly(exe);
            var edited=exe.ToArray();foreach(var p in patches)p.Bytes.CopyTo(edited,(int)(p.Offset-disc.ExecutableFile.Offset));
            Equal(true,GameRules.Normalize(edited).AsSpan().SequenceEqual(exe));
            Equal(true,GameRules.ById(96).Confirmed);Equal(true,GameRules.ById(95).Confirmed);
            var h=new EditSession(disc);
            Equal("0/0/50/0",string.Join("/",new[]{103,104,105,106}.Select(id=>h.Value(EditSession.Rules,id,GameRules.ById(id).Key))));
            h.Apply(new FieldEdit[]{new(EditSession.Rules,103,"hitAdds",5),new(EditSession.Rules,104,"criticalAdds",1),new(EditSession.Rules,105,"criticalThreshold",5)});
            var hp2=Exporter.PatchesFor(h,disc);
            uint W(uint address)=>BitConverter.ToUInt32(hp2.Single(p=>p.Offset==disc.ExecutableFile!.Offset+address-GameRules.ProgramToFile).Bytes);
            Equal(0x0C000000u|(0x001808B0u>>2),W(0x00179A08));Equal(0x0C000000u|(0x0017A230u>>2),W(0x00179420));Equal(0x28410006u,W(0x0017A200));Equal(0x2442FFFBu,W(0x0017A210));
            h.Apply(new FieldEdit[]{new(EditSession.Rules,103,"hitAdds",6),new(EditSession.Rules,107,"minimumDamage",1),new(EditSession.Rules,108,"knifeStat",4),new(EditSession.Rules,109,"knifeRandom",1)});
            Equal("0/0/0",string.Join("/",new[]{107,108,109}.Select(id=>new EditSession(disc).Value(EditSession.Rules,id,GameRules.ById(id).Key))));
            var dp=Exporter.PatchesFor(h,disc);
            uint D(uint address)=>BitConverter.ToUInt32(dp.Single(p=>p.Offset==disc.ExecutableFile!.Offset+address-GameRules.ProgramToFile).Bytes);
            Equal(0x24150001u,D(0x0022AEC4));
            var ally=new EditSession(disc);
            Equal("10/15/15/3/20/3/5/5/5/10/50",string.Join("/",Enumerable.Range(110,11).Select(id=>ally.Value(EditSession.Rules,id,GameRules.ById(id).Key))));
            ally.Apply(new FieldEdit[]{new(EditSession.Rules,110,"commanderHit",50),new(EditSession.Rules,115,"supporterCritical",5),new(EditSession.Rules,119,"battleCryHit",0)});
            var ap=Exporter.PatchesFor(ally,disc);
            uint A(uint address)=>BitConverter.ToUInt32(ap.Single(p=>p.Offset==disc.ExecutableFile!.Offset+address-GameRules.ProgramToFile).Bytes);
            Equal(0x24030032u,A(0x001B7FEC));Equal(0x00431018u,A(0x001B7FF4));Equal(0u,A(0x001B7FFC));Equal(0x00021100u,A(0x001B7A20));
            Equal(0x24020000u,A(0x001B8068));Equal(0x00621018u,A(0x001B806C));Equal(0u,A(0x001B8070));
            Equal("1/1",$"{ally.Value(EditSession.Rules,121,"celerityMove")}/{ally.Value(EditSession.Rules,122,"slowStartMove")}");
            ally.Apply(new FieldEdit[]{new(EditSession.Rules,121,"celerityMove",3),new(EditSession.Rules,122,"slowStartMove",0)});
            var mp=Exporter.PatchesFor(ally,disc);
            uint M(uint address)=>BitConverter.ToUInt32(mp.Single(p=>p.Offset==disc.ExecutableFile!.Offset+address-GameRules.ProgramToFile).Bytes);
            Equal(0x2610001Eu,M(0x0017E768));Equal(0x26100000u,M(0x0017E6A0));Equal(0x0C000000u|(0x001808B0u>>2),D(0x00227100));Equal(0u,D(0x00227110));
            Equal(0x0000102Du,BitConverter.ToUInt32(Exporter.PatchesFor(h,disc).Single(p=>p.Offset==disc.ExecutableFile!.Offset+0x00179A08-GameRules.ProgramToFile).Bytes));
        }
    });
    Test("Randomized starting items keep their type, level limit and restricted items, and repeat with the same seed",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            var s=new EditSession(disc);
            var options=new RandomizeOptions(1234);
            var edits=Randomizer.StartingItems(s,options);
            Equal(true,edits.Count>40);
            Equal(true,edits.SequenceEqual(Randomizer.StartingItems(s,options)));
            Equal(false,edits.SequenceEqual(Randomizer.StartingItems(s,options with {Seed=99})));
            var items=disc.Items.ToDictionary(i=>i.Id);
            foreach(var e in edits.Where(e=>!e.Field.EndsWith("Uses",StringComparison.Ordinal)))
            {
                var old=items[s.Value(e.Table,e.Id,e.Field)];var picked=items[e.Value];
                Equal(old.Type,picked.Type);Equal(true,picked.Level<=old.Level);Equal(true,picked.Price>0);Equal(false,picked.Name=="Bag");
            }
            // Enid's Lady Sword is hers alone, and bags stay in place so their contents still have somewhere to go.
            Equal(false,edits.Any(e=>e.Table==EditSession.Characters && e.Id==0x84 && e.Field=="item1"));
            Equal(false,edits.Any(e=>!e.Field.EndsWith("Uses",StringComparison.Ordinal) && e.Value==0x1DA));
            Equal(true,edits.Any(e=>e.Table==EditSession.Bags));
            Equal(false,Randomizer.StartingItems(s,options with {Bags=false}).Any(e=>e.Table==EditSession.Bags));
            Equal(false,Randomizer.StartingItems(s,options with {Weapons=false,Ammunition=false,Other=true}).Any(e=>items[s.Value(e.Table,e.Id,e.Field.Replace("Uses",""))].Type<21));
            s.Apply(edits);Equal(true,s.IsDirty);
            var back=EditSession.FromJson(disc,s.ToJson());
            Equal(true,edits.All(e=>back.Value(e.Table,e.Id,e.Field)==e.Value));
            s.Undo();Equal(false,s.IsDirty);
        }
    });
    Test("Furniture prices: event 727 on both discs, and stable and furniture edits share archives without overlapping",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal("23000,19000,25000,26000,27500,30000,45000,17000,22000,42000,36000,14500,32000",
                string.Join(",",FurniturePrices.Table.Slots.Select(s=>BinaryPrimitives.ReadInt32LittleEndian(disc.FurniturePriceBytes[s.Item].Span))));
            Equal(13,FurniturePrices.Names.Count);
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var archives=new GameArchives(new IsoImage(stream));
            var (_,stableDecoded,stableCopies)=StablePrices.Load(archives);
            var (_,furnitureDecoded,furnitureCopies)=FurniturePrices.Table.Load(archives);
            Equal(true,furnitureCopies.Count>=2);
            Equal(true,furnitureCopies.Any(c=>stableCopies.Any(s=>s.Top==c.Top)));
            var stableEdited=StablePrices.Write(stableDecoded,new Dictionary<int,int>{[0x205]=1234});
            var furnitureEdited=FurniturePrices.Table.Write(furnitureDecoded,new Dictionary<int,int>{[13]=99,[8]=60000});
            Throws<EditException>(()=>FurniturePrices.Table.Write(furnitureDecoded,new Dictionary<int,int>{[6]=500}));
            var originalPrices=FurniturePrices.Table.Read(furnitureDecoded);
            var listTops=FurnitureList.Locate(archives);
            Equal(true,listTops.Count>=1);
            foreach(int top in listTops)
            {
                var data=archives.Top(top);
                var list=Tarc.Extract(data,Tarc.Parse(data).Single(e=>e.Name==FurnitureList.ArchiveName));
                Equal(true,FurnitureList.Read(list,originalPrices)!.All(p=>originalPrices[p.Key]==p.Value));
            }
            var patches=ScriptPrices.Combined(archives,[(StablePrices.Table,stableCopies,stableEdited),(FurniturePrices.Table,furnitureCopies,furnitureEdited)],
                FurnitureList.Edits(archives,originalPrices,new Dictionary<int,int>{[13]=99,[8]=60000}));
            var ordered=patches.OrderBy(p=>p.Offset).ToList();
            for(int i=1;i<ordered.Count;i++) Equal(true,ordered[i].Offset>=ordered[i-1].Offset+ordered[i-1].Bytes.Length);
            foreach(int top in stableCopies.Select(c=>c.Top).Concat(furnitureCopies.Select(c=>c.Top)).Distinct())
            {
                var bytes=archives.Top(top);long start=archives.TopOffset(top);
                foreach(var p in patches.Where(p=>p.Offset>=start && p.Offset<start+bytes.Length)) p.Bytes.CopyTo(bytes,(int)(p.Offset-start));
                foreach(var copy in stableCopies.Where(c=>c.Top==top)) Equal(true,StablePrices.Extract(bytes,copy).AsSpan().SequenceEqual(stableEdited));
                foreach(var copy in furnitureCopies.Where(c=>c.Top==top)) Equal(true,FurniturePrices.Table.Extract(bytes,copy).AsSpan().SequenceEqual(furnitureEdited));
                if(listTops.Contains(top))
                {
                    var list=Tarc.Extract(bytes,Tarc.Parse(bytes).Single(e=>e.Name==FurnitureList.ArchiveName));
                    var shown=FurnitureList.Read(list,originalPrices)!;
                    Equal(99,shown[13]);Equal(60000,shown[8]);Equal(originalPrices[1],shown[1]);
                }
            }
            var s=new EditSession(disc);
            s.Apply(EditSession.FurniturePrices,13,new Dictionary<string,int>{["furniturePrice"]=99});
            Throws<EditException>(()=>s.Apply(EditSession.FurniturePrices,13,new Dictionary<string,int>{["furniturePrice"]=0}));
            Equal(99,EditSession.FromJson(disc,s.ToJson()).Value(EditSession.FurniturePrices,13,"furniturePrice"));
        }
        Equal(true,jp!.FurniturePriceBytes.All(p=>p.Value.Span.SequenceEqual(en!.FurniturePriceBytes[p.Key].Span)));
    });
    Test("Stable prices: event 602 on both discs, all copies agree, and an edit fits and touches only the event",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            var archives=new GameArchives(new IsoImage(stream));
            var (prices,decoded,copies)=StablePrices.Load(archives);
            Equal(3,copies.Count);Equal(1188,decoded.Length);
            // Every compressed event in ev_all.ar decodes the same way with the game's end-marker rule, using all its stored bytes.
            var top14=archives.Top(14);var evAll=Tarc.Extract(top14,Tarc.Parse(top14).Single(e=>e.Name=="ev_all.ar"));
            foreach(var e in Tarc.Parse(evAll).Where(e=>e.Compressed))
            {
                var stored=evAll.AsSpan(e.Offset,e.Size);var (game,read)=BerwickLz.DecompressToEndMarker(stored,1<<20);
                Equal(true,game.AsSpan().SequenceEqual(BerwickLz.Decompress(stored)));Equal(e.Size-8,read);
            }
            Equal("1000,2500,5000,8000,15000,15000,12000,18000,20000,24000,10000",string.Join(",",StablePrices.Slots.Select(s=>prices[s.Item])));
            Equal(0,StablePrices.Patches(archives,copies,decoded).Count);
            var edited=StablePrices.Write(decoded,new Dictionary<int,int>{[0x205]=1234,[0x20E]=99999});
            Equal(1234,StablePrices.Read(edited)[0x205]);Equal(99999,StablePrices.Read(edited)[0x20E]);Equal(2500,StablePrices.Read(edited)[0x206]);
            var patches=StablePrices.Patches(archives,copies,edited);
            Equal(true,patches.Count>0);
            foreach(var copy in copies)
            {
                var top=archives.Top(copy.Top);long at=archives.TopOffset(copy.Top);
                foreach(var p in patches.Where(p=>p.Offset>=at && p.Offset<at+top.Length))
                {
                    Equal(true,top.AsSpan((int)(p.Offset-at),p.Bytes.Length).SequenceEqual(p.Expected));
                    p.Bytes.CopyTo(top,(int)(p.Offset-at));
                }
                Equal(true,StablePrices.Extract(top,copy).AsSpan().SequenceEqual(edited));
            }
            Equal(patches.Count,copies.Sum(c=>{long at=archives.TopOffset(c.Top);int len=archives.Top(c.Top).Length;return patches.Count(p=>p.Offset>=at && p.Offset+p.Bytes.Length<=at+len);}));
            Throws<EditException>(()=>StablePrices.Write(decoded,new Dictionary<int,int>{[0x205]=0}));
            Throws<EditException>(()=>StablePrices.Write(decoded,new Dictionary<int,int>{[0x210]=500}));
            var broken=decoded.ToArray();broken[0xAC-4]=2;Throws<InvalidDataException>(()=>StablePrices.Read(broken));
        }
    });
    Test("Live game: both programs list the 15 story missions and 45 quest battles a sortie can load",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            byte[] exe;
            using(var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var iso=new IsoImage(stream)) exe=iso.ReadRange(disc.ExecutableFile!.Offset,(int)disc.ExecutableFile.Length,(int)disc.ExecutableFile.Length);
            var battles=LiveGame.Battles(exe);
            Equal(60,battles.Count);
            Equal("26,13,6,21,31,15,25,24,9,22,14,23,11,12,39",string.Join(",",battles.Take(15).Select(b=>b.Battle)));
            Equal(new LiveBattle("Quest 6",104),battles.First(b=>b.Name=="Quest 6"));Equal(true,battles.Skip(15).All(b=>b.Battle is >= 100 and <= 200));
            using(var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var iso=new IsoImage(stream))
            {
                var titles=ArmyData.LoadBattleTitles(new GameArchives(iso));
                var named=LiveGame.Battles(exe,titles);
                // The English disc's titles are read from it; the Japanese disc gets the same English titles from the catalogue.
                Equal("Story mission 9: Breaking Bridges",named[8].Text);Equal("Quest 6: Cruel Brothers",named.First(b=>b.Name=="Quest 6").Text);
                Equal("A Reason to Fight",titles["mission001"]);Equal("St. Stefano",titles["quest030"]);Equal("Save My Colts!",titles["quest093"]);
                Equal(false,titles.ContainsKey("quest035"));Equal(false,titles.ContainsKey("quest001"));Equal(true,named.Count(b=>b.Title.Length>0)>=55);
                Equal(230,titles.Count);
            }
        }
    });
    Test("Battle maps: file lookup, terrain and spots on both discs, and maps no battle uses",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            using var stream=new FileStream(disc.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
            using var iso=new IsoImage(stream);
            var archives=new GameArchives(iso);var library=new EventLibrary(archives);
            Equal(3716,archives.Find("bmap/bmap009.ar"));Equal(3677,archives.Find("bmap/qmap005.ar"));Equal(-1,archives.Find("bmap/maptest.ar"));Equal(-1,archives.Find("nofolder/bmap009.ar"));
            var files=library.BattleMapFiles();
            Equal("bmap009.ar",files[9]);Equal("qmap005.ar",files[104]);Equal("bmap003.ar",files[126]);Equal("zmap002.ar",files[201]);
            var map=BattleMaps.Load(archives,library,9,files[9])!;
            Equal((26,28),(map.Width,map.Height));
            Equal("Withdraw 22,1;Chest 14,2;Investigate 18,2;Gate 10,4;Gate 3,9;Drawbridge 1,11;Investigate 5,15;Investigate 18,20;Investigate 22,26",string.Join(";",map.Spots.Where(s=>s.Kind is 8 or 9 or 13 or 14 or 33).Select(s=>$"{s.Name} {s.X},{s.Y}")));
            Equal(BattleMaps.Border,map.KindAt(-1,0));Equal(BattleMaps.Border,map.KindAt(0,64));
            var onDisc=BattleMaps.Files(archives);
            Equal(83,onDisc.Count);Equal(41,onDisc.Count(f=>!files.Values.Contains(f.File)));
            var unused=onDisc.First(f=>f.File=="qmap001.ar");
            var empty=BattleMaps.Load(archives,null,unused.Top,0,unused.File)!;
            Equal((14,19,0),(empty.Width,empty.Height,empty.Spots.Count));
            var picture=BattleMaps.LoadPicture(archives,map.Top)!;
            Equal((1280,1024),(picture.Width,picture.Height));
            uint Pixel(int x,int y)=>BinaryPrimitives.ReadUInt32LittleEndian(picture.Pixels.AsSpan((y*picture.Width+x)*4));
            Equal(0xFFE4E3D9u,Pixel(820,450));Equal(0xFF043BA9u,Pixel(420,450));
            Equal("4,7",string.Join(",",library.StartingLayers(9)!));Equal("4",string.Join(",",library.StartingLayers(104)!));Equal(null,library.StartingLayers(14));
            var started=BattleMaps.LoadPicture(archives,map.Top,library.StartingLayers(9))!;
            uint At(MapPicture p,int x,int y)=>BinaryPrimitives.ReadUInt32LittleEndian(p.Pixels.AsSpan((y*p.Width+x)*4));
            Equal(0xFFE4E3D9u,At(started,820,450));Equal(false,started.Pixels.AsSpan().SequenceEqual(picture.Pixels));
            Equal(true,At(BattleMaps.LoadPicture(archives,map.Top,[10])!,820,450)!=0xFFE4E3D9u);
            foreach(var file in files.Values.Distinct().Where(f=>onDisc.Any(d=>d.File==f)))
            {
                var used=BattleMaps.Load(archives,library,files.First(f=>f.Value==file).Key,file)!;var art=BattleMaps.LoadPicture(archives,used.Top)!;
                Equal(true,art.Width>=used.Width*BattleMaps.ColumnWidth && art.Height>=used.Height*BattleMaps.RowHeight);
            }
            Throws<InvalidDataException>(()=>Texture.Decode(new byte[0x40]));
            // Start positions, checked in a running game on quest 6 (battle 104).
            Equal("371/1/1",string.Join(";",library.StartingPlacements(104).Select(p=>$"{p.Army}/{p.Team}/{p.Group}")));
            Equal("65/1/1;66/2/2;69/2/10",string.Join(";",library.StartingPlacements(9).Select(p=>$"{p.Army}/{p.Team}/{p.Group}")));
            var quest=BattleMaps.Load(archives,library,104,files[104])!;
            Equal("(9, 3) (10, 3) (8, 3) (9, 2) (10, 2) (7, 2) (8, 2)",string.Join(" ",quest.Deployment));
            Equal("1,12 2,13 2,10 1,6 2,6 6,7 5,9 4,13 8,9 7,12 7,14 5,16 2,15",string.Join(" ",quest.Units.Select(u=>$"{u.X},{u.Y}")));
            Equal(true,quest.Units.Select((u,i)=>u.Army==371 && u.Team==1 && u.Index==i).All(ok=>ok));
            Equal("370/4 2,1 from 2,-1 turn 5;370/5 5,19 from 5,21 turn 6;370/6 2,1 from 2,-1 turn 7;370/7 5,19 from 5,21 turn 8",
                string.Join(";",quest.Arrivals.Select(r=>$"{r.Army}/{r.Script} {r.X},{r.Y} from {r.FromX},{r.FromY} turn {r.Turn}")));
            Equal(true,library.Reinforcements(11).Count(r=>r.Turn is not null)>=20);
            // Battle 12 brings unit 2 of ud083 each turn from 2 to 21 while an event flag is set; battle 9 has arrivals set off by its Withdraw spot.
            var ranged=library.Reinforcements(12).First(r=>r.Army==83 && r.Script==2);
            Equal("2-21 True",$"{ranged.Turn}-{ranged.LastTurn} {ranged.Conditional}");
            Equal(true,library.Reinforcements(9).Any(r=>r.Spot is {Kind:14,X:22,Y:1}));
            // Quest 6's turn arrivals come from its every-turn event; battle 9 has every-turn arrivals on turns the battle stores itself.
            Equal(true,quest.Arrivals.All(r=>r.EveryTurn));
            Equal(true,library.Reinforcements(9).Any(r=>r.EveryTurn && r.Turn is null && r.Army==67));
            Equal(true,library.Reinforcements(24).Any(r=>r.Talk==(83,1365)));
        }
    });
    Test("Game rules: original code present on both editions, edit, undo and project round trip",()=>{
        foreach(var disc in new[]{jp!,en!})
        {
            Equal(GameRules.All.Count,disc.RuleWords.Count);
            var s=new EditSession(disc);
            foreach(var rule in GameRules.All) Equal(rule.Default,s.Value(EditSession.Rules,rule.Id,rule.Key));
            s.Apply(EditSession.Rules,4,new Dictionary<string,int>{["strengthLimit"]=9});Equal(9,s.Value(EditSession.Rules,4,"strengthLimit"));s.Undo();
            s.Apply(EditSession.Rules,1,new Dictionary<string,int>{["shieldParry"]=2});
            s.Apply(EditSession.Rules,2,new Dictionary<string,int>{["battleExperience"]=60});
            Equal(2,s.Value(EditSession.Rules,1,"shieldParry"));Equal(0x2413003Cu,BitConverter.ToUInt32(s.EditedBytes(EditSession.Rules,2)));
            Throws<EditException>(()=>s.Apply(EditSession.Rules,2,new Dictionary<string,int>{["battleExperience"]=101}));
            var back=EditSession.FromJson(disc,s.ToJson());Equal(60,back.Value(EditSession.Rules,2,"battleExperience"));
            s.Undo();Equal(0,s.Value(EditSession.Rules,2,"battleExperience"));
            Throws<EditException>(()=>EditSession.FromJson(disc,back.ToJson().Replace("\"field\": \"shieldParry\"","\"field\": \"battleExperience\"")));
        }
    });
    Test("Character data is identical between editions",()=>Equal(0,RecordComparison.Compare(jp!.Characters,en!.Characters).Count));
    Console.WriteLine($"Final: {passed} passed, {failed} failed");
}
return failed==0?0:1;

static void U32(byte[] b,int p,uint v) { BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(p),v); BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(p+4),v); }
static byte[] Fixture()
{
    var b=new byte[24*2048];int p=16*2048;b[p]=1;Encoding.ASCII.GetBytes("CD001").CopyTo(b,p+1);b[p+6]=1;
    BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(p+128),2048);BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(p+130),2048);
    U32(b,p+80,24);b[p+156]=34;U32(b,p+158,20);U32(b,p+166,2048);b[p+181]=2;b[p+188]=1;
    p=20*2048;var name=Encoding.ASCII.GetBytes("SYSTEM.CNF;1");b[p]=(byte)(33+name.Length+1);U32(b,p+2,21);U32(b,p+10,3);b[p+32]=(byte)name.Length;name.CopyTo(b,p+33);
    Encoding.ASCII.GetBytes("abc").CopyTo(b,21*2048);return b;
}

sealed class ImmediateProgress(Action<InspectionProgress> report) : IProgress<InspectionProgress>
{
    public void Report(InspectionProgress value) => report(value);
}
