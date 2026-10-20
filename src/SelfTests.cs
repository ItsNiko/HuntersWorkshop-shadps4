// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HuntersWorkshop {
    public static class SelfTests {
        static int count;
        static void Check(bool ok,string message) { if(!ok)throw new Exception("Test failed: "+message);count++; }
        static void Reject(Action f,string message) { try { f(); }catch(InvalidOperationException){count++;return;}throw new Exception("Expected rejection: "+message); }
        public static int Run(string dir) {
            Directory.CreateDirectory(dir);var c=Catalog.Read<Catalog>("catalog.json");
            Check(c.Items.Count(x=>x.Known)>390,"named items loaded");Check(c.Effects.Count(x=>x.Known)>12000,"gem effects loaded");
            Check(c.Items.Any(x=>x.Name=="Lost Chikage"),"chalice Lost weapon");Check(c.Items.Any(x=>x.Name=="Uncanny Rakuyo"),"DLC chalice weapon");
            Check(c.Items.Count(x=>x.Known && x.Variant=="Lost")==26,"all Lost variants, including names with apostrophes");
            Check(c.Items.Count(x=>x.Known && x.Variant=="Uncanny")==26,"all Uncanny variants, including names with apostrophes");
            Check(c.Items.Any(x=>x.Name=="Blood Rock"),"upgrade material");Check(c.Items.Any(x=>x.Group=="Items" && x.Name.Contains("Root Chalice")),"root chalices");
            var chikage=c.Items.First(x=>x.Name=="Lost Chikage");c.ValidateItem(chikage,10,1);count++;
            Reject(()=>c.ValidateItem(chikage,11,1),"reinforcement");Reject(()=>c.ValidateItem(chikage,0,2),"weapon quantity");
            var gem=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText.Contains("27.2"));var rune=c.Effects.First(x=>x.Rune);
            Check(c.Gems.Any(x=>x.Id==90150 && (x.Flags&1)!=0),"default gem source");
            int[] effects={gem.Id,-1,-1,-1,-1,-1}; foreach(int shape in new[]{1,2,4,8,63}){c.ValidateGem(false,shape,90150,effects);count++;}
            Reject(()=>c.ValidateGem(false,3,90150,effects),"shape");Reject(()=>c.ValidateGem(false,1,99999999,effects),"source");
            Reject(()=>c.ValidateGem(false,1,90150,new[]{99999999,-1,-1,-1,-1,-1}),"unknown effect");
            c.ValidateGem(true,2,c.Gems.First(x=>(x.Flags&2)!=0).Id,new[]{rune.Id,-1,-1,-1,-1,-1});count++;
            byte[] packet=Commands.Packet(0x400000,0x500000,1,90150,8,1,1,63,effects);
            Check(packet.Length==128 && BitConverter.ToInt64(packet,24)==0x400000,"packet identity");
            Check(BitConverter.ToInt32(packet,60)==gem.Id && BitConverter.ToInt32(packet,80)==-1,"six effects");
            byte[] code=Commands.Bridge(0x10000000,0x30001000,0x10010000,new byte[0]);
            Check(code.Length<4096,"bridge bound");File.WriteAllBytes(Path.Combine(dir,"bridge.bin"),code);File.WriteAllBytes(Path.Combine(dir,"gem-packet.bin"),packet);
            StorageTests(Path.GetFullPath(dir));
            File.WriteAllText(Path.Combine(dir,"self-tests.txt"),count+" checks passed; "+c.Items.Length+" catalogue entries, "+c.Effects.Length+" effect rows."+Environment.NewLine);
            return 0;
        }
        static void StorageTests(string dir) {
            string old=Environment.GetEnvironmentVariable("HUNTERS_WORKSHOP_DATA");
            string root=Path.Combine(dir,"storage-"+Guid.NewGuid().ToString("N"));
            try {
                Environment.SetEnvironmentVariable("HUNTERS_WORKSHOP_DATA",Path.Combine(root,"settings"));
                Check(Storage.Load().Favorites.Count==0,"missing preferences use defaults");
                var p=new Preferences { PortDirectory="C:\\Port with spaces" };
                p.Favorites.Add("0:2020000");Storage.Save(p);
                Check(Storage.Load().PortDirectory==p.PortDirectory && Storage.Load().Favorites.SequenceEqual(p.Favorites),"preferences round-trip");
                p.Favorites.Add("4:1000");Storage.Save(p);
                Check(Storage.Load().Favorites.Count==2,"atomic preferences replacement");
                File.WriteAllText(Path.Combine(Storage.Root,"preferences.json"),"{broken");
                Check(Storage.Load().Favorites.Count==1,"corrupt primary recovers previous preferences");
                Storage.AtomicWrite(Path.Combine(Storage.Root,"preferences.json"),"{\"Favorites\":null,\"Recipes\":[null,{\"Effects\":[1]}]}");
                Check(Storage.Load().Favorites.Count==0 && Storage.Load().Recipes.Count==0,"null preferences and malformed recipes normalized");
                Check(!Directory.GetFiles(Storage.Root,"*.tmp").Any(),"atomic writes leave no temporary files");
                var args=ProcessLaunch.Parse("\"C:\\Port with spaces\\bbport.exe\" boot.bin --user \"C:\\My Saves\\hunter\" --timeout 180");
                Check(args.Length==6 && args[3]=="C:\\My Saves\\hunter","quoted Windows launch arguments");
                string user=Path.Combine(root,"game-user"), backups=Path.Combine(root,"backups");
                Directory.CreateDirectory(Path.Combine(user,"savedata","characters"));
                Directory.CreateDirectory(Path.Combine(user,"savedata_party"));
                File.WriteAllText(Path.Combine(user,"savedata","characters","one.dat"),"solo save fixture");
                File.WriteAllText(Path.Combine(user,"savedata_party","party.dat"),"party save fixture");
                File.WriteAllText(Path.Combine(user,"savedata","incomplete.bbtmp"),"temporary incomplete save");
                string first=SaveBackup.Create(user,backups);
                Check(Directory.Exists(first) && !first.EndsWith(".partial"),"completed backup published");
                Check(File.ReadAllText(Path.Combine(first,"savedata","characters","one.dat"))=="solo save fixture","nested solo save copied");
                Check(File.ReadAllText(Path.Combine(first,"savedata_party","party.dat"))=="party save fixture","party save copied");
                Check(!File.Exists(Path.Combine(first,"savedata","incomplete.bbtmp")),"unfinished save excluded");
                Check(File.ReadAllText(Path.Combine(first,"manifest.json")).Contains(Storage.Hash(Path.Combine(user,"savedata_party","party.dat"))),"backup manifest contains SHA-256");
                Check(SaveBackup.ListFiles(user).Count()==2,"complete save set enumerated");
                string second=SaveBackup.Create(user,backups);
                Check(first!=second && Directory.Exists(first),"successive snapshots preserve earlier recovery point");
                bool rejected=false;try { SaveBackup.Create(user,Path.Combine(user,"nested")); } catch(IOException) { rejected=true; }
                Check(rejected,"backup destination inside saves rejected");
                string empty=Path.Combine(root,"empty");Directory.CreateDirectory(empty);rejected=false;
                try { SaveBackup.Create(empty,backups); } catch(IOException) { rejected=true; }
                Check(rejected,"empty source rejected");
                Check(!Directory.GetDirectories(backups,"*.partial").Any(),"no partial backup published");
                // Make application data unwritable without requiring administrator ACL changes.
                string blocked=Path.Combine(root,"blocked");File.WriteAllText(blocked,"file occupies directory");
                Environment.SetEnvironmentVariable("HUNTERS_WORKSHOP_DATA",blocked);
                Storage.Log("must not throw");count++;
            } finally { Environment.SetEnvironmentVariable("HUNTERS_WORKSHOP_DATA",old); }
        }
        // Deliberate development fixture: --probe PID OUTPUT [--give-test]. It never launches
        // a game or selects a process by assumption; use only with an isolated copy of saves.
        public static int Probe(string[] args) {
            int pid=Int32.Parse(args[1]);string path=args[2];var c=Catalog.Read<Catalog>("catalog.json");
            using(var e=new Engine(Catalog.Read<Profile>("profile.json"),c)) {
                e.Connect(pid);var state=e.Inspect();
                if(args.Contains("--wait-ready")) {
                    var end=DateTime.UtcNow.AddSeconds(90);
                    while(!state.Ready && DateTime.UtcNow<end) { System.Threading.Thread.Sleep(500);state=e.Inspect(); }
                }
                File.WriteAllText(path,new JavaScriptSerializer().Serialize(new {State=state,Inventory=state.Ready?e.Inventory(state):null}));
                if(args.Contains("--verify-existing")) {
                    var inv=e.Inventory(state);
                    Check(inv.Any(x=>x[1]==2021000),"reloaded +10 Lost Chikage");
                    Check(inv.Any(x=>x[1]==unchecked((int)(0x80000000u|90150))),"reloaded blood gem");
                    File.AppendAllText(path,Environment.NewLine+"Reload verified: +10 Lost Chikage and gem present in inventory.");
                    if(args.Contains("--verify-expanded")) {
                        Check(inv.Any(x=>x[1]==c.Items.First(item=>item.Name=="Uncanny Rakuyo").Id+1000),"reloaded +10 Uncanny Rakuyo");
                        var fields=inv.Where(x=>x[1]==unchecked((int)(0x80000000u|90150))).Select(x=>e.GemFields(x[0])).ToArray();
                        var primary=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText=="Physical ATK UP +27.2%");
                        var secondary=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText=="Add physical ATK +15");
                        var curse=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText.StartsWith("Increases stamina costs"));
                        foreach(int shape in new[]{1,2,4,8,63})
                            Check(fields.Any(f=>f[0]==1 && f[1]==shape && f[2]==primary.Id && f[3]==secondary.Id && f[4]==curse.Id && f.Skip(5).All(n=>n==-1)),"reloaded exact shape and effects "+shape);
                        Check(inv.Any(x=>x[1]==unchecked((int)(0x80000000u|6)) && e.GemFields(x[0])[0]==2),"reloaded Caryll rune");
                        File.AppendAllText(path,Environment.NewLine+"Expanded reload verified: +10 Uncanny Rakuyo, five gem shapes with all requested effects, and Caryll rune.");
                    }
                }
                if(args.Contains("--give-test")) {
                    if(!state.Ready)throw new InvalidOperationException(state.Message);
                    string first=e.GiveItem(c.Items.First(x=>x.Name=="Pebble"),0,1).GetAwaiter().GetResult();
                    string second=e.GiveItem(c.Items.First(x=>x.Name=="Lost Chikage"),10,1).GetAwaiter().GetResult();
                    var effect=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText=="Physical ATK UP +27.2%");
                    string third=e.GiveGem(false,63,90150,new[]{effect.Id,-1,-1,-1,-1,-1}).GetAwaiter().GetResult();
                    File.AppendAllText(path,Environment.NewLine+first+Environment.NewLine+second+Environment.NewLine+third);
                }
                if(args.Contains("--give-expanded")) {
                    var inv=e.Inventory(state);
                    var item=c.Items.First(x=>x.Known && x.Category==4 && x.Stack>1 && x.Name=="Fire Paper");
                    File.AppendAllText(path,Environment.NewLine+e.GiveItem(item,0,1).GetAwaiter().GetResult());
                    File.AppendAllText(path,Environment.NewLine+e.GiveItem(c.Items.First(x=>x.Name=="Uncanny Rakuyo"),10,1).GetAwaiter().GetResult());
                    var primary=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText=="Physical ATK UP +27.2%");
                    var secondary=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText=="Add physical ATK +15");
                    var curse=c.Effects.First(x=>x.Known && !x.Rune && x.EffectText.StartsWith("Increases stamina costs"));
                    foreach(int shape in new[]{1,2,4,8,63})
                        File.AppendAllText(path,Environment.NewLine+"Shape "+shape+": "+e.GiveGem(false,shape,90150,new[]{primary.Id,secondary.Id,curse.Id,-1,-1,-1}).GetAwaiter().GetResult());
                    var rune=c.Effects.First(x=>x.Rune && x.Name=="Moon");
                    File.AppendAllText(path,Environment.NewLine+e.GiveGem(true,1,c.Gems.First(x=>(x.Flags&2)!=0).Id,new[]{rune.Id,-1,-1,-1,-1,-1}).GetAwaiter().GetResult());
                }
            }
            return 0;
        }
    }
}
