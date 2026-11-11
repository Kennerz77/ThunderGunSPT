using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx.Configuration;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The gun's size and how it sits, tuned live from F12 (2026-10-31, to make the size presets). The size shrinks only
    /// the gun (its "weapon" bone) about the right hand. EFT's hand IK pulls the palms onto the weapon_*_IK_marker
    /// bones, which are keyed on the gun by our retarget, so both hands ride the smaller gun through every animation,
    /// reloads included. The arms and the camera aren't scaled. The view, hand, aiming and third person sliders
    /// fine-tune each size; "Log fit" writes the numbers to LogOutput.log so they can be baked into presets.
    /// Players only see the F12 "Size" choice: Default (the sliders' defaults, the tuned fit) or Large (the fit the gun
    /// first shipped with). The sliders show again with TuningMenu, to tune a new fit.
    /// The defaults are the fit tuned in game on 2026-10-31 (size 0.7, 0.95 on the menu character, shrunk towards the grip,
    /// the view raised, the menu carry moved in).
    /// </summary>
    internal static class ThundergunFit
    {
        // The fit sliders show in F12 only while a fit is being tuned; otherwise F12 has the size choice alone
        private const bool TuningMenu = false;

        public static ConfigEntry<GunSize> Choice;

        public static ConfigEntry<float> Size, MenuSize;
        public static ConfigEntry<bool> HandsFollow, HoldPull, HoldDraw;
        public static ConfigEntry<float> MenuRoll;
        private static Slider3 _centre, _view, _viewTurn, _right, _rightTurn, _left, _leftTurn, _aim, _fingerTurn, _fingerBend, _fingerPull, _rightElbow, _leftElbow, _drawLeft, _drawLeftElbow, _menuReach, _menuShift, _menuTurn;
        private static readonly List<Slider3> All = new List<Slider3>();

        public static void Bind(ConfigFile settings)
        {
            Choice = settings.Bind("Size", "Size", GunSize.Default, new ConfigDescription(
                "Default: smaller, fitted to sit like EFT's own guns. Large: full size, as the gun first shipped."));
            // While tuning, the fit sliders are in the mod's own config (F12). Otherwise they live in a config nothing
            // ever saves, set from the size choice
            ConfigFile config = TuningMenu ? settings : new ConfigFile(Path.Combine(BepInEx.Paths.ConfigPath, ThundergunPlugin.PluginGuid + ".fit.unsaved"), false) { SaveOnConfigSet = false };

            int order = 1000;
            const string size = "1. Gun size";
            Size = config.Bind(size, "Size", 0.7f, new ConfigDescription("The gun's size in your hands and on the menu character (1 = full size). The hands stay on it.",
                new AcceptableValueRange<float>(0.25f, 1f), Attributes(order--)));
            config.Bind(size, "Quick sizes", "", new ConfigDescription("Snap the size.", null, new ConfigurationManagerAttributes
            {
                Browsable = TuningMenu, Order = order--, HideDefaultButton = true, CustomDrawer = _ => SizeButtons(),
            }));
            HandsFollow = config.Bind(size, "Hands follow the size", true, new ConfigDescription(
                "On: the hands ride the smaller gun. Off: they stay where they are at full size (to compare).", null, Attributes(order--)));
            _centre = Bind(config, size, "Size centre", new Vector3(-3.3803f, -8.7324f, 10f), 20f, "Where the gun shrinks towards, from the right hand (cm right, up, forward).", ref order);

            const string view = "2. View position";
            _view = Bind(config, view, "Gun", new Vector3(-0.4225f, 10.6338f, 2.4648f), 15f, "The gun and both hands together, in first person (cm right, up, forward).", ref order);
            _viewTurn = Bind(config, view, "Gun turn", Vector3.zero, 45f, "The gun's tilt (degrees pitch, yaw, roll).", ref order, Turn);

            const string right = "3. Right hand";
            _right = Bind(config, right, "Hand", Vector3.zero, 10f, "The right hand on the gun (cm right, up, forward).", ref order);
            _rightTurn = Bind(config, right, "Hand turn", Vector3.zero, 45f, "The right hand's angle (degrees about the hand's own axes).", ref order, Turn);

            _rightElbow = Bind(config, right, "Elbow", Vector3.zero, 30f, "Where the right elbow points (cm right, up, forward): moves the goal the arm bends towards.", ref order);
            _fingerTurn = Bind(config, right, "Trigger finger", Vector3.zero, 60f, "The index finger's first joint, onto the trigger (degrees about the joint's own axes).", ref order, Turn);
            _fingerBend = Bind(config, right, "Trigger finger bend", Vector3.zero, 60f, "The index finger's two outer joints, each turned this much (degrees).", ref order, Turn);
            _fingerPull = Bind(config, right, "Trigger pull", Vector3.zero, 60f, "Added to all three index joints at the height of a shot (degrees): the finger squeezing the trigger.", ref order, Turn);
            HoldPull = config.Bind(right, "Hold the trigger pulled", false, new ConfigDescription("Tuning: keeps the finger at the height of the pull, to set \"Trigger pull\" without shooting.", null, Attributes(order--)));

            const string left = "4. Left hand";
            _left = Bind(config, left, "Hand", Vector3.zero, 10f, "The left hand, wherever the animation puts it (cm right, up, forward).", ref order);
            _leftTurn = Bind(config, left, "Hand turn", Vector3.zero, 45f, "The left hand's angle (degrees about the hand's own axes).", ref order, Turn);
            _leftElbow = Bind(config, left, "Elbow", Vector3.zero, 30f, "Where the left elbow points (cm right, up, forward): moves the goal the arm bends towards.", ref order);
            _drawLeft = Bind(config, left, "Draw: hand", Vector3.zero, 30f, "Extra for the left hand as the gun comes out (cm right, up, forward): full at the start, gone by the end. Putting away runs it backwards.", ref order);
            _drawLeftElbow = Bind(config, left, "Draw: elbow", Vector3.zero, 30f, "Extra for the left elbow as the gun comes out, the same way (cm right, up, forward).", ref order);
            HoldDraw = config.Bind(left, "Hold the draw offsets", false, new ConfigDescription("Tuning: keeps the draw extras on all the time, to see them standing still.", null, Attributes(order--)));

            const string aim = "5. Aiming";
            _aim = Bind(config, aim, "Eye", Vector3.zero, 10f, "The eye while aiming (cm right, up, forward). Back (minus) shows more of the gun.", ref order);

            const string menu = "6. Third person";
            MenuSize = config.Bind(menu, "Size", 0.95f, new ConfigDescription("The gun's size on the menu character.",
                new AcceptableValueRange<float>(0.25f, 1f), Attributes(order--)));
            _menuReach = Bind(config, menu, "Front grip", new Vector3(2.1f, -28.5366f, 34.0901f), 60f, "Where the menu character's left hand holds the front grip, from the left shoulder (cm right, up, forward in the body's frame).", ref order);
            _menuShift = Bind(config, menu, "Gun", new Vector3(-2.3662f, -0.2f, 5.0174f), 40f, "The whole gun moved off the torso (cm right, up, forward in the body's frame).", ref order);
            _menuTurn = Bind(config, menu, "Gun turn", Vector3.zero, 90f, "The whole carry turned about the right hand, after the front grip is placed (degrees in the body's frame).", ref order, Turn);
            MenuRoll = config.Bind(menu, "Gun roll", -5.07f, new ConfigDescription("The gun rolled about its barrel line so it sits upright (degrees).", new AcceptableValueRange<float>(-90f, 90f), Attributes(order--)));

            const string debug = "7. Fit tools";
            config.Bind(debug, "Log fit", "", new ConfigDescription("Writes every number above to BepInEx\\LogOutput.log as a [fit] line.", null, new ConfigurationManagerAttributes
            {
                Browsable = TuningMenu, Order = order--, HideDefaultButton = true, CustomDrawer = _ => Button("Log fit", LogFit),
            }));
            config.Bind(debug, "Reset fit", "", new ConfigDescription("Puts every fit setting back to the defaults.", null, new ConfigurationManagerAttributes
            {
                Browsable = TuningMenu, Order = order--, HideDefaultButton = true, CustomDrawer = _ => Button("Reset fit", ResetFit),
            }));

            // Large: the fit before the sizes (release 1.1), every other slider at 0
            Large = new Dictionary<Slider3, Vector3>
            {
                [_menuReach] = new Vector3(2.1f, -29.1f, 32.4f),
                [_menuShift] = new Vector3(-8.0f, -0.2f, 9.9f),
            };
            Choice.SettingChanged += (_, __) => ApplyChoice();
            if (!TuningMenu)
                ApplyChoice();
        }

        private static Dictionary<Slider3, Vector3> Large;

        /// <summary>The fit for the chosen size: Default is the sliders' defaults, Large the fit the gun first shipped with.</summary>
        private static void ApplyChoice()
        {
            bool large = Choice.Value == GunSize.Large;
            Size.Value = large ? 1f : (float)Size.DefaultValue;
            MenuSize.Value = large ? 1f : (float)MenuSize.DefaultValue;
            foreach (Slider3 s in All)
                s.Set(!large ? s.Default : Large.TryGetValue(s, out Vector3 v) ? v : Vector3.zero);
        }

        // ---------- Reading the fit ----------

        /// <summary>The view offset in the weapon root's space (EFT's weapon space: +X right, -Y forward, +Z up), metres.</summary>
        public static Vector3 ViewOffset => Cm(_view.Value);

        public static Quaternion ViewRotation
        {
            get
            {
                Vector3 t = _viewTurn.Value;
                return Quaternion.Euler(t.x, t.z, -t.y);
            }
        }

        /// <summary>The menu carry's front grip target from the left shoulder, in the body's frame (x right, y up, z forward), metres.</summary>
        public static Vector3 MenuReach => _menuReach.Value * 0.01f;

        /// <summary>The menu carry's shift off the torso, in the body's frame, metres.</summary>
        public static Vector3 MenuShift => _menuShift.Value * 0.01f;

        /// <summary>The carry's extra turn about the right hand, in the body's frame.</summary>
        public static Quaternion MenuTurn => Quaternion.Euler(_menuTurn.Value);

        /// <summary>The aiming eye's offset, in <paramref name="weapon"/>'s local space (true centimetres, whatever its size).</summary>
        public static Vector3 AimShift(Transform weapon, Transform frame) =>
            weapon == null || frame == null ? Vector3.zero : weapon.InverseTransformVector(frame.TransformVector(Cm(_aim.Value)));

        /// <summary>(right, up, forward) cm to EFT's weapon space (+X right, -Y forward, +Z up), metres.</summary>
        private static Vector3 Cm(Vector3 v) => new Vector3(v.x, -v.z, v.y) * 0.01f;

        // ---------- First person ----------

        /// <summary>
        /// First person, right after the weapon root gets its rest pose: the gun sized about the right hand, then the
        /// hands' fit on top (or, with "Hands follow" off, the hands kept where they'd be at full size).
        /// </summary>
        public static void ApplyView(Transform rootAnim)
        {
            Rig rig = RigOf(rootAnim.root);
            if (rig == null)
                return;
            Transform frame = rootAnim.parent;
            float s = Size.Value;
            Vector3 pivot = Resize(rig, frame, s);
            Quaternion turn = GunTurn(rig, frame);
            Nudge(rig.RightIk, rig.RightHand, frame, turn, pivot, s, _right.Value, _rightTurn.Value);
            float draw = DrawWeight(rig);
            Nudge(rig.LeftIk, rig.LeftHand, frame, turn, pivot, s, _left.Value + _drawLeft.Value * draw, _leftTurn.Value);
            Bend(rig.RightBend, frame, turn, _rightElbow.Value);
            Bend(rig.LeftBend, frame, turn, _leftElbow.Value + _drawLeftElbow.Value * draw);
            Trigger(rig);
            Aim(rig, frame);
        }

        /// <summary>The aim camera (the eye while aiming) moved by the aiming sliders, from its place on the gun.</summary>
        private static void Aim(Rig rig, Transform frame)
        {
            if (rig.Aim == null)
                return;
            var (pos, rot) = Fresh(rig.Aim);
            rig.Aim.localPosition = pos + AimShift(rig.Weapon, frame);
            rig.Aim.localRotation = rot;
            Remember(rig.Aim, pos, rot, false);
        }

        // ---------- Third person ----------

        /// <summary>The menu character's gun sized about its right hand, before it's hung off the hand.</summary>
        public static void ApplyMenu(Transform model, Transform rootAnim)
        {
            Rig rig = RigOf(model);
            if (rig != null)
                Resize(rig, rootAnim, MenuSize.Value);
        }

        // ---------- Sizing ----------

        /// <summary>
        /// Scales the weapon bone about the right hand (plus the size centre), from where the animation put it this
        /// frame. Every point of the gun, the hand markers included, moves to pivot + size * (point - pivot).
        /// </summary>
        private static Vector3 Resize(Rig rig, Transform frame, float s)
        {
            Transform weapon = rig.Weapon;
            var (pos, rot) = Fresh(weapon);
            weapon.localPosition = pos;
            weapon.localRotation = rot;
            weapon.localScale = Vector3.one;
            Vector3 pivot = (rig.RightIk != null ? rig.RightIk.position : weapon.position) + frame.TransformVector(Cm(_centre.Value));
            if (!Mathf.Approximately(s, 1f))
            {
                Vector3 m = weapon.InverseTransformPoint(pivot);
                weapon.localScale = Vector3.one * s;
                weapon.localPosition = pos + rot * (m * (1f - s));
            }
            Remember(weapon, pos, rot, true);
            return pivot;
        }

        private static readonly int IdleHash = Animator.StringToHash("IDLE"), FireHash = Animator.StringToHash("FIRE");

        /// <summary>
        /// How far the gun is turned, in the view's frame, from its idle pose at the hip (where the hand sliders are
        /// set). The hand offsets ride this turn, so a hand stays where it was put on the gun while an inspect or a
        /// reload swings the gun round, and while the hip tilt eases out for aiming. The idle pose is read under the
        /// weapon root (the animation's part alone), then put at the hip tilt.
        /// </summary>
        private static Quaternion GunTurn(Rig rig, Transform frame)
        {
            Transform weapon = rig.Weapon;
            if (rig.Anim != null && rig.Anim.layerCount > 1 && rig.Anim.GetCurrentAnimatorStateInfo(1).shortNameHash == IdleHash)
            {
                rig.Idle = Quaternion.Inverse(weapon.parent.rotation) * weapon.rotation;
                rig.IdleSeen = true;
            }
            if (!rig.IdleSeen)
                return Quaternion.identity;
            Quaternion now = Quaternion.Inverse(frame.rotation) * weapon.rotation;
            return now * Quaternion.Inverse(ViewRotation * rig.Idle);
        }

        /// <summary>One hand's markers (EFT pulls the palm onto the IK one, checks the grip against the other), moved together.</summary>
        private static void Nudge(Transform ik, Transform hand, Transform frame, Quaternion gunTurn, Vector3 pivot, float s, Vector3 cm, Vector3 turn)
        {
            if (ik == null)
                return;
            var (pos, rot) = Fresh(ik);
            ik.localPosition = pos;
            ik.localRotation = rot;
            Vector3 at = ik.position;
            if (!HandsFollow.Value && s > 0f)
                at = pivot + (at - pivot) / s;
            at += frame.TransformVector(gunTurn * Cm(cm));
            Quaternion facing = ik.rotation * Quaternion.Euler(turn);
            ik.SetPositionAndRotation(at, facing);
            Remember(ik, pos, rot, false);
            if (hand == null)
                return;
            (pos, rot) = Fresh(hand);
            hand.SetPositionAndRotation(at, facing);
            Remember(hand, pos, rot, false);
        }

        /// <summary>An elbow's bend goal (EFT bends each arm towards Bend_Goal_*, keyed on the gun) moved by its sliders.</summary>
        private static void Bend(Transform goal, Transform frame, Quaternion gunTurn, Vector3 cm)
        {
            if (goal == null)
                return;
            var (pos, rot) = Fresh(goal);
            goal.localPosition = pos;
            goal.localRotation = rot;
            goal.position += frame.TransformVector(gunTurn * Cm(cm));
            Remember(goal, pos, rot, false);
        }

        private static readonly int[] DrawIn = { Animator.StringToHash("OUT TO IDLE"), Animator.StringToHash("OUT TO IDLE ARM") };
        private static readonly int[] DrawOut = { Animator.StringToHash("IDLE TO OUT"), Animator.StringToHash("FAST IDLE TO OUT") };

        /// <summary>How much of the draw extras to add: 1 as the gun comes out, easing to 0 as the draw ends (rising again on a put-away).</summary>
        private static float DrawWeight(Rig rig)
        {
            if (HoldDraw.Value)
                return 1f;
            if (rig.Anim == null || rig.Anim.layerCount < 2)
                return 0f;
            AnimatorStateInfo state = rig.Anim.GetCurrentAnimatorStateInfo(1);
            float t = Mathf.Clamp01(state.normalizedTime);
            float ease = t * t * (3f - 2f * t);
            if (System.Array.IndexOf(DrawIn, state.shortNameHash) >= 0)
                return 1f - ease;
            if (System.Array.IndexOf(DrawOut, state.shortNameHash) >= 0)
                return ease;
            return 0f;
        }

        // ---------- Trigger finger ----------

        // The pull: in quickly as the shot goes, out again slower
        private const float PullIn = 0.05f, PullOut = 0.15f;

        /// <summary>
        /// The right index finger on the trigger. EFT's hand poser copies the fingers from the gun rig's palm (no grip
        /// pose on our guns), so turning the rig's Digit21-23 moves the real finger. A shot (the gun entering its FIRE
        /// state) squeezes it by the pull sliders.
        /// </summary>
        private static void Trigger(Rig rig)
        {
            if (rig.Index == null)
                return;
            bool firing = rig.Anim != null && rig.Anim.layerCount > 1 && rig.Anim.GetCurrentAnimatorStateInfo(1).shortNameHash == FireHash;
            if (firing && !rig.Firing)
                rig.ShotAt = Time.time;
            rig.Firing = firing;
            float t = Time.time - rig.ShotAt;
            float pull = HoldPull.Value ? 1f : t < PullIn ? t / PullIn : Mathf.Clamp01(1f - (t - PullIn) / PullOut);
            Vector3 squeeze = _fingerPull.Value * pull;
            Curl(rig.Index[0], _fingerTurn.Value + squeeze);
            Curl(rig.Index[1], _fingerBend.Value + squeeze);
            Curl(rig.Index[2], _fingerBend.Value + squeeze);
        }

        private static void Curl(Transform joint, Vector3 euler)
        {
            if (joint == null)
                return;
            var (pos, rot) = Fresh(joint);
            joint.localPosition = pos;
            joint.localRotation = rot * Quaternion.Euler(euler);
            Remember(joint, pos, rot, false);
        }

        // ---------- Keeping the animation's pose ----------

        // Bones this moved: what was written, the animation's pose under it, the frame it was last applied, and whether
        // it was scaled. A bone the animation rewrites shows something else next frame; one it leaves alone still shows
        // what was written, and its own pose is the remembered one (so nothing builds up either way)
        private struct Written
        {
            public Vector3 WrotePos, Pos;
            public Quaternion WroteRot, Rot;
            public int Frame;
            public bool Scaled;
        }

        private static readonly Dictionary<Transform, Written> Moved = new Dictionary<Transform, Written>();

        private static (Vector3, Quaternion) Fresh(Transform t)
        {
            if (Moved.TryGetValue(t, out Written w) && t.localPosition == w.WrotePos && t.localRotation == w.WroteRot)
                return (w.Pos, w.Rot);
            return (t.localPosition, t.localRotation);
        }

        private static void Remember(Transform t, Vector3 pos, Quaternion rot, bool scaled) =>
            Moved[t] = new Written { WrotePos = t.localPosition, WroteRot = t.localRotation, Pos = pos, Rot = rot, Frame = Time.frameCount, Scaled = scaled };

        private static readonly List<Transform> Stale = new List<Transform>();

        /// <summary>
        /// Every frame: a model no longer being fitted (dropped as loot, back in the pool, the menu gone) gets its
        /// animation's pose and full size back, so a pooled model never shows up small somewhere else.
        /// </summary>
        public static void Restore()
        {
            if (Moved.Count == 0)
                return;
            Stale.Clear();
            foreach (KeyValuePair<Transform, Written> kv in Moved)
            {
                if (kv.Key == null || Time.frameCount - kv.Value.Frame > 3)
                    Stale.Add(kv.Key);
            }
            foreach (Transform t in Stale)
            {
                Written w = Moved[t];
                Moved.Remove(t);
                if (t == null)
                    continue;
                if (t.localPosition == w.WrotePos && t.localRotation == w.WroteRot)
                {
                    t.localPosition = w.Pos;
                    t.localRotation = w.Rot;
                }
                if (w.Scaled)
                    t.localScale = Vector3.one;
            }
            if (Rigs.Count > 32)
                Rigs.Clear(); // old models from earlier raids: keyed by destroyed transforms
        }

        // ---------- The model's bones ----------

        private class Rig
        {
            public Animator Anim;
            public Transform[] Index;
            public Quaternion Idle;
            public bool IdleSeen, Firing;
            public float ShotAt = -10f;
            public Transform RightBend, LeftBend;
            public Transform Weapon, RightIk, RightHand, LeftIk, LeftHand, Aim;
        }

        private static readonly Dictionary<Transform, Rig> Rigs = new Dictionary<Transform, Rig>();

        private static Rig RigOf(Transform model)
        {
            if (model == null)
                return null;
            if (Rigs.TryGetValue(model, out Rig rig))
                return rig;
            Transform[] all = model.GetComponentsInChildren<Transform>(true);
            Transform weapon = all.FirstOrDefault(t => t.name == "weapon" && t.parent != null && t.parent.name == "Weapon_root_anim");
            rig = weapon == null ? null : new Rig
            {
                Weapon = weapon,
                RightIk = all.FirstOrDefault(t => t.name == "weapon_R_IK_marker"),
                RightHand = all.FirstOrDefault(t => t.name == "weapon_R_hand_marker"),
                LeftIk = all.FirstOrDefault(t => t.name == "weapon_L_IK_marker"),
                LeftHand = all.FirstOrDefault(t => t.name == "weapon_L_hand_marker"),
                Anim = model.GetComponentInChildren<Animator>(true),
                RightBend = all.FirstOrDefault(t => t.name == "Bend_Goal_Right" && t.parent == weapon),
                LeftBend = all.FirstOrDefault(t => t.name == "Bend_Goal_Left" && t.parent == weapon),
                Index = new[] { "Base HumanRDigit21", "Base HumanRDigit22", "Base HumanRDigit23" }.Select(n => all.FirstOrDefault(t => t.name == n)).ToArray(),
                Aim = all.FirstOrDefault(t => t.name == "aim_camera" && t.parent == weapon),
            };
            Rigs[model] = rig;
            if (rig == null)
                ThundergunPlugin.Log.LogWarning($"Thundergun fit: no weapon bone under {model.name}");
            return rig;
        }

        // ---------- F12 ----------

        private static void LogFit()
        {
            ThundergunPlugin.Log.LogInfo($"[fit] Size {Size.Value:F3} | follow {HandsFollow.Value} | " +
                                     $"menu size {MenuSize.Value:F3} | " + string.Join(" | ", All.Select(s => $"{s.Name} {s}")) + $" | menu roll {MenuRoll.Value:F3}f");
        }

        private static void ResetFit()
        {
            Size.Value = (float)Size.DefaultValue;
            HandsFollow.Value = (bool)HandsFollow.DefaultValue;
            HoldPull.Value = false;
            HoldDraw.Value = false;
            MenuSize.Value = (float)MenuSize.DefaultValue;
            MenuRoll.Value = (float)MenuRoll.DefaultValue;
            foreach (Slider3 s in All)
                s.Reset();
        }

        private static readonly (string label, float size)[] QuickSizes = { ("Full", 1f), ("3/4", 0.75f), ("Half", 0.5f), ("Quarter", 0.25f) };

        private static void SizeButtons()
        {
            GUILayout.BeginHorizontal();
            foreach (var (label, size) in QuickSizes)
            {
                if (GUILayout.Button(label))
                    Size.Value = size;
            }
            GUILayout.EndHorizontal();
        }

        private static void Button(string label, Action action)
        {
            if (GUILayout.Button(label, GUILayout.ExpandWidth(true)))
                action();
        }

        private static readonly string[] Move = { "right", "up", "forward" };
        private static readonly string[] Turn = { "pitch", "yaw", "roll" };

        private static Slider3 Bind(ConfigFile config, string section, string name, Vector3 defaults, float range, string description, ref int order, string[] axes = null)
        {
            var s = new Slider3($"{section.Substring(3)}: {name}");
            axes = axes ?? Move;
            for (int i = 0; i < 3; i++)
                s.Axes[i] = config.Bind(section, $"{name} {axes[i]}", defaults[i], new ConfigDescription(description, new AcceptableValueRange<float>(-range, range), Attributes(order--)));
            All.Add(s);
            return s;
        }

        private static ConfigurationManagerAttributes Attributes(int order) => new ConfigurationManagerAttributes { Browsable = TuningMenu, Order = order };

        /// <summary>Three sliders read as one vector.</summary>
        private sealed class Slider3
        {
            public readonly string Name;
            public readonly ConfigEntry<float>[] Axes = new ConfigEntry<float>[3];

            public Slider3(string name) => Name = name;

            public Vector3 Value => new Vector3(Axes[0].Value, Axes[1].Value, Axes[2].Value);

            public Vector3 Default => new Vector3((float)Axes[0].DefaultValue, (float)Axes[1].DefaultValue, (float)Axes[2].DefaultValue);

            public void Set(Vector3 v)
            {
                for (int i = 0; i < 3; i++)
                    Axes[i].Value = v[i];
            }

            public void Reset()
            {
                foreach (ConfigEntry<float> a in Axes)
                    a.Value = (float)a.DefaultValue;
            }

            public override string ToString() => $"({Axes[0].Value:F3}f, {Axes[1].Value:F3}f, {Axes[2].Value:F3}f)";
        }
    }

    /// <summary>The F12 size choice.</summary>
    internal enum GunSize
    {
        Default,
        Large,
    }

    /// <summary>Read by BepInEx's Configuration Manager (F12) by name: entry order, visibility and custom drawers.</summary>
    internal sealed class ConfigurationManagerAttributes
    {
        public bool? Browsable;
        public int? Order;
        public bool? HideDefaultButton;
        public Action<ConfigEntryBase> CustomDrawer;
    }

    /// <summary>Puts models that stopped being fitted back to their own size and pose.</summary>
    internal class ThundergunFitKeeper : MonoBehaviour
    {
        private void Update() => ThundergunFit.Restore();
    }
}
