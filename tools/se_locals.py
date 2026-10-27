"""Prints the frame-0 local (parent-relative) position and rotation an SEAnim keys for the named bones.
Usage: python -I se_locals.py file.seanim bone [bone ...]"""
import math
import struct
import sys

d = open(sys.argv[1], 'rb').read()
want = sys.argv[2:]


def cstr(p):
    e = d.index(b'\0', p)
    return d[p:e].decode('utf8', 'replace'), e + 1


def isz(n):
    return 1 if n <= 0xFF else 2 if n <= 0xFFFF else 4


def rd(p, size):
    return (d[p], p + 1) if size == 1 else (struct.unpack_from('<H', d, p)[0], p + 2) if size == 2 else (struct.unpack_from('<I', d, p)[0], p + 4)


p = 8
hs = struct.unpack_from('<H', d, p)[0]
dflags, pflags = d[p + 4], d[p + 5]
fps, nf, nb = struct.unpack_from('<fII', d, p + 8)
nmod = d[p + 20]
p = 8 + hs
names = []
for _ in range(nb):
    s, p = cstr(p)
    names.append(s)
bsz, fsz = isz(nb), isz(nf)
p += nmod * (bsz + 1)
fmt = 'd' if pflags & 1 else 'f'
comp = 8 if pflags & 1 else 4
for b in range(nb):
    p += 1
    keys = {}
    for flag, width in ((1, 3), (2, 4), (4, 3)):
        if dflags & flag:
            k, p = rd(p, fsz)
            keys[flag] = []
            for i in range(k):
                fr, p = rd(p, fsz)
                v = struct.unpack_from('<%d%s' % (width, fmt), d, p)
                p += width * comp
                keys[flag].append((fr, v))
    if names[b] in want:
        pos = keys.get(1) or []
        rot = keys.get(2) or []
        print('%-28s %d pos keys, %d rot keys' % (names[b], len(pos), len(rot)))
        for fr, v in pos[:: max(1, len(pos) // 6)]:
            print('     pos f%-3d %s' % (fr, [round(x, 2) for x in v]))
        for fr, v in rot[:: max(1, len(rot) // 4)]:
            print('     rot f%-3d %s' % (fr, [round(x, 3) for x in v]))
