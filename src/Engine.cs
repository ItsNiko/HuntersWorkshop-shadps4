// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace HuntersWorkshop {
    public sealed class Snapshot { public long Pgd,Player; public int Hp,Level; public bool Ready; public string Message; }
    public sealed class Engine : IDisposable {
        readonly Profile profile; readonly Catalog catalog; Memory m; long image,block,data,site; byte[] original,patch;
        Semaphore sessionLock; bool ownsSession,faulted,hookInstalled; long backedUpCharacter; readonly SemaphoreSlim actions=new SemaphoreSlim(1,1);
        public bool Connected { get { try { return m!=null && !m.Process.HasExited; } catch(InvalidOperationException) { return false; } } }
        public int Pid { get { return Connected?m.Process.Id:0; } }
        public string PortPath { get; private set; }
        public string LastBackup { get; private set; }
        public string UserPath { get; set; }
        // True when attached to a shadPS4 emulator process instead of the native bbport build.
        public bool IsShadPs4 { get; private set; }
        public string TargetName { get { return IsShadPs4 ? "shadPS4" : "bbport"; } }
        public const string ShadTitleId = "CUSA03173";
        static readonly string[] ShadProcessNames = { "shadPS4", "shadPS4QtLauncher" };
        static readonly byte[] givePro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x68};
        static readonly byte[] createPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x28};
        static readonly byte[] releasePro={0x55,0x48,0x89,0xe5,0x41,0x56,0x53,0x48,0x89,0xfb,0x8b,0x33};
        static readonly byte[] listPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53};
        static readonly byte[] tickPro={0x55,0x48,0x89,0xe5,0x41,0x57,0x41,0x56,0x41,0x55,0x41,0x54,0x53,0x48,0x83,0xec,0x38};
        static readonly byte[] savePro={0x55,0x48,0x89,0xe5,0x48,0x8d,0x05,0x1d,0x43,0x20,0x04,0x48,0x83,0x38,0x00,0x75};
        public Engine(Profile p,Catalog c) { profile=p; catalog=c; PortPath=Storage.Load().PortDirectory; }
        public static string Hash(string file) { using(var s=File.OpenRead(file)) using(var h=SHA256.Create()) return BitConverter.ToString(h.ComputeHash(s)).Replace("-","").ToLowerInvariant(); }
        public static string BaseDir { get { return AppDomain.CurrentDomain.BaseDirectory; } }
        public static void Log(string s) { Storage.Log(s); }
        bool Match(long at,byte[] expected) { try { return m.Read(at,expected.Length).SequenceEqual(expected); } catch { return false; } }
        // Accept only this byte-verified port's own prehandler trampoline, including its exact
        // displaced prologue and branch back. Arbitrary third-party detours are refused.
        bool VerifiedFunction(long at,byte[] prologue) {
            if(Match(at,prologue)) return true;
            try {
                byte[] entry=m.Read(at,5); if(entry[0]!=0xe9) return false;
                long target=at+5+BitConverter.ToInt32(entry,1);
                if(Math.Abs(target-at)>0x70000000) return false;
                byte[] b=m.Read(target,700);
                for(int i=0;i<b.Length-prologue.Length-5;i++) {
                    bool same=true; for(int k=0;k<prologue.Length;k++) if(b[i+k]!=prologue[k]) { same=false; break; }
                    int j=i+prologue.Length;
                    if(same && b[j]==0xe9 && target+j+5+BitConverter.ToInt32(b,j+1)==at+prologue.Length) return true;
                }
            } catch { }
            return false;
        }
        // shadPS4 maps the guest address space itself (not as a private image allocation), so
        // locate the eboot by finding the verified Give prologue and checking Create at its fixed
        // distance. Both functions come from the same decrypted 1.09 executable.
        long scanBytes,scanHits,scanRegions,scanReadFails;
        long ScanForImage(bool executableOnly) {
            scanBytes=0; scanHits=0; scanRegions=0; scanReadFails=0;
            long at=0x10000; byte[] needle=givePro; const int chunk=1024*1024;
            while(at<0x7ffffffeffff) {
                Native.Region r;
                if(Native.VirtualQueryEx(m.Handle,new IntPtr(at),out r,(UIntPtr)48)==UIntPtr.Zero || r.Size==0) break;
                bool readable=r.State==0x1000 && (r.Protect&0x101)==0 && r.Protect!=0;
                bool exec=(r.Protect&0xf0)!=0;
                if(readable && (exec || !executableOnly) && r.Size>=0x10000 && r.Size<=(executableOnly?0x80000000UL:0x10000000UL)) {
                    long start=r.Base,end=r.Base+(long)r.Size; scanRegions++;
                    for(long off=start;off<end;off+=chunk-needle.Length) {
                        int n=(int)Math.Min(chunk,end-off); if(n<needle.Length) break;
                        byte[] buf; try { buf=m.Read(off,n); } catch { scanReadFails++; break; }
                        scanBytes+=n;
                        int i=0;
                        while((i=Array.IndexOf(buf,needle[0],i))>=0 && i<=buf.Length-needle.Length) {
                            bool same=true; for(int k=1;k<needle.Length;k++) if(buf[i+k]!=needle[k]) { same=false; break; }
                            if(same) {
                                scanHits++; long candidate=off+i-Commands.Give;
                                if(candidate>0x10000 && Match(candidate+Commands.Create,createPro)) return candidate;
                            }
                            i++;
                        }
                    }
                }
                long next=r.Base+(long)r.Size; if(next<=at) break; at=next;
            }
            return 0;
        }
        long FindShadImage() {
            var clock=Stopwatch.StartNew();
            long found=ScanForImage(true);
            Log("shadPS4 scan pass 1 (executable): found="+(found!=0)+" regions="+scanRegions+" MB="+(scanBytes>>20)+" functionHits="+scanHits+" readFailures="+scanReadFails+" ms="+clock.ElapsedMilliseconds);
            if(found==0) { found=ScanForImage(false); Log("shadPS4 scan pass 2 (all memory <=256MB regions): found="+(found!=0)+" regions="+scanRegions+" MB="+(scanBytes>>20)+" functionHits="+scanHits+" readFailures="+scanReadFails+" ms="+clock.ElapsedMilliseconds); }
            if(found==0) Log("shadPS4 scan: no match. regions="+scanRegions+" MB="+(scanBytes>>20)+" givePrologueHits="+scanHits+" readFailures="+scanReadFails);
            if(found==0) throw new InvalidOperationException("Bloodborne CUSA03173 1.09 was not found in shadPS4 (scanned "+(scanBytes>>20)+" MB, "+scanHits+" function hits). Start the game, wait for the title screen, and try again. Only CUSA03173 v01.09 is supported.");
            return found;
        }
        long FindImage() {
            long at=0x10000; var seen=new HashSet<long>();
            while(at<0x7ffffffeffff) {
                Native.Region r;
                if(Native.VirtualQueryEx(m.Handle,new IntPtr(at),out r,(UIntPtr)48)==UIntPtr.Zero || r.Size==0) break;
                if(r.State==0x1000 && r.Type==0x20000 && (r.Protect&0xf0)!=0 && r.AllocationBase>0x10000 && seen.Add(r.AllocationBase)) {
                    long candidate=r.AllocationBase;
                    if(Match(candidate+Commands.Give,givePro) && Match(candidate+Commands.Create,createPro)) return candidate;
                }
                long next=r.Base+(long)r.Size; if(next<=at) break; at=next;
            }
            throw new InvalidOperationException("Bloodborne's 1.09 image is not ready or does not match. Finish starting the game and try again.");
        }
        public void Connect(int specificPid=0) {
            if(Connected) return;
            Dispose();
            var candidates=Process.GetProcessesByName("bbport").Where(x=>specificPid==0 || x.Id==specificPid).ToArray();
            Process p; string exe; IsShadPs4=false;
            if(candidates.Length==0) {
                // No native port is running: look for a shadPS4 emulator hosting the game.
                var emu=ShadProcessNames.SelectMany(n=>Process.GetProcessesByName(n)).Where(x=>specificPid==0 || x.Id==specificPid).ToArray();
                if(emu.Length==0) throw new InvalidOperationException("Start Bloodborne (native port or shadPS4), then connect.");
                var hosts=new List<Process>(); bool denied=false; string diag="";
                foreach(var e in emu) {
                    try {
                        using(var probe=new Memory(Process.GetProcessById(e.Id))) { var saved=m; m=probe; try { if(FindShadImage()!=0) hosts.Add(e); } catch(InvalidOperationException ex2) { diag=ex2.Message; } finally { m=saved; } }
                    } catch(System.ComponentModel.Win32Exception ex) { Log("Cannot open shadPS4 PID "+e.Id+": "+ex.Message); denied=true; } catch(ArgumentException) { }
                }
                if(hosts.Count==0 && denied) throw new InvalidOperationException("Windows would not let the Workshop open shadPS4. Run both as the same user, or run the Workshop as administrator if shadPS4 is elevated.");
                if(hosts.Count!=1) throw new InvalidOperationException(hosts.Count==0
                    ? "shadPS4 is running but Bloodborne was not matched. "+diag
                    : "Several shadPS4 instances are running Bloodborne. Close the extra instance first.");
                p=hosts[0]; exe=p.MainModule.FileName; IsShadPs4=true;
            } else {
                if(candidates.Length!=1) throw new InvalidOperationException("Several Bloodborne processes are running. Close the extra instance first.");
                p=candidates[0]; exe=p.MainModule.FileName;
                if(!profile.ExeHashes.Contains(Hash(exe))) throw new InvalidOperationException("This port executable differs from the verified visy 0.6.7 / 0.6.8 builds. A compatibility update is needed.");
            }
            if(IsShadPs4) { PortPath=Path.GetDirectoryName(exe); UserPath=ShadUserDirectory(PortPath) ?? UserPath; }
            else { PortPath=Directory.GetParent(Path.GetDirectoryName(exe)).FullName; UserPath=ProcessLaunch.UserDirectory(p.Id,PortPath) ?? UserPath; }
            sessionLock=new Semaphore(1,1,"Local\\BloodborneHuntersWorkshop-"+p.Id);
            ownsSession=sessionLock.WaitOne(0);
            if(!ownsSession) { sessionLock.Dispose(); sessionLock=null; throw new InvalidOperationException("Another Hunter's Workshop is already connected."); }
            try {
                m=new Memory(p); image=IsShadPs4?FindShadImage():FindImage();
                if(!VerifiedFunction(image+Commands.Tick,tickPro) || !VerifiedFunction(image+Commands.GiveList,listPro) || !Match(image+Commands.Release,releasePro) || !Match(image+Commands.SaveRequest,savePro))
                    throw new InvalidOperationException("A required function is changed. This build cannot be attached safely.");
                site=image+Commands.Tick; byte[] first=m.Read(site,tickPro.Length); long resume; byte[] displaced;
                if(first[0]==0xe9) {
                    if(first.Skip(5).Any(b=>b!=0x90)) throw new InvalidOperationException("Unknown tick-hook padding; cannot install the command bridge.");
                    original=first; resume=site+5+BitConverter.ToInt32(first,1); displaced=new byte[0];
                }
                else { original=first; resume=site+first.Length; displaced=first; }
                block=m.AllocateNear(site); data=block+4096;
                byte[] bridge=Commands.Bridge(image,data,resume,displaced); if(bridge.Length>4096) throw new InvalidOperationException("Command bridge exceeds its code page.");
                m.Write(block,bridge); uint old;
                if(!Native.VirtualProtectEx(m.Handle,new IntPtr(block),(UIntPtr)4096,0x20,out old) || !Native.FlushInstructionCache(m.Handle,new IntPtr(block),(UIntPtr)4096)) throw Native.Error("Cannot prepare command bridge");
                patch=Enumerable.Repeat((byte)0x90,original.Length).ToArray();
                patch[0]=0xff;patch[1]=0x25;for(int i=2;i<6;i++)patch[i]=0;
                Buffer.BlockCopy(BitConverter.GetBytes(block),0,patch,6,8);
                m.PausedPatch(site,original,patch); hookInstalled=true; faulted=false;
                Log("Connected "+TargetName+" PID "+p.Id+" image 0x"+image.ToString("X")+"; main-thread bridge installed.");
            } catch { Dispose(); throw; }
        }
        public Snapshot Inspect() {
            var s=new Snapshot { Message="Not connected" };
            if(!Connected) return s;
            if(faulted) { s.Message="Command outcome uncertain — restart Bloodborne"; return s; }
            try {
                long g=m.Q(image+Commands.Gdm),w=m.Q(image+Commands.World),f=m.Q(image+Commands.Flow);
                if(g==0 || w==0 || f==0) { s.Message="Load a character"; return s; }
                s.Pgd=m.Q(g+8); s.Player=m.Q(w+0x60);
                if(s.Pgd==0 || s.Player==0) { s.Message="Load a character"; return s; }
                if(m.Read(image+Commands.Loading,1)[0]!=0) { s.Message="Waiting for loading to finish"; return s; }
                if(m.Read(f+0x1592,1)[0]!=1) { s.Message="Return to your own world before granting items"; return s; }
                long modules=m.Q(s.Player+0x3b0); long stats=modules==0?0:m.Q(modules+0x20);
                s.Hp=stats==0?0:m.I(stats+0xf8); s.Level=m.I(s.Pgd+0x90);
                if(s.Hp<=0) { s.Message="Waiting for a living character"; return s; }
                s.Ready=true; s.Message="Hunter ready · level "+s.Level+" · HP "+s.Hp;
            } catch { s.Message="Player data is unavailable — wait for loading"; }
            return s;
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
                if(LastBackup==null || backedUpCharacter!=s.Pgd) { LastBackup=Backup(); backedUpCharacter=s.Pgd; }
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
                    faulted=true; Log("TIMEOUT id="+id+" cancelled-before-execution="+cancelled+"; no retry; restart required.");
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
            if(!String.IsNullOrWhiteSpace(emulatorFolder)) options.Add(Path.Combine(emulatorFolder,"user"));
            options.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"shadPS4","user"));
            foreach(string o in options) if(Directory.Exists(Path.Combine(o,"savedata"))) return Path.GetFullPath(o);
            return null;
        }
        public void ConfigurePort(string directory, string userDirectory = null) {
            if(Connected) throw new InvalidOperationException("Disconnect before changing the port folder.");
            if(String.IsNullOrWhiteSpace(directory)) throw new IOException("Select the native port folder or the shadPS4 folder.");
            if(File.Exists(Path.Combine(directory,"out","bbport.exe"))) {
                IsShadPs4=false; PortPath=Path.GetFullPath(directory);
                UserPath=String.IsNullOrWhiteSpace(userDirectory)?null:Path.GetFullPath(userDirectory);
            } else if(File.Exists(Path.Combine(directory,"shadPS4.exe")) || File.Exists(Path.Combine(directory,"shadPS4QtLauncher.exe"))) {
                IsShadPs4=true; PortPath=Path.GetFullPath(directory);
                UserPath=String.IsNullOrWhiteSpace(userDirectory)?ShadUserDirectory(PortPath):Path.GetFullPath(userDirectory);
            } else throw new IOException("Select the native port folder containing out\\bbport.exe, or the shadPS4 folder containing shadPS4.exe.");
        }
        public string Backup() {
            string user=UserPath;
            if(String.IsNullOrWhiteSpace(user)) {
                if(String.IsNullOrWhiteSpace(PortPath)) throw new IOException("Choose your native port or shadPS4 folder in Session, or connect to the game first.");
                user=IsShadPs4?(ShadUserDirectory(PortPath) ?? Path.Combine(PortPath,"user")):Path.Combine(PortPath,"user");
            }
            return SaveBackup.Create(user,Storage.Backups,IsShadPs4?ShadTitleId:null);
        }
        public void Dispose() {
            if(actions.CurrentCount==0) throw new InvalidOperationException("Wait for the current grant to finish before disconnecting.");
            if(m!=null) {
                bool exited=m.Process.HasExited;
                if(!exited && hookInstalled) {
                    if(!Match(site,patch)) throw new InvalidOperationException("Another tool changed the command hook. Close Bloodborne before disconnecting.");
                    // Keep the connection available for retry if safe removal fails.
                    m.PausedPatch(site,patch,original);
                    hookInstalled=false;
                }
                if(!exited && !hookInstalled && block!=0 && patch==null) m.Free(block);
                m.Dispose(); m=null; patch=null; original=null; block=0; data=0; site=0; image=0;
                hookInstalled=false; faulted=false; LastBackup=null; backedUpCharacter=0;
            }
            if(ownsSession) { sessionLock.Release(); ownsSession=false; }
            if(sessionLock!=null) { sessionLock.Dispose(); sessionLock=null; }
        }
    }
}
