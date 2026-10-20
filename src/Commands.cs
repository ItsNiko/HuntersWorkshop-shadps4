// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;

namespace HuntersWorkshop {
    public sealed class Emitter {
        readonly List<byte> b=new List<byte>(); readonly Dictionary<string,int> labels=new Dictionary<string,int>();
        readonly List<KeyValuePair<int,string>> fixes=new List<KeyValuePair<int,string>>();
        public void B(params byte[] x) { b.AddRange(x); }
        public void I(int x) { B(BitConverter.GetBytes(x)); }
        public void Q(long x) { B(BitConverter.GetBytes(x)); }
        public void L(string n) { labels.Add(n,b.Count); }
        public void J(byte condition,string n) { B(0x0f,condition); fixes.Add(new KeyValuePair<int,string>(b.Count,n)); I(0); }
        public void Jump(string n) { B(0xe9); fixes.Add(new KeyValuePair<int,string>(b.Count,n)); I(0); }
        public void Rax(long x) { B(0x48,0xb8); Q(x); }
        public void Call(long x) { Rax(x); B(0xff,0xd0); }
        public void AbsJump(long x) { B(0xff,0x25,0,0,0,0); Q(x); }
        public byte[] Finish() { byte[] a=b.ToArray(); foreach(var f in fixes) Buffer.BlockCopy(BitConverter.GetBytes(labels[f.Value]-f.Key-4),0,a,f.Key,4); return a; }
    }
    public static class Commands {
        public const long Tick=0x2034770, Give=0x131cb70, Create=0x1a87590, Release=0x1a8bc00, GiveList=0x17d89f0, SaveRequest=0x1336da0, LuaEvent=0x553b0c8;
        public const long Gdm=0x553b130, World=0x553e878, Flow=0x5556678, Gaitem=0x553e990, MapItem=0x553d6e0, Loading=0x556286b;
        public static readonly byte[] TickPrologue={0x55,0x48,0x89,0xe5,0x41,0x57};
        static void Nonzero(Emitter c,string failure="stale") { c.B(0x48,0x85,0xc0); c.J(0x84,failure); }
        static void Slot(Emitter c,long p,string failure="stale") { c.Rax(p); c.B(0x48,0x8b,0x00); Nonzero(c,failure); }
        static void Status(Emitter c,int result) { c.B(0x41,0xc7,0x45,4); c.I(result); c.B(0x41,0xc7,0x45,0); c.I(3); c.Jump("restore"); }
        // Packet: status/result/kind/id/type/quantity (6x u32), expected PGD/player (2x u64),
        // reserved (8), handle/flags/shape/effects[6] (9x u32), list at +96.
        public static byte[] Bridge(long image,long data,long continuation,byte[] displaced) {
            var c=new Emitter();
            // Respect the SysV 128-byte red zone, then preserve every GPR, flags and x87/SSE/AVX.
            c.B(0x48,0x8d,0xa4,0x24,0x80,0xff,0xff,0xff,0x9c,0x50,0x51,0x52,0x53,0x55,0x56,0x57);
            for(byte r=0x50;r<=0x57;r++) c.B(0x41,r);
            c.B(0x49,0x89,0xe4,0x48,0x81,0xec,0x40,4,0,0,0x48,0x83,0xe4,0xc0);
            // XSAVE header must be initialized. Mask 7 saves x87, SSE and AVX (port needs AVX2).
            c.B(0x31,0xc0,0x48,0x89,0xe7,0xb9,0x88,0,0,0,0xf3,0x48,0xab,0xb8,7,0,0,0,0x31,0xd2,0x48,0x0f,0xae,0x24,0x24);
            c.B(0x49,0xbd); c.Q(data);
            c.B(0xb8,1,0,0,0,0xb9,2,0,0,0,0xf0,0x41,0x0f,0xb1,0x4d,0); c.J(0x85,"restore");
            c.Rax(image+Loading); c.B(0x80,0x38,0); c.J(0x85,"stale");
            Slot(c,image+Gdm); c.B(0x48,0x8b,0x40,8,0x49,0x3b,0x45,24); c.J(0x85,"stale"); Nonzero(c);
            Slot(c,image+World); c.B(0x48,0x8b,0x40,0x60,0x49,0x3b,0x45,32); c.J(0x85,"stale"); Nonzero(c);
            // The player must still be alive. ChrIns -> module container -> data module.
            c.B(0x48,0x8b,0x80,0xb0,3,0,0); Nonzero(c); c.B(0x48,0x8b,0x40,0x20); Nonzero(c);
            c.B(0x83,0xb8,0xf8,0,0,0,0); c.J(0x8e,"stale");
            Slot(c,image+Flow); c.B(0x80,0xb8,0x92,0x15,0,0,1); c.J(0x85,"stale");
            Slot(c,image+MapItem);
            Slot(c,image+LuaEvent);
            c.B(0x41,0x83,0x7d,8,1); c.J(0x84,"gem");
            c.B(0x31,0xff,0x41,0x8b,0x75,16,0x41,0x8b,0x55,12,0x41,0x8b,0x4d,20); c.Call(image+Give);
            c.Jump("complete");
            c.L("gem"); Slot(c,image+Gaitem); c.B(0x48,0x89,0xc6,0x49,0x8d,0x7d,48,0x41,0x8b,0x55,12); c.Call(image+Create);
            c.B(0x41,0x8b,0x45,48,0x85,0xc0); c.J(0x84,"failed");
            c.B(0xa9,0,0,0x80,0); c.J(0x84,"releasefailed");
            c.B(0x0f,0xb7,0xc8,0x81,0xf9,0xff,0xff,0,0); c.J(0x84,"releasefailed");
            Slot(c,image+Gaitem,"releasefailed"); c.B(0x48,0x8b,0x44,0xc8,8); Nonzero(c,"releasefailed");
            c.B(0x41,0x8b,0x55,48,0x39,0x50,8); c.J(0x85,"releasefailed");
            c.B(0x41,0x8b,0x55,12,0x81,0xca,0,0,0,0x80,0x39,0x50,12); c.J(0x85,"releasefailed");
            // The serialized type/shape are instance +0x18/+0x1c. Six effects follow them.
            for(byte offset=0;offset<32;offset+=4) c.B(0x41,0x8b,0x55,(byte)(52+offset),0x89,0x50,(byte)(24+offset));
            c.B(0x41,0xc7,0x45,96,1,0,0,0,0x41,0x8b,0x55,48,0x41,0x89,0x55,100,0x8b,0x50,12,0x41,0x89,0x55,104,
                0x41,0xc7,0x45,108,1,0,0,0,0x41,0xc7,0x45,112,0xff,0xff,0xff,0xff);
            Slot(c,image+MapItem,"releasefailed"); c.B(0x48,0x89,0xc7,0x49,0x8d,0x75,96); c.Call(image+GiveList);
            c.B(0x49,0x8d,0x7d,48); c.Call(image+Release);
            c.L("complete"); c.Call(image+SaveRequest); Status(c,1);
            c.L("releasefailed"); c.B(0x49,0x8d,0x7d,48); c.Call(image+Release);
            c.L("failed"); Status(c,3);
            c.L("stale"); Status(c,2);
            c.L("restore");
            c.B(0xb8,7,0,0,0,0x31,0xd2,0x48,0x0f,0xae,0x2c,0x24,0x4c,0x89,0xe4);
            for(byte r=0x5f;r>=0x58;r--) c.B(0x41,r);
            c.B(0x5f,0x5e,0x5d,0x5b,0x5a,0x59,0x58,0x9d,0x48,0x8d,0xa4,0x24,0x80,0,0,0);
            c.B(displaced); c.AbsJump(continuation);
            return c.Finish();
        }
        public static byte[] Packet(long pgd,long player,int kind,int id,int category,int count,int flags,int shape,int[] effects) {
            byte[] b=new byte[128]; Put(b,8,kind); Put(b,12,id); Put(b,16,category<<28); Put(b,20,count);
            Buffer.BlockCopy(BitConverter.GetBytes(pgd),0,b,24,8); Buffer.BlockCopy(BitConverter.GetBytes(player),0,b,32,8);
            Put(b,52,flags); Put(b,56,shape); for(int i=0;i<6;i++) Put(b,60+4*i,effects==null?-1:effects[i]); return b;
        }
        public static void Put(byte[] b,int offset,int n) { Buffer.BlockCopy(BitConverter.GetBytes(n),0,b,offset,4); }
    }
}
