// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace HuntersWorkshop {
    public sealed class Snapshot { public long Pgd,Player; public int Hp,Level; public bool Ready; public string Message; }

    // Attaches to shadPS4 and runs game calls (item and gem grants) on the game's main thread.
    //
    // shadPS4 executes the PS4 eboot natively, so the game's code and data live inside the
    // shadPS4.exe process, and the Bloodborne 1.09 eboot is always mapped at 0x800000000 (this is how
    // the working Bloodborne mod menu finds it too). Every offset in Commands.cs is relative to that
    // base. The image is confirmed by comparing function prologues before anything is written.
    public sealed class Engine : IDisposable {
        public const long ImageBase=0x800000000L;
        public const string TitleId="CUSA03173";
        // Bloodborne saves are stored under the save-data title id of the disc/region, which is not always the 1.09 patch id.
        public const string SaveTitles="CUSA00207|CUSA03173|CUSA00900|CUSA01363|CUSA00299";

        sealed class Fn {
            public readonly string Name; public readonly long At; public readonly byte[] Prologue;
            public Fn(string name,long at,byte[] prologue) { Name=name; At=at; Prologue=prologue; }
        }
        static readonly byte[] givePro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x68};
        static readonly byte[] createPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x28};
        static readonly byte[] releasePro={0x55,0x48,0x89,0xe5,0x41,0x56,0x53,0x48,0x89,0xfb,0x8b,0x33};
        static readonly byte[] listPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53};
        static readonly byte[] tickPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x38};
        static readonly byte[] savePro={0x55,0x48,0x89,0xe5,0x48,0x8d,0x05,0x1d,0x43,0x20,0x04,0x48,0x83,0x38,0x00,0x75};
        static readonly Fn[] required={
            new Fn("Give",Commands.Give,givePro), new Fn("Create",Commands.Create,createPro), new Fn("Tick",Commands.Tick,tickPro),
            new Fn("GiveList",Commands.GiveList,listPro), new Fn("Release",Commands.Release,releasePro), new Fn("SaveRequest",Commands.SaveRequest,savePro) };

        readonly Catalog catalog; Memory m; long image,block,data,site; byte[] original,patch;
        Semaphore sessionLock; bool ownsSession,faulted,hookInstalled; long backedUpCharacter; string lastStatus;
        readonly SemaphoreSlim actions=new SemaphoreSlim(1,1);

        public Engine(Catalog c) { catalog=c; ShadPath=Storage.Load().ShadDirectory; }
        public bool Connected { get { try { return m!=null && !m.Host.HasExited; } catch(InvalidOperationException) { return false; } } }
        public string ShadPath { get; private set; }
        public string LastBackup { get; private set; }
        public string UserPath { get; set; }
        public static void Log(string s) { Storage.Log(s); }

        static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-"," "); }
        static bool Same(Memory mem,long at,byte[] expected) { try { return mem.Read(at,expected.Length).SequenceEqual(expected); } catch(Exception) { return false; } }
        bool Match(long at,byte[] expected) { return Same(m,at,expected); }
        static bool IsGame(Memory mem,long img) { return Entry(mem,img+Commands.Give,givePro,null) && Entry(mem,img+Commands.Create,createPro,null); }

        // A function the Workshop only CALLS may carry somebody else's entry detour (a patched eboot, the
        // mod menu, a shadPS4 patch): 'jmp rel32' or 'jmp [rip+0]' over the first instruction(s), NOPs up to
        // the next instruction boundary, and the rest of the prologue untouched. Calling through such a
        // detour is safe, so it is accepted as long as the untouched tail still matches the 1.09 prologue.
        // 'detour' is set to a description when one was recognised, null for the stock bytes.
        internal static bool Accepts(long at,byte[] actual,byte[] expected,out string detour) {
            detour=null;
            if(actual.Length<expected.Length) return false;
            if(actual.Take(expected.Length).SequenceEqual(expected)) return true;
            int jump; long target;
            if(actual[0]==0xe9) { jump=5; target=at+5+BitConverter.ToInt32(actual,1); }
            else if(actual[0]==0xff && actual[1]==0x25 && actual[2]==0 && actual[3]==0 && actual[4]==0 && actual[5]==0 && expected.Length>=jump14+4) { jump=jump14; target=BitConverter.ToInt64(actual,6); }
            else return false;
            for(int n=jump;n<=expected.Length-4;n++) {
                if(n>jump && actual[n-1]!=0x90 && actual[n-1]!=0xcc) break;
                bool tail=true; for(int i=n;i<expected.Length;i++) if(actual[i]!=expected[i]) { tail=false; break; }
                if(tail) { detour="jump to 0x"+target.ToString("X")+" over the first "+n+" bytes"; return true; }
            }
            return false;
        }
        const int jump14=14;
        static bool Entry(Memory mem,long at,byte[] expected,Fn f) {
            byte[] actual; try { actual=mem.Read(at,expected.Length); } catch(Exception) { return false; }
            string detour; if(!Accepts(at,actual,expected,out detour)) return false;
            if(detour!=null && f!=null) Log("check "+f.Name+" rva=0x"+f.At.ToString("X")+" accepted: entry is detoured by another tool ("+detour+"); the rest of the prologue matches 1.09");
            return true;
        }
        // Tick is the one function the Workshop patches itself, so it must be stock (or our own stale hook).
        bool Verify(Fn f) { return f.Name=="Tick" ? Match(image+f.At,f.Prologue) : Entry(m,image+f.At,f.Prologue,f); }

        // The real shadPS4 window process. The Qt launcher alone does not host the game.
        static List<Process> FindShadProcesses() {
            var found=new List<Process>();
            foreach(Process p in Process.GetProcesses()) {
                bool keep=false;
                try { keep=p.ProcessName.StartsWith("shadPS4",StringComparison.OrdinalIgnoreCase) && p.ProcessName.IndexOf("Launcher",StringComparison.OrdinalIgnoreCase)<0; }
                catch(Exception) { }
                if(keep) found.Add(p); else p.Dispose();
            }
            return found;
        }

        // Fixed base first. The scan is only a safety net for a shadPS4 build that maps the eboot
        // somewhere else: it looks for Give's prologue in executable memory.
        static long Locate(Memory mem) {
            if(IsGame(mem,ImageBase)) return ImageBase;
            const int chunk=1024*1024; long at=0x10000;
            while(at<0x7ffffffeffff) {
                Native.Region r;
                if(Native.VirtualQueryEx(mem.Handle,new IntPtr(at),out r,(UIntPtr)48)==UIntPtr.Zero || r.Size==0) break;
                bool readable=r.State==0x1000 && (r.Protect&0x101)==0 && r.Protect!=0;
                if(readable && (r.Protect&0xf0)!=0 && r.Size>=0x10000 && r.Size<=0x80000000UL) {
                    long end=r.Base+(long)r.Size;
                    for(long off=r.Base;off<end;off+=chunk-givePro.Length) {
                        int n=(int)Math.Min(chunk,end-off); if(n<givePro.Length) break;
                        byte[] buf; try { buf=mem.Read(off,n); } catch(Exception) { break; }
                        int i=0;
                        while((i=Array.IndexOf(buf,givePro[0],i))>=0 && i<=buf.Length-givePro.Length) {
                            bool same=true; for(int k=1;k<givePro.Length;k++) if(buf[i+k]!=givePro[k]) { same=false; break; }
                            if(same) { long candidate=off+i-Commands.Give; if(candidate>0x10000 && IsGame(mem,candidate)) return candidate; }
                            i++;
                        }
                    }
                }
                long next=r.Base+(long)r.Size; if(next<=at) break; at=next;
            }
            return 0;
        }

        static string Explain(Memory mem) {
            long at=ImageBase+Commands.Give; string have;
            try { have=Hex(mem.Read(at,givePro.Length)); } catch(Exception) { have="unreadable"; }
            Log("Bloodborne not found: bytes at 0x"+at.ToString("X")+" are "+have+"; expected "+Hex(givePro));
            return have=="unreadable" ? "The game is not loaded yet. Start Bloodborne and wait for the title screen." : "The game code does not match the 1.09 eboot (see the activity log).";
        }

        void Dump(Fn f) {
            try { Log("check "+f.Name+" rva=0x"+f.At.ToString("X")+" FAILED\n   expected "+Hex(f.Prologue)+"\n   actual   "+Hex(m.Read(image+f.At,f.Prologue.Length))); }
            catch(Exception ex) { Log("check "+f.Name+" unreadable: "+ex.Message); }
        }

        // A Workshop that was killed instead of disconnected leaves its jump at Tick. When that jump
        // provably leads to one of our own bridges, put the original prologue back so the next
        // session starts clean instead of demanding a game restart.
        void RemoveStaleHook() {
            long at=image+Commands.Tick; byte[] now;
            try { now=m.Read(at,tickPro.Length); } catch(Exception) { return; }
            if(now.SequenceEqual(tickPro)) return;
            if(now[0]!=0xff || now[1]!=0x25 || now[2]!=0 || now[3]!=0 || now[4]!=0 || now[5]!=0) return;
            for(int i=14;i<now.Length;i++) if(now[i]!=0x90) return;
            long target=BitConverter.ToInt64(now,6);
            int length=Commands.Bridge(image,0,at+tickPro.Length,tickPro).Length;
            var tail=new List<byte>(tickPro); tail.AddRange(new byte[]{0xff,0x25,0,0,0,0}); tail.AddRange(BitConverter.GetBytes(at+tickPro.Length));
            byte[] have;
            try { have=m.Read(target+length-tail.Count,tail.Count); } catch(Exception) { return; }
            if(!have.SequenceEqual(tail)) return;
            m.PausedPatch(at,now,tickPro);
            Log("Removed the command hook left behind by an earlier Workshop session.");
        }

        public void Connect() {
            if(Connected) return;
            Dispose();
            var hosts=new List<KeyValuePair<int,long>>(); bool denied=false; string why="";
            var emulators=FindShadProcesses(); int running=emulators.Count;
            try {
                foreach(Process e in emulators) {
                    try {
                        using(var probe=new Memory(Process.GetProcessById(e.Id))) {
                            long found=Locate(probe);
                            if(found!=0) hosts.Add(new KeyValuePair<int,long>(e.Id,found)); else why=Explain(probe);
                        }
                    }
                    catch(Win32Exception ex) { Log("Cannot open shadPS4 PID "+e.Id+": "+ex.Message); denied=true; }
                    catch(ArgumentException) { }
                }
            } finally { foreach(Process e in emulators) e.Dispose(); }
            if(running==0) throw new InvalidOperationException("Start shadPS4 (shadPS4.exe, not just the launcher) with Bloodborne, then connect.");
            if(hosts.Count==0) throw new InvalidOperationException(denied
                ? "Windows would not let the Workshop open shadPS4. Run both as the same user, or run the Workshop as administrator if shadPS4 is elevated."
                : "shadPS4 is running but Bloodborne CUSA03173 1.09 was not found. "+why);
            if(hosts.Count>1) throw new InvalidOperationException("Several shadPS4 instances are running Bloodborne. Close the extra instance first.");

            int pid=hosts[0].Key; Process p=Process.GetProcessById(pid);
            string exe=null; try { exe=p.MainModule.FileName; } catch(Exception) { }
            if(exe!=null) { ShadPath=Path.GetDirectoryName(exe); UserPath=ShadUserDirectory(ShadPath) ?? UserPath; }
            sessionLock=new Semaphore(1,1,"Local\\BloodborneHuntersWorkshop-"+pid);
            ownsSession=sessionLock.WaitOne(0);
            if(!ownsSession) { sessionLock.Dispose(); sessionLock=null; p.Dispose(); throw new InvalidOperationException("Another Hunter's Workshop is already connected."); }
            try {
                m=new Memory(p); image=hosts[0].Value;
                RemoveStaleHook();
                var bad=required.Where(f=>!Verify(f)).ToList();
                if(bad.Count>0) {
                    foreach(Fn f in bad) Dump(f);
                    if(bad.Any(f=>f.Name=="Tick")) throw new InvalidOperationException("Another tool already hooks the game's per-frame Tick function, and the Workshop cannot share that one. Close the other tool or restart the game. Details are in the activity log.");
                    throw new InvalidOperationException("These game functions do not match Bloodborne 1.09: "+String.Join(", ",bad.Select(f=>f.Name))+". Only entry detours on the called functions are tolerated; anything else in the code must be the stock CUSA03173 1.09 eboot. Details are in the activity log.");
                }
                site=image+Commands.Tick; original=m.Read(site,tickPro.Length);
                block=m.Allocate(); data=block+4096;
                byte[] bridge=Commands.Bridge(image,data,site+original.Length,original); if(bridge.Length>4096) throw new InvalidOperationException("Command bridge exceeds its code page.");
                m.Write(block,bridge); uint old;
                if(!Native.VirtualProtectEx(m.Handle,new IntPtr(block),(UIntPtr)4096,0x20,out old) || !Native.FlushInstructionCache(m.Handle,new IntPtr(block),(UIntPtr)4096)) throw Native.Error("Cannot prepare command bridge");
                patch=Enumerable.Repeat((byte)0x90,original.Length).ToArray();
                patch[0]=0xff;patch[1]=0x25;for(int i=2;i<6;i++)patch[i]=0;
                Buffer.BlockCopy(BitConverter.GetBytes(block),0,patch,6,8);
                m.PausedPatch(site,original,patch); hookInstalled=true; faulted=false;
                Log("Connected shadPS4 PID "+pid+" image 0x"+image.ToString("X")+"; main-thread bridge at 0x"+block.ToString("X")+" installed.");
            } catch { Dispose(); throw; }
        }

        Snapshot Say(Snapshot s,string message,string detail=null) {
            s.Message=message;
            string key=s.Ready?"ready":message;
            if(key!=lastStatus) { lastStatus=key; Log("status: "+message+(detail==null?"":" ["+detail+"]")); }
            return s;
        }
        public Snapshot Inspect() {
            var s=new Snapshot();
            if(!Connected) return Say(s,"Not connected");
            if(faulted) return Say(s,"Command outcome uncertain — restart Bloodborne");
            try {
                long g=m.Q(image+Commands.Gdm),w=m.Q(image+Commands.World),f=m.Q(image+Commands.Flow);
                if(g==0 || w==0 || f==0) return Say(s,"Load a character","gdm/world/flow="+g.ToString("X")+"/"+w.ToString("X")+"/"+f.ToString("X"));
                s.Pgd=m.Q(g+8); s.Player=m.Q(w+0x60);
                if(s.Pgd==0 || s.Player==0) return Say(s,"Load a character");
                int loading=m.Read(image+Commands.Loading,1)[0];
                if(loading!=0) return Say(s,"Waiting for loading to finish","loading="+loading);
                int ownWorld=m.Read(f+0x1592,1)[0];
                if(ownWorld!=1) return Say(s,"Return to your own world before granting items","flow+0x1592="+ownWorld);
                long modules=m.Q(s.Player+0x3b0); long stats=modules==0?0:m.Q(modules+0x20);
                s.Hp=stats==0?0:m.I(stats+0xf8); s.Level=m.I(s.Pgd+0x90);
                if(s.Hp<=0) return Say(s,"Waiting for a living character","hp="+s.Hp);
                s.Ready=true; return Say(s,"Hunter ready · level "+s.Level+" · HP "+s.Hp);
            } catch(Exception ex) { return Say(s,"Player data is unavailable — wait for loading",ex.Message); }
        }
        public List<int[]> Inventory(Snapshot s) {
            long inv=s.Pgd+0x328; int split=m.I(inv+0x24),last=m.I(inv+0x88);
            if(split<0 || split>8192 || last< -1 || last>8192) throw new InvalidOperationException("Unknown inventory layout.");
            long first=m.Q(inv+0x58),second=m.Q(inv+0x48); var rows=new List<int[]>();
            for(int i=0;i<=last;i++) {
                long at=i<split?first+16L*i:second+16L*(i-split);
                byte[] b=m.Read(at,16); int handle=BitConverter.ToInt32(b,0),id=BitConverter.ToInt32(b,4),n=BitConverter.ToInt32(b,8);
                if(handle!=0 && n>0) rows.Add(new[]{handle,id,n});
            }
            return rows;
        }
        public async Task<string> GiveItem(Item item,int upgrade,int quantity) {
            catalog.ValidateItem(item,upgrade,quantity);
            return await Execute(0,item.Id+100*upgrade,item.Category,quantity,0,0,null,item.Name);
        }
        public async Task<string> GiveGem(bool rune,int shape,int source,int[] effects) {
            effects=effects==null?null:(int[])effects.Clone();
            catalog.ValidateGem(rune,shape,source,effects);
            return await Execute(1,source,8,1,rune?2:1,shape,effects,rune?"Caryll rune":"Blood gem");
        }
        internal int[] GemFields(int handle) {
            long manager=m.Q(image+Commands.Gaitem);
            if(manager==0) throw new InvalidOperationException("Gem manager is unavailable.");
            long instance=m.Q(manager+8+8L*(unchecked((uint)handle)&0xffff));
            if(instance==0 || m.I(instance+8)!=handle) throw new InvalidOperationException("The gem instance changed.");
            byte[] fields=m.Read(instance+0x18,32);
            return Enumerable.Range(0,8).Select(i=>BitConverter.ToInt32(fields,i*4)).ToArray();
        }
        async Task<string> Execute(int kind,int id,int category,int count,int flags,int shape,int[] effects,string name) {
            if(!await actions.WaitAsync(0)) throw new InvalidOperationException("Wait for the current grant to finish.");
            try {
                Snapshot s=Inspect(); if(!s.Ready) throw new InvalidOperationException(s.Message);
                int state=m.I(data); if(state==1 || state==2) throw new InvalidOperationException("A game command is still pending.");
                // Take a complete verified snapshot once per session before the first persistent action.
                if(LastBackup==null || backedUpCharacter!=s.Pgd) {
                    try { LastBackup=Backup(); } catch(IOException ex) { throw new IOException("Nothing was granted: the safety backup of your save could not be made. "+ex.Message,ex); }
                    backedUpCharacter=s.Pgd;
                }
                var before=Inventory(s); int fullId=id|(category<<28);
                long oldCount=before.Where(x=>x[1]==fullId).Sum(x=>(long)x[2]);
                byte[] packet=Commands.Packet(s.Pgd,s.Player,kind,id,category,count,flags,shape,effects);
                m.Write(data,packet); m.Write(data,BitConverter.GetBytes(1));
                var timer=Stopwatch.StartNew();
                while(timer.ElapsedMilliseconds<8000 && m.I(data)!=3) await Task.Delay(25);
                if(m.I(data)!=3) {
                    // Cancel an unclaimed packet while all game threads are suspended; never
                    // overwrite an executing status, retry, or free executable memory.
                    bool cancelled=m.CancelQueued(data);
                    Log("TIMEOUT id="+id+" cancelled-before-execution="+cancelled);
                    // Unclaimed means the game never started it, so the outcome is certain. This is what
                    // happens while shadPS4 is paused (F9) or the game is stalled on a loading screen.
                    if(cancelled) throw new InvalidOperationException("The game did not pick up the command within 8 seconds, so nothing was granted. If shadPS4 is paused, resume the game and try again.");
                    faulted=true;
                    throw new InvalidOperationException("The game did not confirm the command. Its outcome is uncertain. Restart Bloodborne and check your inventory before repeating it.");
                }
                int result=m.I(data+4);
                if(result!=1) throw new InvalidOperationException(result==2?"Grant cancelled: the character changed, died, or started loading.":"The game could not create the gem; nothing was granted.");
                Snapshot after=Inspect();
                if(!after.Ready || after.Pgd!=s.Pgd || after.Player!=s.Player) { faulted=true; throw new InvalidOperationException("The call completed while the character changed. Restart the game and check your inventory before repeating it."); }
                var now=Inventory(after); long gained=now.Where(x=>x[1]==fullId).Sum(x=>(long)x[2])-oldCount;
                string message;
                if(kind==1) {
                    var added=now.Where(x=>x[1]==fullId && !before.Any(b=>b[0]==x[0])).ToArray();
                    if(added.Length==0) throw new InvalidOperationException("The gem call completed but no new inventory instance was found. Check storage and inventory before repeating it.");
                    int[] check=GemFields(added[0][0]);
                    if(check[0]!=flags || check[1]!=shape || Enumerable.Range(0,6).Any(i=>check[i+2]!=effects[i]))
                        throw new InvalidOperationException("The created gem did not match its requested effects. Check inventory before repeating it.");
                    message=name+" added — shape and all six effects verified in inventory.";
                } else message=gained>0?name+" ×"+gained+" added to inventory.":"The grant ran; inventory count did not increase. The item may already be at its limit or in storage.";
                Log(message+" id="+id+" category="+category+" count="+count+(effects==null?"":" effects="+String.Join(",",effects)));
                return message;
            } finally { actions.Release(); }
        }

        // shadPS4 keeps PS4 saves under <user>\savedata\<userId>\CUSA03173. A portable install
        // uses a "user" folder beside the executable; otherwise it lives in %APPDATA%\shadPS4.
        public static string ShadUserDirectory(string emulatorFolder) {
            var options=new List<string>();
            if(!String.IsNullOrWhiteSpace(emulatorFolder)) {
                string trimmed=emulatorFolder.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
                options.Add(Path.Combine(trimmed,"user"));
                string parent=null; try { parent=Path.GetDirectoryName(trimmed); } catch(ArgumentException) { }
                if(!String.IsNullOrEmpty(parent)) options.Add(Path.Combine(parent,"user"));
            }
            options.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"shadPS4","user"));
            foreach(string o in options) if(Directory.Exists(Path.Combine(o,"savedata"))) return Path.GetFullPath(o);
            // Custom layouts (bundled or renamed folders): look a few levels around the emulator for the
            // folder that holds savedata\<user>\CUSA03173.
            if(!String.IsNullOrWhiteSpace(emulatorFolder)) {
                string trimmed=emulatorFolder.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar), parent=null;
                try { parent=Path.GetDirectoryName(trimmed); } catch(ArgumentException) { }
                foreach(string root in new[]{trimmed,parent,Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"shadPS4")}) {
                    if(String.IsNullOrEmpty(root)) continue;
                    string found=FindSaveRoot(root,3); if(found!=null) { Log("Found the Bloodborne save folder at "+found); return Path.GetFullPath(found); }
                }
            }
            return null;
        }
        static string FindSaveRoot(string dir,int depth) {
            try {
                if((File.GetAttributes(dir)&FileAttributes.ReparsePoint)!=0) return null;
                string saves=Path.Combine(dir,"savedata");
                if(Directory.Exists(saves)) {
                    foreach(string t in SaveTitles.Split('|')) {
                        if(Directory.Exists(Path.Combine(saves,t))) return dir;
                        foreach(string u in Directory.GetDirectories(saves)) if(Directory.Exists(Path.Combine(u,t))) return dir;
                    }
                }
                if(depth<=0) return null;
                foreach(string sub in Directory.GetDirectories(dir)) { string r=FindSaveRoot(sub,depth-1); if(r!=null) return r; }
            } catch(IOException) { } catch(UnauthorizedAccessException) { }
            return null;
        }
        public void SetFolder(string directory) {
            if(Connected) throw new InvalidOperationException("Disconnect before changing the shadPS4 folder.");
            if(String.IsNullOrWhiteSpace(directory) || !(File.Exists(Path.Combine(directory,"shadPS4.exe")) || File.Exists(Path.Combine(directory,"shadPS4QtLauncher.exe"))))
                throw new IOException("Select the shadPS4 folder (the one containing shadPS4.exe).");
            ShadPath=Path.GetFullPath(directory); UserPath=ShadUserDirectory(ShadPath);
        }
        public static bool EmulatorRunning() {
            var found=FindShadProcesses();
            try { return found.Count>0; } finally { foreach(Process p in found) p.Dispose(); }
        }
        // Puts a snapshot back after backing up the current saves. The game must be closed: a running
        // shadPS4 keeps its own copy of the save in memory and would write it over the restored files.
        public string RestoreSnapshot(SnapshotInfo snapshot) {
            if(Connected) throw new InvalidOperationException("Disconnect from the game first, then close shadPS4 completely before restoring.");
            if(EmulatorRunning()) throw new InvalidOperationException("Close shadPS4 completely before restoring a snapshot. A running game would write its own save over the restored files.");
            string user=UserPath;
            if(String.IsNullOrWhiteSpace(user)) {
                if(String.IsNullOrWhiteSpace(ShadPath)) throw new IOException("Choose your shadPS4 folder in Session first.");
                user=ShadUserDirectory(ShadPath) ?? Path.Combine(ShadPath,"user");
            }
            return SaveBackup.Restore(snapshot,user,Storage.Backups,SaveTitles);
        }
        public string Backup() {
            string user=UserPath;
            if(String.IsNullOrWhiteSpace(user)) {
                if(String.IsNullOrWhiteSpace(ShadPath)) throw new IOException("Choose your shadPS4 folder in Session, or connect to the game first.");
                user=ShadUserDirectory(ShadPath) ?? Path.Combine(ShadPath,"user");
            }
            return SaveBackup.Create(user,Storage.Backups,SaveTitles);
        }
        public void Dispose() {
            if(actions.CurrentCount==0) throw new InvalidOperationException("Wait for the current grant to finish before disconnecting.");
            if(m!=null) {
                bool exited=m.Host.HasExited;
                if(!exited && hookInstalled) {
                    if(!Match(site,patch)) throw new InvalidOperationException("Another tool changed the command hook. Close Bloodborne before disconnecting.");
                    // Keep the connection available for retry if safe removal fails.
                    m.PausedPatch(site,patch,original);
                    hookInstalled=false;
                }
                if(!exited && !hookInstalled && block!=0 && patch==null) m.Free(block);
                m.Dispose(); m=null; patch=null; original=null; block=0; data=0; site=0; image=0;
                hookInstalled=false; faulted=false; LastBackup=null; backedUpCharacter=0; lastStatus=null;
            }
            if(ownsSession) { sessionLock.Release(); ownsSession=false; }
            if(sessionLock!=null) { sessionLock.Dispose(); sessionLock=null; }
        }
    }
}
