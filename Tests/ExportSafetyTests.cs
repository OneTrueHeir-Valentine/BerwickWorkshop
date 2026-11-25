using Berwick.Core;

/// <summary>Disc export safety: patch application, copies that must agree, and files that must never be replaced.</summary>
public static class ExportSafetyTests
{
    public static void Run(Action<string, Action> test)
    {
        test("Export refuses a differing packed byte in a table copy",()=>{
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
        test("Export copies the source and applies patches across buffer edges",()=>{
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
        test("Export preserves an existing partial file",()=>{
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
        test("Export preserves a source named like a partial file",()=>{
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
        test("Export never replaces an existing destination",()=>{
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
    }

    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual} (line {line})");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
