"""Prints what a Greyhound SEModel / SEAnim holds: bones (with parents and local positions), meshes and materials,
or the animation's type, frames, bones and notetracks. Usage: python -I se_info.py file.semodel|file.seanim [--all]"""
import struct
import sys

path = sys.argv[1]
d = open(path, 'rb').read()
show_all = '--all' in sys.argv


def cstr(p):
    e = d.index(b'\0', p)
    return d[p:e].decode('utf8', 'replace'), e + 1


def isz(n):
    return 1 if n <= 0xFF else 2 if n <= 0xFFFF else 4


def rd(p, size):
    return (d[p], p + 1) if size == 1 else (struct.unpack_from('<H', d, p)[0], p + 2) if size == 2 else (struct.unpack_from('<I', d, p)[0], p + 4)


if d.startswith(b'SEModel'):
    p = 9
    hs = struct.unpack_from('<H', d, p)[0]
    df, bf, mf = d[p + 2], d[p + 3], d[p + 4]
    nb, nm, nmat = struct.unpack_from('<III', d, p + 5)
    p = 9 + hs
    names = []
    for _ in range(nb):
        s, p = cstr(p); names.append(s)
    bones = []
    for i in range(nb):
        p += 1
        parent = struct.unpack_from('<i', d, p)[0]; p += 4
        g = l = None
        if bf & 1: g = struct.unpack_from('<7f', d, p); p += 28
        if bf & 2: l = struct.unpack_from('<7f', d, p); p += 28
        if bf & 4: p += 12
        bones.append((names[i], parent, g, l))
    print('SEModel bones', nb, 'meshes', nm, 'materials', nmat, 'boneflags', bf, 'meshflags', mf)
    for i, (n, par, g, l) in enumerate(bones):
        print('  %3d %-32s parent %-28s global %s' % (i, n, bones[par][0] if par >= 0 else '-', [round(x, 2) for x in g[:3]] if g else None))
    # skip meshes to read materials
    bsz = isz(nb)
    for m in range(nm):
        p += 1
        layers, infl = d[p], d[p + 1]
        vc, fc = struct.unpack_from('<II', d, p + 2); p += 10
        if not mf & 1: layers = 0
        if not mf & 8: infl = 0
        p += vc * 12 + vc * 8 * layers
        if mf & 2: p += vc * 12
        if mf & 4: p += vc * 4
        p += vc * infl * (bsz + 4)
        p += fc * 3 * isz(vc)
        mats = struct.unpack_from('<%di' % layers, d, p); p += 4 * layers
        print('  mesh %d: %d verts, %d faces, influence %d, material %s' % (m, vc, fc, infl, mats))
    for _ in range(nmat):
        s, p = cstr(p)
        simple = d[p]; p += 1
        maps = []
        if simple:
            for _ in range(3):
                t, p = cstr(p); maps.append(t)
        print('  material %-45s %s' % (s, maps))
elif d.startswith(b'SEAnim'):
    p = 8
    hs = struct.unpack_from('<H', d, p)[0]
    atype, flags, dflags, pflags = d[p + 2], d[p + 3], d[p + 4], d[p + 5]
    fps, nf, nb = struct.unpack_from('<fII', d, p + 8)
    nmod = d[p + 20]
    nnotes = struct.unpack_from('<I', d, p + 24)[0]
    p = 8 + hs
    names = []
    for _ in range(nb):
        s, p = cstr(p); names.append(s)
    print('SEAnim type', ['absolute', 'additive', 'relative', 'delta'][atype], 'looped', flags & 1, 'data', dflags, 'hiprec', pflags & 1,
          'fps', fps, 'frames', nf, 'bones', nb, 'modifiers', nmod, 'notes', nnotes)
    if show_all:
        print('  bones:', ', '.join(names))
    bsz, fsz = isz(nb), isz(nf)
    for _ in range(nmod):
        b, p = rd(p, bsz)
        print('  modifier', names[b], ['absolute', 'additive', 'relative', 'delta'][d[p]]); p += 1
    comp = 8 if pflags & 1 else 4
    unkeyed = []
    for b in range(nb):
        p += 1
        keys = []
        for flag, width in ((1, 3), (2, 4), (4, 3)):
            if dflags & flag:
                k, p = rd(p, fsz)
                keys.append(k)
                p += k * (fsz + width * comp)
        if not any(keys):
            unkeyed.append(names[b])
    if show_all:
        print('  unkeyed:', ', '.join(unkeyed) or 'none')
    for _ in range(nnotes):
        f, p = rd(p, fsz)
        s, p = cstr(p)
        print('  note frame %3d  %s' % (f, s))
