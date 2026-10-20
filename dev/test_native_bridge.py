"""Run the exact bridge on the real Windows CPU, against isolated fake game memory.
No process is opened and no game/save is touched. Validates real XSAVE/XRSTOR AVX state.
"""
import ctypes as c
import struct
import sys
from pathlib import Path

k=c.WinDLL('kernel32',use_last_error=True)
k.VirtualAlloc.argtypes=[c.c_void_p,c.c_size_t,c.c_uint32,c.c_uint32];k.VirtualAlloc.restype=c.c_void_p
k.VirtualFree.argtypes=[c.c_void_p,c.c_size_t,c.c_uint32]
BASE=0x10000000;CODE=0x30000000;DATA=CODE+4096;OBJECTS=0x50000000;WRAP=0x40000000
PGD=OBJECTS+0x10000;PLAYER=OBJECTS+0x30000;INSTANCE=OBJECTS+0xa0000
HANDLE=0xc0800003
allocated=[]
def put(at,value,fmt='<Q'):c.memmove(at,struct.pack(fmt,value),struct.calcsize(fmt))
def read(at,n):return c.string_at(at,n)
def get(at,fmt='<I'):return struct.unpack(fmt,read(at,struct.calcsize(fmt)))[0]
def raw(at,b):c.memmove(at,b,len(b))
def imm(v):return struct.pack('<Q',v)

try:
    for at,size in [(BASE,0x6000000),(CODE,8192),(OBJECTS,0xc0000),(WRAP,0x3000)]:
        p=k.VirtualAlloc(at,size,0x3000,0x40)
        if p!=at:raise OSError(f'Cannot reserve isolated test memory at {at:x}')
        allocated.append(p)
    root=Path(sys.argv[1]).resolve() if len(sys.argv)>1 else Path(__file__).resolve().parents[1]/'test-output/core'
    raw(CODE,(root/'bridge.bin').read_bytes());raw(BASE+0x10000,b'\xc3')
    for slot,obj in [(0x553b130,OBJECTS),(0x553e878,OBJECTS+0x20000),(0x5556678,OBJECTS+0x60000),(0x553e990,OBJECTS+0x70000),(0x553d6e0,OBJECTS+0x90000),(0x553b0c8,OBJECTS+0xb1000)]:put(BASE+slot,obj)
    for at,value in [(OBJECTS+8,PGD),(OBJECTS+0x20060,PLAYER),(PLAYER+0x3b0,OBJECTS+0x40000),(OBJECTS+0x40020,OBJECTS+0x50000),(OBJECTS+0x70008+3*8,INSTANCE)]:put(at,value)
    put(OBJECTS+0x500f8,900,'<i');put(OBJECTS+0x61592,1,'<B');put(INSTANCE+8,HANDLE,'<I');put(INSTANCE+12,0x80000000|90150,'<I')
    # Function mocks use real AVX instructions to destroy every YMM register.
    clobber=b'\xc5\xfc\x77'
    raw(BASE+0x131cb70,b'\x48\xb8'+imm(OBJECTS+0xb0000)+b'\xff\x00'+clobber+b'\xc3')
    raw(BASE+0x1336da0,b'\x48\xb8'+imm(OBJECTS+0xb0008)+b'\xff\x00'+clobber+b'\xc3')
    raw(BASE+0x1a87590,b'\xc7\x07'+struct.pack('<I',HANDLE)+clobber+b'\xc3')
    raw(BASE+0x17d89f0,b'\x48\xb8'+imm(OBJECTS+0xb0004)+b'\xff\x00'+clobber+b'\xc3')
    raw(BASE+0x1a8bc00,b'\xc7\x07\x00\x00\x00\x00'+clobber+b'\xc3')
    # Wrapper preserves its caller's SIMD state, installs test patterns in all 16 YMMs,
    # calls the exact shipped bridge, records all YMMs and restores its caller's state.
    wrapper=bytearray.fromhex('4154574989e44881ec400400004883e4c031c04889e7b988000000f348abb80700000031d2480fae2424')
    patterns=[]
    for i in range(16):
        pattern=bytes((i*13+j)%256 for j in range(32));patterns.append(pattern);raw(WRAP+0x1000+i*32,pattern)
        wrapper+=b'\x48\xb8'+imm(WRAP+0x1000+i*32)+bytes([0xc5,0xfe if i<8 else 0x7e,0x6f,(i%8)<<3])
    wrapper+=b'\x48\xb8'+imm(CODE)+b'\xff\xd0'
    for i in range(16):wrapper+=b'\x48\xb8'+imm(WRAP+0x2000+i*32)+bytes([0xc5,0xfe if i<8 else 0x7e,0x7f,(i%8)<<3])
    wrapper+=bytes.fromhex('b80700000031d2480fae2c244c89e45f415cc3')
    raw(WRAP,bytes(wrapper));fn=c.WINFUNCTYPE(None)(WRAP)
    base_packet=bytearray((root/'gem-packet.bin').read_bytes());struct.pack_into('<QQ',base_packet,24,PGD,PLAYER)
    def execute(kind=1,shape=63,flag=1):
        p=bytearray(base_packet);struct.pack_into('<III',p,0,1,0,kind);struct.pack_into('<II',p,52,flag,shape)
        if kind==0:struct.pack_into('<III',p,12,1000,0x40000000,3)
        raw(DATA,bytes(p));fn()
        assert all(read(WRAP+0x2000+i*32,32)==p for i,p in enumerate(patterns)), 'real AVX state corrupted'
        assert get(DATA)==3 and get(DATA+4)==1,'command failed'
        if kind==1:assert read(INSTANCE+24,32)==bytes(p[52:84]),'gem fields differ'
    execute(kind=0)
    for shape in [1,2,4,8,63]:execute(shape=shape)
    for shape in [1,2]:execute(shape=shape,flag=2)
    assert get(OBJECTS+0xb0000)==1 and get(OBJECTS+0xb0004)==7,'unexpected function calls'
    assert get(OBJECTS+0xb0008)==8,'every successful grant requests saving'
    # A completed packet cannot run twice.
    fn();assert get(OBJECTS+0xb0004)==7
    print('9 real CPU bridge checks passed: item, five gem shapes, two rune shapes, no repeat; all 16 YMM registers preserved.')
    (root/'native-command-tests.txt').write_text('9 real CPU bridge checks passed; AVX state preserved across clobbering System V mocks.\n')
finally:
    for p in allocated:k.VirtualFree(p,0,0x8000)
