"""Dumps an EFT AnimatorController (compiled form) to JSON with everything needed to rebuild it in the Unity editor:
parameters with defaults, layers, states (speed, tags, write defaults, motions as clip names or blend trees),
transitions (from states and from Any State) with conditions, and each state's AnimationEventsStateBehaviour /
LActionState index into the AnimatorControllerStaticData event lists.

Usage: python -I dump_controller.py out.json controller_bundle [dependency bundles...]
"""
import json
import sys

import UnityPy

PTYPES = {1: 'Float', 3: 'Int', 4: 'Bool', 9: 'Trigger'}
COND = {1: 'If', 2: 'IfNot', 3: 'Greater', 4: 'Less', 6: 'Equals', 7: 'NotEqual'}

env = UnityPy.load(*sys.argv[2:])
ctrl = next(o for o in env.objects if o.type.name == 'AnimatorController')
c = ctrl.read()
tt = ctrl.read_typetree()
tos = {int(k): v for k, v in tt['m_TOS']}
name = lambda h: tos.get(h, '#%d' % h) if h else None

clip_names = []
for p in c.m_AnimationClips:
    try:
        clip_names.append(p.read().m_Name)
    except Exception:
        clip_names.append(None)

C = tt['m_Controller']
vals = C['m_Values']['data']['m_ValueArray']
dv = C['m_DefaultValues']['data']
params = []
for v in vals:
    t = PTYPES.get(v['m_Type'], v['m_Type'])
    idx = v['m_Index']
    default = None
    try:
        default = {'Float': dv['m_FloatValues'], 'Int': dv['m_IntValues'], 'Bool': dv['m_BoolValues'], 'Trigger': dv['m_BoolValues']}[t][idx]
    except Exception:
        pass
    params.append({'name': name(v['m_ID']), 'type': t, 'default': default})


def conditions(t):
    return [{'param': name(cd['data']['m_EventID']), 'mode': COND.get(cd['data']['m_ConditionMode'], cd['data']['m_ConditionMode']),
             'threshold': cd['data']['m_EventThreshold']} for cd in t['m_ConditionConstantArray']]


def transition(t, states):
    d = t['m_DestinationState']
    dest = states[d] if 0 <= d < len(states) else None  # 30000+ = exit / state machine targets
    return {
        'to': dest, 'toIndex': d, 'name': name(t.get('m_FullPathID')), 'conditions': conditions(t),
        'duration': t['m_TransitionDuration'], 'offset': t['m_TransitionOffset'], 'exitTime': t['m_ExitTime'],
        'hasExitTime': bool(t['m_HasExitTime']), 'fixedDuration': bool(t['m_HasFixedDuration']),
        'interruption': t['m_InterruptionSource'], 'orderedInterruption': bool(t['m_OrderedInterruption']),
        'canTransitionToSelf': bool(t['m_CanTransitionToSelf']),
    }


def blend_tree(bt):
    nodes = bt['data']['m_NodeArray']

    def node(i):
        n = nodes[i]['data']
        if n['m_ClipID'] != 4294967295 and not n['m_ChildIndices']:
            cid = n['m_ClipID']
            return {'clip': clip_names[cid] if cid < len(clip_names) else None, 'clipIndex': cid}
        out = {'blendType': n['m_BlendType'], 'param': name(n['m_BlendEventID']), 'paramY': name(n.get('m_BlendEventYID')),
               'children': [node(ci) for ci in n['m_ChildIndices']]}
        b1 = n.get('m_Blend1dData', {}).get('data', {}).get('m_ChildThresholdArray')
        if b1:
            out['thresholds'] = b1
        b2 = n.get('m_Blend2dData', {}).get('data', {}).get('m_ChildPositionArray')
        if b2:
            out['positions'] = [[p['x'], p['y']] for p in b2]
        return out

    return node(0) if nodes else None


