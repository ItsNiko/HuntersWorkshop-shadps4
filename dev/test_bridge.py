"""Execute the exact shipped x64 bridge with mocked game functions in Unicorn.
This validates ABI arguments, state preservation, once-only delivery, guards and gem references.
It does not claim that mocked calls prove game compatibility.
"""
import json
import struct
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = Path(sys.argv[1]).resolve() if len(sys.argv)>1 else ROOT/'test-output/core'
from unicorn import Uc, UC_ARCH_X86, UC_MODE_64, UC_HOOK_CODE
from unicorn.x86_const import *

BASE=0x10000000
CODE=0x30000000
DATA=CODE+4096
END=BASE+0x10000
STACK=0x40000000
OBJECTS=0x50000000
GDM=OBJECTS
PGD=OBJECTS+0x10000
WCM=OBJECTS+0x20000
PLAYER=OBJECTS+0x30000
MODULES=OBJECTS+0x40000
STATS=OBJECTS+0x50000
FLOW=OBJECTS+0x60000
GI=OBJECTS+0x70000
MIM=OBJECTS+0x90000
INSTANCE=OBJECTS+0xa0000
HANDLE=0xc0800003
code=(ARTIFACTS/'bridge.bin').read_bytes()
packet=(ARTIFACTS/'gem-packet.bin').read_bytes()
REGS=[UC_X86_REG_RAX, UC_X86_REG_RBX, UC_X86_REG_RCX, UC_X86_REG_RDX,
      UC_X86_REG_RBP,UC_X86_REG_RSI,UC_X86_REG_RDI,
      UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R10,UC_X86_REG_R11,
      UC_X86_REG_R12,UC_X86_REG_R13,UC_X86_REG_R14,UC_X86_REG_R15]
VOLATILE=[UC_X86_REG_RAX,UC_X86_REG_RCX,UC_X86_REG_RDX,UC_X86_REG_RSI,UC_X86_REG_RDI,
          UC_X86_REG_R8,UC_X86_REG_R9,UC_X86_REG_R10,UC_X86_REG_R11]

