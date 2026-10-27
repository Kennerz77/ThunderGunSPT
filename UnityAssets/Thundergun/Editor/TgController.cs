using System.Collections.Generic;
using System.IO;
using System.Linq;
using AnimationEventSystem;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace Thundergun.Editor
{
    /// <summary>
    /// Rebuilds the vanilla Saiga-12K's AnimatorController (reference/saiga_controller.json) around our clips:
    /// the same parameters, layers, masks, states, transitions and event behaviours, so EFT's weapon logic drives
    /// the Thundergun exactly like a vanilla magazine-fed shotgun. The events table (reference/saiga_events.json)
    /// is copied into our own AnimatorControllerStaticData; states keep their index into it.
    /// </summary>
    public static class TgController
    {
        private const int SoundHash = 1554795451;

        /// <param name="sounds">
        /// Hands states whose sound events are replaced (the Saiga's, timed to its own clips) by the given ones:
        /// normalized time and sound event name (looked up as "Snd" + name in the WeaponSoundPlayer).
        /// </param>
        public static AnimatorController Build(string path, Transform rigRoot, Dictionary<string, AnimationClip> handsClips,
            AnimationClip fallback, Dictionary<string, List<(float t, string sound)>> sounds, HashSet<(string from, string to)> contiguous,
            Dictionary<string, float> speeds)
        {
            object json = MiniJson.Parse(File.ReadAllText(TgPaths.Reference("saiga_controller.json")));
            var soundsByList = new Dictionary<int, List<(float, string)>>();
            object handsLayer = json.Get("layers").Arr().First(l => l.Str("name") == "Hands");
            foreach (object S in handsLayer.Get("states").Arr())
            {
                if (!sounds.TryGetValue(S.Str("name"), out var list) || !(S.Get("behaviours") is List<object> behaviours))
                    continue;
                foreach (object b in behaviours.Where(b => b.Str("class") == "AnimationEventsStateBehaviour"))
                    soundsByList[b.Get("fields").Int("EventsListId")] = list;
            }
            AnimatorControllerStaticData events = BuildEvents(Path.ChangeExtension(path, null) + "_events.asset", soundsByList);

            AssetDatabase.DeleteAsset(path);
            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            foreach (object p in json.Get("params").Arr())
            {
                var param = new AnimatorControllerParameter { name = p.Str("name") };
                switch (p.Str("type"))
                {
                    case "Float": param.type = AnimatorControllerParameterType.Float; param.defaultFloat = p.Num("default"); break;
                    case "Int": param.type = AnimatorControllerParameterType.Int; param.defaultInt = p.Int("default"); break;
                    case "Bool": param.type = AnimatorControllerParameterType.Bool; param.defaultBool = p.Bool("default"); break;
                    default: param.type = AnimatorControllerParameterType.Trigger; break;
                }
                controller.AddParameter(param);
            }

            List<string> rigPaths = rigRoot.GetComponentsInChildren<Transform>(true).Select(t => TgRig.PathOf(t, rigRoot)).ToList();
            List<object> layers = json.Get("layers").Arr();
            for (int li = 0; li < layers.Count; li++)
            {
                object L = layers[li];
                string layerName = L.Str("name");
                if (li > 0)
                    controller.AddLayer(layerName);
                AnimatorControllerLayer[] all = controller.layers;
                AnimatorControllerLayer layer = all[li];
                layer.name = layerName;
                layer.defaultWeight = L.Num("defaultWeight");
                layer.blendingMode = L.Int("blending") == 1 ? AnimatorLayerBlendingMode.Additive : AnimatorLayerBlendingMode.Override;
                layer.iKPass = L.Bool("ikPass");
                if (L.Get("mask") is Dictionary<string, object> mask && mask.Count > 0)
                    layer.avatarMask = BuildMask($"{Path.ChangeExtension(path, null)}_{layerName}_mask.mask", mask, rigPaths);
                controller.layers = all;

                bool hands = layerName == "Hands";
                bool lactions = layerName == "LActions";
                AnimatorStateMachine sm = controller.layers[li].stateMachine;
                var states = new Dictionary<string, AnimatorState>();
                List<object> stateList = L.Get("states").Arr();
                for (int si = 0; si < stateList.Count; si++)
                {
                    object S = stateList[si];
                    string stateName = S.Str("name");
                    AnimatorState state = sm.AddState(stateName, new Vector3(250 * (si % 6), 80 * (si / 6), 0));
                    states[stateName] = state;

                    // Hands: our BO1 clips (EFT's speeds were tuned to the Saiga's clip lengths, ours play as authored).
                    // LActions: the idle pose, masked to the left arm, so door/loot gestures keep the hand where it was.
                    // The gun-part layers (selector, hammer, stock, malfunction) animate bones the Thundergun doesn't use.
                    if (hands)
                    {
                        state.motion = handsClips.TryGetValue(stateName, out AnimationClip clip) ? clip : fallback;
                        state.speed = speeds.TryGetValue(stateName, out float speed) ? speed : 1f;
                    }
                    else
                    {
                        state.motion = lactions ? fallback : null;
                        state.speed = S.Num("speed", 1f);
                    }
                    string speedParam = S.Str("speedParam");
                    if (!string.IsNullOrEmpty(speedParam) && !speedParam.StartsWith("#"))
                    {
                        state.speedParameter = speedParam;
                        state.speedParameterActive = true;
                    }
                    state.cycleOffset = S.Num("cycleOffset");
                    state.writeDefaultValues = S.Bool("writeDefaults");
                    state.mirror = S.Bool("mirror");
                    state.iKOnFeet = S.Bool("iKOnFeet");
                    string tag = S.Str("tag");
                    if (!string.IsNullOrEmpty(tag) && !tag.StartsWith("#"))
                        state.tag = tag;

                    if (S.Get("behaviours") is List<object> behaviours)
                    {
                        foreach (object b in behaviours)
                        {
                            object fields = b.Get("fields");
                            switch (b.Str("class"))
                            {
                                case "AnimationEventsStateBehaviour":
                                    var ev = state.AddStateMachineBehaviour<AnimationEventsStateBehaviour>();
                                    ev.FullName = fields.Str("FullName");
                                    ev.FullNameHash = fields.Int("FullNameHash");
                                    ev.EventsListId = fields.Int("EventsListId");
                                    ev.EventsData = events;
                                    ev.EventsContainer = new AnimationEventsStateBehaviour_AnimationEventsContainer();
                                    EditorUtility.SetDirty(ev);
                                    break;
                                case "LActionState":
                                    var la = state.AddStateMachineBehaviour<LActionState>();
                                    la.LActionSetupId = fields.Int("LActionSetupId");
                                    la.StaticData = events;
                                    EditorUtility.SetDirty(la);
                                    break;
                            }
                        }
                    }
                }

                string defaultState = L.Str("defaultState");
                if (defaultState != null && states.TryGetValue(defaultState, out AnimatorState def))
                    sm.defaultState = def;

                foreach (object S in stateList)
                {
                    AnimatorState from = states[S.Str("name")];
                    foreach (object T in S.Get("transitions").Arr())
                    {
                        string to = T.Str("to");
                        AnimatorStateTransition tr = to != null && states.TryGetValue(to, out AnimatorState dest)
                            ? from.AddTransition(dest)
                            : from.AddExitTransition();
                        tr.hasExitTime = T.Bool("hasExitTime");
                        tr.exitTime = T.Num("exitTime");
                        tr.duration = T.Num("duration");
                        tr.offset = T.Num("offset");
                        tr.hasFixedDuration = T.Bool("fixedDuration");
                        tr.interruptionSource = (TransitionInterruptionSource)T.Int("interruption");
                        tr.orderedInterruption = T.Bool("orderedInterruption");
                        tr.canTransitionToSelf = T.Bool("canTransitionToSelf");
                        foreach (object C in T.Get("conditions").Arr())
                            tr.AddCondition(Mode(C.Str("mode")), C.Num("threshold"), C.Str("param"));

                        // Consecutive pieces of one BO1 animation: carry on from the exact last frame, no blend
                        if (hands && to != null && contiguous.Contains((S.Str("name"), to)))
                        {
                            if (tr.hasExitTime)
                                tr.exitTime = 1f;
                            tr.duration = 0f;
                            tr.offset = 0f;
                        }
                    }
                }
                foreach (object T in L.Get("anyState").Arr())
                {
                    string to = T.Str("to");
                    if (to == null || !states.TryGetValue(to, out AnimatorState dest))
                        continue;
                    AnimatorStateTransition tr = sm.AddAnyStateTransition(dest);
                    tr.hasExitTime = T.Bool("hasExitTime");
                    tr.exitTime = T.Num("exitTime");
                    tr.duration = T.Num("duration");
                    tr.hasFixedDuration = T.Bool("fixedDuration");
                    tr.canTransitionToSelf = T.Bool("canTransitionToSelf");
                    foreach (object C in T.Get("conditions").Arr())
                        tr.AddCondition(Mode(C.Str("mode")), C.Num("threshold"), C.Str("param"));
                }
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            Debug.Log($"[Thundergun] Controller {path}: {controller.parameters.Length} parameters, " +
                      string.Join(", ", controller.layers.Select(l => $"{l.name} ({l.stateMachine.states.Length})")));
            return controller;
        }

        private static AnimatorConditionMode Mode(string mode)
        {
            switch (mode)
            {
                case "If": return AnimatorConditionMode.If;
                case "IfNot": return AnimatorConditionMode.IfNot;
                case "Greater": return AnimatorConditionMode.Greater;
                case "Less": return AnimatorConditionMode.Less;
                case "Equals": return AnimatorConditionMode.Equals;
                default: return AnimatorConditionMode.NotEqual;
            }
        }

        /// <summary>
        /// The Saiga's masks are CRC32 path hashes; ours are rebuilt from our own paths. Paths the Saiga doesn't have
        /// (the Thundergun's parts under the weapon bone) take their nearest listed ancestor's weight.
        /// </summary>
        private static AvatarMask BuildMask(string assetPath, Dictionary<string, object> hashes, List<string> paths)
        {
            var weights = hashes.ToDictionary(kv => uint.Parse(kv.Key), kv => (float)(double)kv.Value);
            var mask = new AvatarMask { transformCount = paths.Count };
            for (int i = 0; i < paths.Count; i++)
            {
                mask.SetTransformPath(i, paths[i]);
                bool active = false;
                for (string p = paths[i]; ; p = p.Contains("/") ? p.Substring(0, p.LastIndexOf('/')) : "")
                {
                    if (weights.TryGetValue(p.Length == 0 ? 0u : TgSpace.Crc32(p), out float w))
                    {
                        active = w > 0f;
                        break;
                    }
                    if (p.Length == 0)
                        break;
                }
                mask.SetTransformActive(i, active);
            }
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(mask, assetPath);
            return mask;
        }

        /// <summary>Our copy of the Saiga's events table: the same lists in the same order (states index into it).</summary>
        private static AnimatorControllerStaticData BuildEvents(string assetPath, Dictionary<int, List<(float t, string sound)>> soundsByList)
        {
            object json = MiniJson.Parse(File.ReadAllText(TgPaths.Reference("saiga_events.json")));
            var data = ScriptableObject.CreateInstance<AnimatorControllerStaticData>();
            data._stateHashToEventsCollection = new List<AnimatorControllerStaticData_EventsCollection>();
            List<object> collections = json.Get("_stateHashToEventsCollection").Arr();
            for (int id = 0; id < collections.Count; id++)
            {
                object coll = collections[id];
                var list = new AnimatorControllerStaticData_EventsCollection { _animationEvents = new List<AnimatorControllerStaticData_AnimationEvent>() };
                soundsByList.TryGetValue(id, out var ours);
                foreach (object e in coll.Get("_animationEvents").Arr())
                {
                    if (ours != null && e.Str("_functionName") == "Sound")
                        continue; // timed to the Saiga's clip; ours follow BO1's notetracks
                    object p = e.Get("Parameter");
                    list._animationEvents.Add(new AnimatorControllerStaticData_AnimationEvent
                    {
                        Parameter = new AnimatorControllerStaticData_AnimationEventParameter
                        {
                            BoolParam = (byte)p.Int("BoolParam"), FloatParam = p.Num("FloatParam"), IntParam = p.Int("IntParam"),
                            StringParam = p.Str("StringParam") ?? "", ParamType = p.Int("ParamType"),
                        },
                        _functionName = e.Str("_functionName"),
                        _functionNameHash = e.Int("_functionNameHash"),
                        Enabled = (byte)e.Int("Enabled"),
                        _time = e.Num("_time"),
                        EventConditions = e.Get("EventConditions").Arr().Select(c => new AnimatorControllerStaticData_EventCondition
                        {
                            BoolValue = (byte)c.Int("BoolValue"), FloatValue = c.Num("FloatValue"), IntValue = c.Int("IntValue"),
                            ParameterName = c.Str("ParameterName") ?? "", ConditionParamType = c.Int("ConditionParamType"),
                            ConditionMode = c.Int("ConditionMode"),
                        }).ToList(),
                    });
                }
                foreach (var (t, sound) in ours ?? new List<(float, string)>())
                {
                    list._animationEvents.Add(new AnimatorControllerStaticData_AnimationEvent
                    {
                        // ParamType 3 = string; the hash is EFT's for "Sound" (taken from the Saiga's own events)
                        Parameter = new AnimatorControllerStaticData_AnimationEventParameter { StringParam = sound, ParamType = 3 },
                        _functionName = "Sound",
                        _functionNameHash = SoundHash,
                        Enabled = 1,
                        _time = t,
                        EventConditions = new List<AnimatorControllerStaticData_EventCondition>(),
                    });
                }
                list._animationEvents.Sort((a, b) => a._time.CompareTo(b._time));
                data._stateHashToEventsCollection.Add(list);
            }
            data._stateHashToLActionSetups = new List<AnimatorControllerStaticData_LActionSetup>();
            foreach (object s in json.Get("_stateHashToLActionSetups").Arr())
            {
                object curve = s.Get("LayerWeight");
                var keys = curve.Get("m_Curve").Arr().Select(k => new Keyframe(k.Num("time"), k.Num("value"), k.Num("inSlope"), k.Num("outSlope"), k.Num("inWeight"), k.Num("outWeight"))).ToArray();
                data._stateHashToLActionSetups.Add(new AnimatorControllerStaticData_LActionSetup
                {
                    StartThreshold = s.Num("StartThreshold"),
                    // Sampled over 0..1 only, so the curve's wrap modes don't matter
                    LayerWeight = new AnimationCurve(keys),
                    _stateHash = s.Int("_stateHash"),
                });
            }
            AssetDatabase.DeleteAsset(assetPath);
            AssetDatabase.CreateAsset(data, assetPath);
            return data;
        }
    }
}