layers = []
for layer in C['m_LayerArray']:
    L = layer['data']
    sm = C['m_StateMachineArray'][L['m_StateMachineIndex']]['data']
    snames = [name(s['data']['m_NameID']) for s in sm['m_StateConstantArray']]
    states = []
    for st in sm['m_StateConstantArray']:
        s = st['data']
        trees = [blend_tree(bt) for bt in s['m_BlendTreeConstantArray']]
        states.append({
            'name': name(s['m_NameID']), 'tag': name(s['m_TagID']), 'speed': s['m_Speed'], 'cycleOffset': s['m_CycleOffset'],
            'speedParam': name(s['m_SpeedParamID']), 'mirrorParam': name(s['m_MirrorParamID']), 'cycleParam': name(s['m_CycleOffsetParamID']),
            'timeParam': name(s.get('m_TimeParamID')), 'writeDefaults': bool(s['m_WriteDefaultValues']), 'loop': bool(s['m_Loop']),
            'mirror': bool(s['m_Mirror']), 'iKOnFeet': bool(s['m_IKOnFeet']),
            'motion': trees[0] if trees else None,
            'transitions': [transition(t['data'], snames) for t in s['m_TransitionConstantArray']],
        })
    any_state = [transition(t['data'], snames) for t in sm['m_AnyStateTransitionConstantArray']]
    # Skeleton mask: CRC32 of each transform path (relative to the Animator) -> weight. The editor hashes its own
    # paths the same way to rebuild an AvatarMask.
    mask = {str(e['m_PathHash']): e['m_Weight'] for e in L['m_SkeletonMask']['data']['m_Data']}
    layers.append({
        'name': name(L['m_Binding']), 'blending': L.get('(int&)m_LayerBlendingMode'), 'defaultWeight': L.get('m_DefaultWeight'),
        'mask': mask,
        'ikPass': bool(L.get('m_IKPass')), 'syncedLayerAffectsTiming': bool(L.get('m_SyncedLayerAffectsTiming')),
        'defaultState': snames[sm['m_DefaultState']] if snames else None,
        'states': states, 'anyState': any_state,
    })

# Which state carries which behaviours (events / LAction): the controller's own range table, keyed by layer + state id
def read_behaviour(pptr):
    try:
        obj = pptr.deref() if hasattr(pptr, 'deref') else None
        if obj is None:
            return None
        cls = obj.read().m_Script.read().m_ClassName
        b = obj.read_typetree()
        return {'class': cls, 'fields': {k: v for k, v in b.items() if k not in ('m_GameObject', 'm_Script', 'm_Enabled', 'm_Name', 'EventsData', 'StaticData')}}
    except Exception as e:
        return {'class': None, 'error': str(e)}

behaviour_objs = [read_behaviour(p) for p in c.m_StateMachineBehaviours]
desc = tt['m_StateMachineBehaviourVectorDescription']
indices = desc['m_StateMachineBehaviourIndices']
behaviours = []
for key, rng in desc['m_StateMachineBehaviourRanges']:
    layer_i, state_id = key['m_LayerIndex'], key['m_StateID']
    L = layers[layer_i] if layer_i < len(layers) else None
    sm = C['m_StateMachineArray'][C['m_LayerArray'][layer_i]['data']['m_StateMachineIndex']]['data'] if L else None
    state = None
    if sm:
        for st, s in zip(sm['m_StateConstantArray'], L['states']):
            if state_id in (st['data']['m_FullPathID'], st['data']['m_NameID'], st['data'].get('m_PathID')):
                state = s
                break
    attached = [behaviour_objs[indices[i]] for i in range(rng['m_StartIndex'], rng['m_StartIndex'] + rng['m_Count'])]
    if state is not None:
        state['behaviours'] = attached
    behaviours.append({'layer': L['name'] if L else layer_i, 'state': state['name'] if state else state_id, 'behaviours': attached})

# Layer-default weights live on the controller's layer array (m_LayerArray in the editor form isn't stored)
json.dump({'name': tt['m_Name'], 'params': params, 'layers': layers, 'behaviours': behaviours, 'clips': clip_names},
          open(sys.argv[1], 'w'), indent=1, default=str)
print('params', len(params), 'layers', [(l['name'], len(l['states']), len(l['anyState'])) for l in layers], 'behaviours', len(behaviours))
