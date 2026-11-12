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