def case(kind=1, change=None, failure=None, state=1):
    u=Uc(UC_ARCH_X86,UC_MODE_64)
    u.ctl_set_cpu_model(UC_CPU_X86_HASWELL)
    for address,size in [(BASE,0x6000000),(CODE,0x2000),(STACK,0x10000),(OBJECTS,0xc0000)]:
        u.mem_map(address,size)
    # Bloodborne's AVX2 runtime has OSXSAVE enabled. Mirror that OS CPU setup.
    u.reg_write(UC_X86_REG_CR4,u.reg_read(UC_X86_REG_CR4)|0x40000)
    setup=b'\x31\xc9\xb8\x07\x00\x00\x00\x31\xd2\x0f\x01\xd1'
    u.mem_write(CODE+0x800,setup)
    u.emu_start(CODE+0x800,CODE+0x800+len(setup))
    def put(at,value,fmt='<Q'):u.mem_write(at,struct.pack(fmt,value))
    def get(at,fmt='<I'):return struct.unpack(fmt,u.mem_read(at,struct.calcsize(fmt)))[0]
    u.mem_write(CODE,code)
    p=bytearray(packet)
    struct.pack_into('<III',p,0,state,0,kind)
    struct.pack_into('<QQ',p,24,PGD,PLAYER)
    if kind==0: struct.pack_into('<III',p,12,1000,0x40000000,3)
    u.mem_write(DATA,bytes(p))
    for slot,obj in [(0x553b130,GDM),(0x553e878,WCM),(0x5556678,FLOW),(0x553e990,GI),(0x553d6e0,MIM),(0x553b0c8,OBJECTS+0xb1000)]:put(BASE+slot,obj)
    for at,value in [(GDM+8,PGD),(WCM+0x60,PLAYER),(PLAYER+0x3b0,MODULES),(MODULES+0x20,STATS),(GI+8+3*8,INSTANCE)]:put(at,value)
    put(STATS+0xf8,900,'<i');put(FLOW+0x1592,1,'<B')
    put(INSTANCE+8,HANDLE,'<I');put(INSTANCE+12,0x80000000|90150,'<I')
    calls=[]
    funcs={BASE+0x131cb70:'give',BASE+0x1a87590:'create',BASE+0x1a8bc00:'release',BASE+0x17d89f0:'list',BASE+0x1336da0:'save'}
    for at in funcs:u.mem_write(at,b'\xc3')
    initial={r:0x12345600+i for i,r in enumerate(REGS)}
    for r,v in initial.items():u.reg_write(r,v)
    initial_ymm={UC_X86_REG_YMM0+i:((i+1)<<240)|((i+20)<<64)|0x123 for i in range(16)}
    for r,v in initial_ymm.items():u.reg_write(r,v)
    sp=STACK+0x8008
    u.reg_write(UC_X86_REG_RSP,sp);u.reg_write(UC_X86_REG_EFLAGS,0x246)
    u.mem_write(sp-128,b'R'*128)
    def hook(uc,at,size,user):
        if at==END:uc.emu_stop();return
        # Unicorn does not implement the AVX XSAVE component. Model that pair here;
        # test_native_bridge.py separately verifies the exact instructions on the real CPU.
        ins=bytes(uc.mem_read(at,size))
        if ins==bytes.fromhex('480fae2424'):
            saved_simd.update({r:uc.reg_read(r) for r in initial_ymm})
            uc.reg_write(UC_X86_REG_RIP,at+size);return
        if ins==bytes.fromhex('480fae2c24'):
            for r,v in saved_simd.items():uc.reg_write(r,v)
            uc.reg_write(UC_X86_REG_RIP,at+size);return
        if at not in funcs:return
        args=[uc.reg_read(r) for r in [UC_X86_REG_RDI,UC_X86_REG_RSI,UC_X86_REG_RDX,UC_X86_REG_RCX]]
        assert uc.reg_read(UC_X86_REG_RSP)%16==8, 'SysV function-entry stack alignment'
        name=funcs[at];calls.append(name)
        if name=='give':assert args==[0,0x40000000,1000,3],args
        if name=='create':
            assert args[:3]==[DATA+48,GI,90150],args
            put(DATA+48,0 if failure=='allocation' else HANDLE,'<I')
            if failure=='wrong_handle':put(INSTANCE+8,HANDLE+1,'<I')
            if failure=='wrong_type':put(INSTANCE+12,0x80000001,'<I')
            if failure=='null_instance':put(GI+8+3*8,0)
            if failure=='null_manager':put(BASE+0x553e990,0)
            if failure=='null_map':put(BASE+0x553d6e0,0)
        if name=='list':
            assert args[:2]==[MIM,DATA+96],args
            assert list(struct.unpack('<5I',uc.mem_read(DATA+96,20)))==[1,HANDLE,0x80000000|90150,1,0xffffffff]
            assert uc.mem_read(INSTANCE+24,32)==uc.mem_read(DATA+52,32),'gem type/shape/six effects'
        if name=='release':assert args[0]==DATA+48
        # Model the registers a real System V callee may destroy, including AVX state.
        for r in VOLATILE:uc.reg_write(r,0xdeadbeef)
        for r in initial_ymm:uc.reg_write(r,0xbeef)
    saved_simd={}
    u.hook_add(UC_HOOK_CODE,hook)
    if change:change(put,get)
    u.emu_start(CODE,END,count=5000)
    assert u.reg_read(UC_X86_REG_RIP)==END
    assert all(u.reg_read(r)==v for r,v in initial.items()),'GPR preservation'
    assert all(u.reg_read(r)==v for r,v in initial_ymm.items()),'AVX/SSE preservation'
    assert u.reg_read(UC_X86_REG_RSP)==sp,'stack preservation'
    assert u.reg_read(UC_X86_REG_EFLAGS)==0x246,'flags preservation'
    assert u.mem_read(sp-128,128)==b'R'*128,'SysV red zone preservation'
    return get(DATA),get(DATA+4),calls,u

checks=[]
def check(label,kwargs,expected):
    actual=case(**kwargs)[:3]
    assert actual==expected,(label,actual,expected)
    checks.append(label)

check('ordinary item SysV arguments',dict(kind=0),(3,1,['give','save']))
check('gem create, copy, give, release',{},(3,1,['create','list','release','save']))
check('no duplicate when complete',dict(state=3),(3,0,[]))
check('no duplicate when executing',dict(state=2),(2,0,[]))
check('no command when idle',dict(state=0),(0,0,[]))
check('loading guard',dict(change=lambda p,g:p(BASE+0x556286b,1,'<B')),(3,2,[]))
check('changed PGD guard',dict(change=lambda p,g:p(GDM+8,PGD+100)),(3,2,[]))
check('changed player guard',dict(change=lambda p,g:p(WCM+0x60,PLAYER+100)),(3,2,[]))
check('death guard',dict(change=lambda p,g:p(STATS+0xf8,0,'<i')),(3,2,[]))
check('summoned-world guard',dict(change=lambda p,g:p(FLOW+0x1592,0,'<B')),(3,2,[]))
check('null Lua save manager guard',dict(change=lambda p,g:p(BASE+0x553b0c8,0)),(3,2,[]))
check('null game data guard',dict(change=lambda p,g:p(BASE+0x553b130,0)),(3,2,[]))
check('allocation failure',dict(failure='allocation'),(3,3,['create']))
check('instance identity failure releases',dict(failure='wrong_handle'),(3,3,['create','release']))
check('instance type failure releases',dict(failure='wrong_type'),(3,3,['create','release']))
for failure in ['null_instance','null_manager','null_map']:
    check(failure+' after creation releases',dict(failure=failure),(3,3,['create','release']))

out=ARTIFACTS/'command-tests.txt'
out.write_text('\n'.join('PASS '+x for x in checks)+'\n',encoding='utf-8')
print(f'{len(checks)} exact-byte command tests passed; GPRs, YMM0–15, flags, stack and red zone checked on every path.')
