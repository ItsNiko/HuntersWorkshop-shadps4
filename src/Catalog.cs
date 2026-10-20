// SPDX-License-Identifier: GPL-3.0-or-later
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace HuntersWorkshop {
    public sealed class Item {
        public int Id {get;set;} public int Category,Max,Stack;
        public string Name {get;set;} public string Variant {get;set;} public string Group,Description;
        public bool Known;
        public int[] Upgrades;
        public string DisplayId { get { return "0x"+((uint)Id | ((uint)Category<<28)).ToString("X8"); } }
    }
    public sealed class GemEffect {
        public int Id {get;set;} public int Rating;
        public string Name {get;set;} public string EffectText,Note;
        public string Effect { get { return EffectText; } set { EffectText=value; } }
        public bool Rune,Known;
        public string Label { get { return Name+" — "+EffectText+" ["+Id+"]"; } }
        public override string ToString() { return Label; }
    }
    public sealed class GemSource { public int Id,Flags; }
    public sealed class Profile { public string[] ExeHashes; public string Version; }
    public sealed class Catalog {
        public Item[] Items; public GemEffect[] Effects; public GemSource[] Gems; public int[] WeaponRows;
        public string GameSourceHash;
        public static T Read<T>(string resource) {
            using(var s=Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
            using(var r=new StreamReader(s)) return new JavaScriptSerializer {MaxJsonLength=16*1024*1024}.Deserialize<T>(r.ReadToEnd());
        }
        public void ValidateItem(Item item,int upgrade,int quantity) {
            if(item==null || !Items.Contains(item)) throw new InvalidOperationException("Choose an item from the catalogue.");
            if(item.Category==0 && (!item.Upgrades.Contains(upgrade) || !WeaponRows.Contains(item.Id+100*upgrade))) throw new InvalidOperationException("That weapon reinforcement does not exist.");
            if(item.Category!=0 && upgrade!=0) throw new InvalidOperationException("Only weapons have reinforcement.");
            if(quantity<1 || quantity>Math.Min(item.Stack,999)) throw new InvalidOperationException("Quantity must be 1–"+Math.Min(item.Stack,999)+".");
        }
        public GemSource ValidateGem(bool rune,int shape,int source,int[] effects) {
            if(!(rune ? new[]{1,2} : new[]{1,2,4,8,63}).Contains(shape)) throw new InvalidOperationException("Choose a valid shape.");
            var g=Gems.FirstOrDefault(x=>x.Id==source && (x.Flags&(rune?2:1))!=0);
            if(g==null) throw new InvalidOperationException("The gem source does not exist or has the wrong type.");
            if(effects==null || effects.Length!=6 || effects[0]<0) throw new InvalidOperationException("Choose a primary effect.");
            if(effects.Any(id=>id!=-1 && !Effects.Any(e=>e.Id==id))) throw new InvalidOperationException("An effect is absent from this game's parameters.");
            if(rune && !Effects.Any(e=>e.Id==effects[0] && e.Rune)) throw new InvalidOperationException("Choose a Caryll rune effect.");
            if(!rune && Effects.Any(e=>e.Id==effects[0] && e.Rune)) throw new InvalidOperationException("Choose a blood gem primary effect.");
            return g;
        }
    }
}
