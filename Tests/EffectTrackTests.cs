using Berwick.Core;
using System.Buffers.Binary;
internal static class EffectTrackTests
{
 public static void Run(Action<string,Action> test)
 {
  test("Effect records preserve hidden delays, flags and graph edges",()=>{
   var bytes=new byte[180];bytes[2]=2;bytes[3]=30;
   BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(60),0x35);bytes[62]=3;bytes[63]=4;bytes[81]=1;
   BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(74),21);bytes[84]=10;
   var track=EffectTrack.Decode("m2_1",1,new byte[16],bytes,null);
   Check(track.Records.Count==3,"Hidden records discarded");
   Check(!track.Records[0].Visible&&track.Records[0].DurationCounter==30&&track.Records[0].NextIndex==1,"Hidden delay lost");
   Check(track.Records[1].Visible&&track.Records[1].Picture==21&&track.Records[1].Texture==1&&track.Records[1].BlendMode==3,"Effect fields wrong");
   Check(track.Records[1].Flags==0x35&&track.Records[1].Depth==10,"Flags/depth lost");
   Check(track.Records[2].NextIndex is null,"End edge wrong");
   bytes[122]=1;var loop=EffectTrack.Decode("m2_1",1,new byte[16],bytes,null);
   Check(loop.Records[2].NextIndex==0,"Loop lost");
  });
  test("Effect artwork uses swapped source palette colors and transparency",()=>{
   var positions=new byte[120];
   BinaryPrimitives.WriteInt32LittleEndian(positions,2);
   BinaryPrimitives.WriteInt32LittleEndian(positions.AsSpan(12),24);
   BinaryPrimitives.WriteInt32LittleEndian(positions.AsSpan(16),96);
   positions[55]=0x6c;BinaryPrimitives.WriteInt32LittleEndian(positions.AsSpan(56),1);
   BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(88),1);
   BinaryPrimitives.WriteSingleLittleEndian(positions.AsSpan(92),2);
   var texture=new byte[8192];texture[0]=8;texture[1]=16;texture[2]=17;
   var palette=new byte[1024];
   new byte[]{11,22,33,127}.CopyTo(palette,16*4);
   new byte[]{44,55,66,127}.CopyTo(palette,8*4);
   new byte[]{77,88,99,0}.CopyTo(palette,9*4);
   var set=AnimationSet.FromArchive(1,Archive(new(){["vueffpos.dat"]=positions,["1.tex"]=texture,["eff0.pal"]=palette}));
   var image=set.DrawEffect(1,5,new(0,0,288,288));
   int at=(2*288+1)*4;
   Check(image.Pixels.AsSpan(at,8).SequenceEqual(new byte[]{33,22,11,255,66,55,44,255}),"Effect palette swap or BGRA is wrong");
   Check(image.Pixels[at+11]==0&&image.Pixels[at+15]==0,"Effect transparency is wrong");
   Check(set.EffectPalette(5).Name=="eff0.pal","Palette fallback not identified");
  });
  test("Effect decoder refuses malformed data and unsafe motion names",()=>{
   Reject(()=>EffectTrack.Decode("../escape",0,new byte[16],new byte[60],null));
   Reject(()=>EffectTrack.Decode("m0_0",0,new byte[16],new byte[61],null));
   var bytes=new byte[60];bytes[2]=2;
   Reject(()=>EffectTrack.Decode("m0_0",0,new byte[16],bytes,null));
  });
 }
 private static byte[] Archive(Dictionary<string,byte[]> files)
 {
  int names=16+files.Count*12, payload=names+files.Keys.Sum(n=>n.Length+1);
  var data=new byte[payload+files.Values.Sum(v=>v.Length)];"TARC"u8.CopyTo(data);
  BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(4),files.Count);
  BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(8),names);
  int row=16,nameAt=names,at=payload;
  foreach(var (name,bytes) in files)
  {
   var encoded=System.Text.Encoding.ASCII.GetBytes(name);encoded.CopyTo(data,nameAt);nameAt+=encoded.Length+1;
   BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(row),at*2);
   BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(row+4),bytes.Length);
   bytes.CopyTo(data,at);at+=bytes.Length;row+=12;
  }
  return data;
 }
 private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
 private static void Reject(Action action){try{action();}catch(EditException){return;}throw new Exception("Malformed track accepted");}
}
