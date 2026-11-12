using Berwick.Core;
using System.Buffers.Binary;
using System.Text;

int passed = 0, failed = 0;
void Test(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception e) { failed++; Console.WriteLine($"FAIL {name}: {e.Message}"); }
}
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws<T>(Action action) where T:Exception
{
    try { action(); } catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
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
    var expected=new Dictionary<string,int>{["might"]=r.Might,["accuracy"]=r.Accuracy,["weight"]=r.Weight,["minRange"]=r.MinRange,["maxRange"]=r.MaxRange,["critical"]=r.Critical,["level"]=r.Level,["price"]=r.Price,["durability"]=r.Durability,["uses"]=r.Uses};
    foreach(var f in ItemEncoder.Fields) Equal($"{f.Key}={expected[f.Key]}",$"{f.Key}={ItemEncoder.Read(b,f)}");
});
Test("Item encoder rejects out of range values",()=>{
    Throws<EditException>(()=>ItemEncoder.Write(new byte[56],ItemEncoder.Field("might"),64));
    Throws<EditException>(()=>ItemEncoder.Write(new byte[56],ItemEncoder.Field("durability"),-1));
    Throws<EditException>(()=>ItemEncoder.Field("nonsense"));
    Equal("Minimum range cannot be greater than maximum range.",ItemEncoder.Validate(new Dictionary<string,int>{["minRange"]=2,["maxRange"]=1}));
});
Console.WriteLine($"{passed} passed, {failed} failed");
if(args.Length==2)
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
