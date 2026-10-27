"""Dumps every MonoBehaviour (and ScriptableObject) in a weapon container bundle as JSON, with object references
resolved to readable targets: transforms/GameObjects by hierarchy path, assets by type and name, external files by
index. The editor rebuilds the Thundergun prefab's EFT components from these values.

Usage: python -I dump_components.py out.json bundle [dependency bundles...]
"""
import json
import sys

import UnityPy

env = UnityPy.load(*sys.argv[2:])
by_id = {}
for o in env.objects:
    by_id[(o.assets_file.name if hasattr(o, 'assets_file') else '', o.path_id)] = o
local = {o.path_id: o for o in env.objects}


def go_path(go):
    names = []
    tr = None
    for c in go.m_Component:
        p = c.component if hasattr(c, 'component') else c
        if p.type.name == 'Transform':
            tr = p.read()
            break
    while tr is not None:
        names.append(tr.m_GameObject.read().m_Name)
        tr = tr.m_Father.read() if tr.m_Father and tr.m_Father.path_id else None
    return '/'.join(reversed(names))


def describe(o):
    t = o.type.name
    d = o.read()
    if t == 'GameObject':
        return 'GameObject:' + go_path(d)
    if t == 'Transform':
        return 'Transform:' + go_path(d.m_GameObject.read())
    if t == 'MonoBehaviour':
        cls = d.m_Script.read().m_ClassName
        if d.m_GameObject and d.m_GameObject.path_id:
            return 'MB:%s@%s' % (cls, go_path(d.m_GameObject.read()))
        return 'SO:%s:%s' % (cls, getattr(d, 'm_Name', ''))
    if t in ('MeshRenderer', 'SkinnedMeshRenderer', 'MeshFilter', 'BoxCollider', 'Animator', 'LODGroup'):
        return '%s@%s' % (t, go_path(d.m_GameObject.read()))
    return '%s:%s' % (t, getattr(d, 'm_Name', ''))


def resolve(v):
    if isinstance(v, dict):
        if set(v.keys()) == {'m_FileID', 'm_PathID'}:
            if v['m_PathID'] == 0:
                return None
            if v['m_FileID'] == 0 and v['m_PathID'] in local:
                try:
                    return {'ref': describe(local[v['m_PathID']])}
                except Exception as e:
                    return {'ref': 'unreadable', 'error': str(e)}
            return {'ref': 'external', 'file': v['m_FileID'], 'path': v['m_PathID']}
        return {k: resolve(x) for k, x in v.items()}
    if isinstance(v, list):
        return [resolve(x) for x in v]
    return v


out = []
for o in env.objects:
    if o.type.name != 'MonoBehaviour':
        continue
    try:
        d = o.read()
        cls = d.m_Script.read().m_ClassName
        tt = o.read_typetree()
    except Exception:
        continue
    if cls in ('AnimationEventsStateBehaviour', 'LActionState', 'AnimatorControllerStaticData'):
        continue  # the controller dump covers these
    owner = describe(o)
    fields = {k: resolve(v) for k, v in tt.items() if k not in ('m_GameObject', 'm_Script', 'm_Enabled')}
    out.append({'owner': owner, 'class': cls, 'fields': fields})
json.dump(out, open(sys.argv[1], 'w'), indent=1, default=str)
for c in out:
    print(c['owner'])
