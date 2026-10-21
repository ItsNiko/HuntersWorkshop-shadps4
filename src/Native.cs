// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace HuntersWorkshop {
    internal static class Native {
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr OpenProcess(uint access,bool inherit,int pid);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool VirtualFreeEx(IntPtr h,IntPtr at,UIntPtr size,uint kind);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool ReadProcessMemory(IntPtr h,IntPtr at,byte[] b,UIntPtr n,out UIntPtr done);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool WriteProcessMemory(IntPtr h,IntPtr at,byte[] b,UIntPtr n,out UIntPtr done);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr VirtualAllocEx(IntPtr h,IntPtr at,UIntPtr size,uint allocation,uint protection);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool VirtualProtectEx(IntPtr h,IntPtr at,UIntPtr size,uint protection,out uint old);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool FlushInstructionCache(IntPtr h,IntPtr at,UIntPtr size);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern UIntPtr VirtualQueryEx(IntPtr h,IntPtr at,out Region info,UIntPtr size);
        [DllImport("ntdll.dll")] internal static extern int NtSuspendProcess(IntPtr h);
        [DllImport("ntdll.dll")] internal static extern int NtResumeProcess(IntPtr h);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern IntPtr OpenThread(uint access,bool inherit,int id);
        [DllImport("kernel32.dll",SetLastError=true)] internal static extern bool GetThreadContext(IntPtr thread,IntPtr context);
        [StructLayout(LayoutKind.Sequential)] internal struct Region {
            public long Base,AllocationBase; public uint AllocationProtect; public ushort Partition; public ushort Padding;
            public ulong Size; public uint State,Protect,Type,Pad;
        }
        internal static Exception Error(string op) { return new Win32Exception(Marshal.GetLastWin32Error(),op); }
    }

    // Read/write access to the shadPS4 process plus the one operation that needs care: replacing
    // game code while every thread is stopped.
    public sealed class Memory : IDisposable {
        internal IntPtr Handle; internal readonly Process Host;
        internal Memory(Process p) {
            Host=p; Handle=Native.OpenProcess(0xC38,false,p.Id); // query/read/write/VM operation + suspend; no remote-thread access
            if(Handle==IntPtr.Zero) throw Native.Error("Cannot open shadPS4");
        }
        public byte[] Read(long at,int size) {
            if(at<0x10000 || size<1 || size>1048576) throw new InvalidOperationException("Invalid memory range.");
            byte[] b=new byte[size]; UIntPtr n;
            if(!Native.ReadProcessMemory(Handle,new IntPtr(at),b,(UIntPtr)(uint)size,out n) || n.ToUInt64()!=(ulong)size) throw Native.Error("Cannot read game memory");
            return b;
        }
        public int I(long at) { return BitConverter.ToInt32(Read(at,4),0); }
        public long Q(long at) { return BitConverter.ToInt64(Read(at,8),0); }
        internal void Write(long at,byte[] b) {
            UIntPtr n;
            if(!Native.WriteProcessMemory(Handle,new IntPtr(at),b,(UIntPtr)(uint)b.Length,out n) || n.ToUInt64()!=(ulong)b.Length) throw Native.Error("Cannot write game command");
        }
        internal void Code(long at,byte[] b) {
            uint old;
            if(!Native.VirtualProtectEx(Handle,new IntPtr(at),(UIntPtr)(uint)b.Length,0x40,out old)) throw Native.Error("Cannot open hook page");
            try { Write(at,b); if(!Native.FlushInstructionCache(Handle,new IntPtr(at),(UIntPtr)(uint)b.Length)) throw Native.Error("Cannot flush hook"); }
            finally { uint ignored; if(!Native.VirtualProtectEx(Handle,new IntPtr(at),(UIntPtr)(uint)b.Length,old,out ignored)) throw Native.Error("Cannot restore hook page protection"); }
        }
        internal void Free(long at) { if(at!=0 && !Native.VirtualFreeEx(Handle,new IntPtr(at),UIntPtr.Zero,0x8000)) throw Native.Error("Cannot release unused command memory"); }
        // Independent allocation in shadPS4's own address space. The bridge is reached through an
        // absolute jump, so it does not need to sit near the game image.
        internal long Allocate() {
            var p=Native.VirtualAllocEx(Handle,IntPtr.Zero,(UIntPtr)8192,0x3000,4);
            if(p==IntPtr.Zero) throw Native.Error("Cannot allocate the main-thread command bridge");
            return p.ToInt64();
        }
        internal bool CancelQueued(long data) {
            if(Native.NtSuspendProcess(Handle)!=0) return false;
            try { if(I(data)!=1)return false;Write(data,BitConverter.GetBytes(0));return true; }
            finally { if(Native.NtResumeProcess(Handle)!=0) throw new InvalidOperationException("Cannot resume the game."); }
        }

        // Instruction pointer of one thread (the process must be suspended), or -1 when the thread
        // cannot be opened or read.
        static long ThreadRip(int id) {
            IntPtr th=Native.OpenThread(0x48,false,id);
            if(th==IntPtr.Zero) return -1;
            IntPtr raw=Marshal.AllocHGlobal(1232+16);
            try {
                var ctx=new IntPtr((raw.ToInt64()+15)&~15L);
                Marshal.Copy(new byte[1232],0,ctx,1232); Marshal.WriteInt32(ctx,48,0x100001);
                return Native.GetThreadContext(th,ctx)?Marshal.ReadInt64(ctx,248):-1;
            } finally { Marshal.FreeHGlobal(raw); Native.CloseHandle(th); }
        }
        // True when some thread is executing inside [lo,hi). The thread list is taken fresh every
        // time: shadPS4 starts and ends helper threads constantly, and a thread that has gone or
        // cannot be read is not a game thread parked inside our bytes, so it is skipped.
        bool AnyThreadInside(long lo,long hi) {
            Process fresh;
            try { fresh=Process.GetProcessById(Host.Id); }
            catch(ArgumentException) { throw new InvalidOperationException("shadPS4 closed while the Workshop was attaching."); }
            using(fresh) {
                foreach(ProcessThread t in fresh.Threads) {
                    long rip=ThreadRip(t.Id);
                    if(rip>=lo && rip<hi) return true;
                }
            }
            return false;
        }
        // All game threads are stopped for instruction replacement. Retry while a thread sits inside
        // the bytes being replaced; never translate an in-flight instruction.
        internal void PausedPatch(long at,byte[] expected,byte[] replacement) {
            for(int attempt=0;attempt<50;attempt++) {
                if(Native.NtSuspendProcess(Handle)!=0) throw new InvalidOperationException("Cannot suspend game threads for the hook.");
                try {
                    if(!AnyThreadInside(at,at+expected.Length)) {
                        if(!Read(at,expected.Length).SequenceEqual(expected)) throw new InvalidOperationException("The game hook changed. Restart the game before connecting again.");
                        try { Code(at,replacement); }
                        catch { Code(at,expected); throw; }
                        return;
                    }
                } finally { if(Native.NtResumeProcess(Handle)!=0) throw new InvalidOperationException("Cannot resume the game."); }
                Thread.Sleep(5);
            }
            throw new InvalidOperationException("Game code is busy. Try connecting again.");
        }
        public void Dispose() { if(Handle!=IntPtr.Zero) { Native.CloseHandle(Handle); Handle=IntPtr.Zero; } Host.Dispose(); }
    }
}
