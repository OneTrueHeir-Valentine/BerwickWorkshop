using Berwick.Core;

internal static class RuleAndFieldTests
{
    public static void Run(Action<string, Action> Test)
    {
        Test("Native formula terms and furniture triggers retain exact original instructions",()=>{
            Equal(0x02028021u,GameRules.ById(252).Original.Single());Equal(0u,GameRules.ById(252).Encode(1).Single());
            var accuracy=GameRules.ById(253);
            Equal("02028021,0200102D",string.Join(",",accuracy.Original.Select(w=>w.ToString("X8"))));
            Equal("00000000,0200102D",string.Join(",",accuracy.Encode(1).Select(w=>w.ToString("X8"))));
            Equal("00021043,02021021",string.Join(",",accuracy.Encode(2).Select(w=>w.ToString("X8"))));
            Equal("00021083,02021021",string.Join(",",accuracy.Encode(3).Select(w=>w.ToString("X8"))));
            Equal("00021040,02021021",string.Join(",",accuracy.Encode(4).Select(w=>w.ToString("X8"))));
            Equal<int?>(null,accuracy.Decode([accuracy.Encode(2)[0],accuracy.Encode(0)[1]]));
            foreach(int id in new[]{254,255}){
                var rule=GameRules.ById(id);Equal("00001010,00021083,00431021",string.Join(",",rule.Original.Select(w=>w.ToString("X8"))));
                Equal(0x00021043u,rule.Encode(1)[1]);
                for(int mode=0;mode<=6;mode++) Equal<int?>(mode,rule.Decode(rule.Encode(mode)));
                Equal<int?>(null,rule.Decode([rule.Encode(1)[0],rule.Encode(2)[1],rule.Encode(0)[2]]));
            }
            Equal(0x00021042u,GameRules.ById(254).Encode(3)[0]);Equal(0x00031042u,GameRules.ById(255).Encode(3)[0]);
            Equal(0x24040E74u,GameRules.ById(256).Original.Single());Equal(0x24040E75u,GameRules.ById(257).Original.Single());
            Equal(0x24040E79u,GameRules.ById(258).Original.Single());Equal(0x24040E74u,GameRules.ById(258).Encode(0).Single());
        });
        Test("Native skill query masks distinguish all three banks from food and equipment codes",()=>{
            foreach(var (name,mask) in new (string,uint)[]{ ("Counter",0x20u), ("Vantage",0x200u), ("Arrowbane",0x10000u), ("Adept",0x100u), ("Magicbane",0x40000080u), ("Parry",0x80000800u) })
                Equal(mask, SkillGrantBindings.QueryMaskForPersonalBit(Array.IndexOf(CharacterDecoder.SkillBits,name)));
            var named = CharacterDecoder.SkillBits.Select((name,bit)=>(name,bit)).Where(x=>x.name.Length>0).ToArray();
            Equal(named.Length,named.Select(x=>SkillGrantBindings.QueryMaskForPersonalBit(x.bit)).Distinct().Count());
            foreach(int reserved in new[]{-1,30,31,62,63,94,95,96})
                Throws<EditException>(()=>SkillGrantBindings.QueryMaskForPersonalBit(reserved));
        });
        Test("Expanded furniture assignments, Maim terms and default horse preserve original bindings",()=>{
            Equal("1,5,11,2,4,8,10,13,9",string.Join(",",GameRules.FurnitureTriggerPieces));
            Equal(9,GameRules.FurnitureTriggerRules.Count);
            Equal(0x24040E78u, GameRules.ById(266).Original.Single());
            var aura=GameRules.ById(261);
            Equal("24040E7C,24040E7C",string.Join(",",aura.Original.Select(w=>w.ToString("X8"))));
            Equal<int?>(null,aura.Decode([aura.Encode(1)[0],aura.Encode(2)[1]]));
            foreach(var (piece,id) in GameRules.FurnitureTriggerRules){
                var rule=GameRules.ById(id);Equal(piece,GameRules.FurnitureTriggerPieces[rule.Default]);
                Equal(true,FurniturePrices.EffectRules.ContainsKey(piece));
                for(int n=0;n<GameRules.FurnitureTriggerPieces.Length;n++)Equal<int?>(n,rule.Decode(rule.Encode(n)));
            }
            var maim=GameRules.ById(264);
            Equal("04610003,00038043,24630001,00038043",string.Join(",",maim.Original.Select(w=>w.ToString("X8"))));
            Equal("00621823,00031843,00028083,02038021",string.Join(",",maim.Encode(2).Select(w=>w.ToString("X8"))));
            Equal(0x0000802Du,maim.Encode(5)[2]);
            var horse=GameRules.ById(265);
            Equal("24050205,24050205",string.Join(",",horse.Original.Select(w=>w.ToString("X8"))));
            Equal("24050214,24050214",string.Join(",",horse.Encode(15).Select(w=>w.ToString("X8"))));
            Equal<int?>(null,horse.Decode([horse.Encode(1)[0],horse.Encode(2)[1]]));
        });
        Test("Game rules encode and decode every setting, and reject unknown code",()=>{
            foreach(var rule in GameRules.All)
            {
                for(int s=rule.Min;s<=rule.Max;s++)
                {
                    uint[] words;
                    try{words=rule.Encode(s);}catch(EditException){if(rule.Kind is RuleKind.Range or RuleKind.Pair)continue;throw;}
                    Equal($"{rule.Key}={s}",$"{rule.Key}={rule.Decode(words)}");
                }
                Equal<int?>(null,rule.Decode(Enumerable.Repeat(0xDEADBEEFu,rule.Sites.Count).ToArray()));
                Throws<EditException>(()=>rule.Encode(rule.Max+1));
                Equal($"{rule.Key}={rule.Default}",$"{rule.Key}={rule.Decode(rule.Original)}");
            }
            var parry=GameRules.ById(1);Equal(0x1020000Cu,parry.Original[0]);Equal(0x14000000u,parry.Encode(1)[0]);Equal(0x1000000Cu,parry.Encode(2)[0]);
            Equal("Parry",parry.Name);Equal("220,230,232,233,234,235,236,237,243,244,245,246,247,248,249,250,142,143,144,145,146,147,160,161,178,198,210,242,200,188,252,253,254,255,256,257,258,259,260,261,262,263,264,265,266",string.Join(",",GameRules.All.Where(r=>!r.Confirmed && r.Section != PromotionQuestWindows.Section).Select(r=>r.Id)));
            var block=GameRules.ById(9);Equal(0x10200019u,block.Original[0]);Equal(0x14000019u,block.Encode(1)[0]);Equal(0x10000019u,block.Encode(2)[0]);
            var training=GameRules.ById(11);Equal(0x001716A4u,training.Sites[0].Address);Equal(0x24090004u,training.Original[0]);Equal(0x24090032u,training.Encode(50)[0]);
            Equal(0x24090001u,GameRules.ById(12).Original[0]);Equal<int?>(null,training.Decode(new[]{0x24080004u}));
            var crit=GameRules.ById(13);Equal("2405000A,24060014",string.Join(",",crit.Original.Select(x=>x.ToString("X8"))));Equal("10 to 20 (normal)",crit.Describe(crit.Default));
            Equal("24050019,24060019",string.Join(",",crit.Encode(GameRules.RangeSetting(25,25)).Select(x=>x.ToString("X8"))));
            Throws<EditException>(()=>crit.Encode(GameRules.RangeSetting(30,20)));Equal<int?>(null,crit.Decode(new[]{0x2405001Eu,0x24060014u}));
            var failed=GameRules.ById(14);Equal(0x28A10002u,failed.Original[0]);Equal(0x28A10006u,failed.Encode(1)[0]);
            var guard=GameRules.ById(10);Equal(111,guard.Sites.Count);Equal(0x0022A63Cu,guard.Sites[0].Address);Equal(0x0022C2C0u,guard.Sites[27].Address);Equal(0x001481FCu,guard.Sites[41].Address);
            Equal(0x00324EDCu,guard.Sites[42].Address);Equal(0x0019C214u,guard.Sites[45].Address);Equal(0x0022E660u,guard.Sites[46].Address);Equal(0x0022E6CCu,guard.Sites[73].Address);
            Equal(0x00571000u,guard.Sites[74].Address);Equal(0x3B5000L,guard.Sites[74].FileOffset);Equal(0x3B506CL,guard.Sites[101].FileOffset);Equal(0x00126114u,guard.Sites[102].Address);Equal(0x74L,guard.Sites[103].FileOffset);Equal(0x90L,guard.Sites[110].FileOffset);
            Equal("False,True,False,False,False",string.Join(",",new[]{42,46,74,102,103}.Select(i=>guard.Sites[i].Live)));Equal(0x08590012u,guard.Original[74]);Equal(0x24420800u,guard.Original[102]);Equal(0x00570380u,guard.Original[105]);
            Equal(0u,guard.Original[0]);Equal(0x24020001u,guard.Original[1]);Equal(0x8E550008u,guard.Original[27]);Equal(0u,guard.Original[28]);Equal(0x27BDFFD0u,guard.Original[46]);
            foreach(int g in Enumerable.Range(1,5)){var w=guard.Encode(g);Equal(true,w.Take(27).SequenceEqual(guard.Original.Take(27)));Equal(0x08052072u,w[27]);Equal(0x0808B0B2u,w[40]);
                Equal("0C15C400,0C15C40A,0C15C414,0C15C414",string.Join(",",w.Skip(42).Take(4).Select(x=>x.ToString("X8"))));Equal(true,w.Skip(46).Take(28).SequenceEqual(guard.Original.Skip(46).Take(28)));
                Equal(0x03E00008u,w[74+8]);Equal(0x0200202Du,w[101]);Equal(0x24421800u,w[102]);Equal("1,3B5000,571000,571000,1000,1000,7,80",string.Join(",",w.Skip(103).Select(x=>x.ToString("X"))));
                var old=guard.Original.ToArray();Array.Copy(w,old,42);string[] oldCalls=["0C08B998","0C08B9A2","0C08B9AC","0C08B9AC"];for(int k=0;k<4;k++)old[42+k]=Convert.ToUInt32(oldCalls[k],16);Array.Copy(w,74,old,46,28);Equal<int?>(g,guard.Decode(old));
                var live=guard.LiveEncode(g);Equal(true,live.Skip(42).SequenceEqual(guard.Original.Skip(42)));Equal(true,live.Take(42).SequenceEqual(w.Take(42)));Equal<int?>(g,guard.LiveDecode(live));}
            Equal("00021883,00021843,00021803,00021883,00021843",string.Join(",",Enumerable.Range(1,5).Select(x=>guard.Encode(x)[30].ToString("X8"))));
            Equal("0002082B,0023180A",$"{guard.Encode(4)[31]:X8},{guard.Encode(4)[32]:X8}");Equal(0u,guard.Encode(1)[31]);Equal(0x00021843u,guard.Encode(5)[94]);
            foreach(int g in Enumerable.Range(0,6))Equal<int?>(g,guard.Decode(guard.Encode(g)));
            var mixed=guard.Encode(2);mixed[30]=guard.Original[30];Equal<int?>(null,guard.Decode(mixed));
            mixed=guard.Encode(4);mixed[44]=guard.Original[44];Equal<int?>(null,guard.Decode(mixed));
            Equal(0x28420005u,GameRules.ById(232).Original[0]);Equal(0x28420020u,GameRules.ById(232).Encode(32)[0]);
            Equal(0x2406FFFBu,GameRules.ById(233).Original[0]);Equal(0x24060000u,GameRules.ById(233).Encode(0)[0]);Equal<int?>(9,GameRules.ById(233).Decode(new[]{0x2406FFF7u}));
            Equal("2406FFFF,2407FFFF",string.Join(",",GameRules.ById(234).Original.Select(x=>x.ToString("X8"))));Equal(0x24060001u,GameRules.ById(235).Original[0]);
            var loose=GameRules.ById(236);Equal("AFA300B0,10000006,AFA200A0,00000000",string.Join(",",loose.Original.Select(x=>x.ToString("X8"))));
            Equal("24030005,AFA300B0,10000004,24020004",string.Join(",",loose.Encode(GameRules.RangeSetting(5,4)).Select(x=>x.ToString("X8"))));
            Equal<int?>(GameRules.RangeSetting(5,4),loose.Decode(loose.Encode(GameRules.RangeSetting(5,4))));Throws<EditException>(()=>loose.Encode(GameRules.RangeSetting(31,1)));
            var tight=GameRules.ById(237);Equal(0x8FA200B0u,tight.Original[2]);Equal("24020002,AFA200B0,24020000,AFA200A0",string.Join(",",tight.Encode(GameRules.RangeSetting(2,0)).Select(x=>x.ToString("X8"))));
            var vantage=GameRules.ById(15);Equal(36,vantage.Sites.Count);Equal(0x00224E68u,vantage.Sites[0].Address);Equal(0x00224EF4u,vantage.Sites[35].Address);
            Equal(0x12600005u,vantage.Original[0]);Equal(0xAE6211E0u,vantage.Original[35]);Equal(0x0C08B878u,vantage.Encode(1)[2]);
            Equal("24090000,24090001",$"{vantage.Encode(1)[12]:X8},{vantage.Encode(2)[12]:X8}");Equal(34,Enumerable.Range(0,36).Count(i=>vantage.Encode(2)[i]!=vantage.Original[i]));
            Equal<int?>(1,vantage.Decode(vantage.Encode(1)));Equal<int?>(2,vantage.Decode(vantage.Encode(2)));
            mixed=vantage.Encode(2);mixed[12]=vantage.Original[12];Equal<int?>(null,vantage.Decode(mixed));
            mixed=vantage.Original.ToArray();mixed[12]=vantage.Encode(2)[12];Equal<int?>(null,vantage.Decode(mixed));
            Equal("1,2,3,4,5,6,7",string.Join(",",Enumerable.Range(213,7).Select(i=>GameRules.ById(i).Original[0])));Equal(0x0016B080u,GameRules.ById(220).Original[0]);Equal(0x0016B000u,GameRules.ById(220).Encode(0)[0]);
            Equal("2841003E,2841002A,28410016,28410002",string.Join(",",Enumerable.Range(221,4).Select(i=>GameRules.ById(i).Original[0].ToString("X8"))));
            Equal("0000102D,0060102D,0060102D,0080102D,24020064",string.Join(",",Enumerable.Range(225,5).Select(i=>GameRules.ById(i).Original[0].ToString("X8"))));
            Equal(0x24020008u,GameRules.ById(228).Encode(8)[0]);Equal(0x26100014u,GameRules.ById(230).Original[0]);
            var fail=GameRules.ById(231);Equal("00031080,00431021",string.Join(",",fail.Original.Select(x=>x.ToString("X8"))));Equal("0000102D,00000000",string.Join(",",fail.Encode(0).Select(x=>x.ToString("X8"))));
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
            Equal(234,GameRules.All.Count(r=>r.Group.Length>0 && r.Section != PromotionQuestWindows.Section));
            var iaido=GameRules.ById(40);Equal(0x24700028u,iaido.Original[0]);Equal(0x24700064u,iaido.Encode(100)[0]);Equal(0x2475001Eu,GameRules.ById(41).Original[0]);
            var spearbane=GameRules.ById(42);Equal("2A010021,24100021",string.Join(",",spearbane.Original.Select(x=>x.ToString("X8"))));Equal("2A010032,24100032",string.Join(",",spearbane.Encode(50).Select(x=>x.ToString("X8"))));
            Equal<int?>(null,spearbane.Decode(new[]{0x2A010032u,0x24100021u}));
            Equal("0x2275F0,0x227694,0x227734,0x227550,0x2277D4",string.Join(",",new[]{42,43,44,45,46}.Select(i=>$"0x{GameRules.ById(i).Sites[0].Address:X}")));
            Equal(157,GameRules.All.Count(r=>r.Section==GameRules.SkillsSection));Equal(0x246300FFu,GameRules.ById(94).Original[0]);Equal(0x24630000u,GameRules.ById(94).Encode(1)[0]);
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
            var row=new byte[TerrainData.RowLength];row[0]=2;row[4+50]=70;var m=TerrainData.Decode(2,row);Equal("Fly/Flying/7",$"{m.Name}/{m.MountText}/{m.Cost(50)}");
        });
    }

    private static void Equal<T>(T expected, T actual, [System.Runtime.CompilerServices.CallerLineNumber] int line = 0)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"Expected {expected}, got {actual} (line {line})");
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception($"Expected {typeof(T).Name}");
    }
}
