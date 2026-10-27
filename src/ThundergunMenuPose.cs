using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The character on the raid confirmation and loading screens. The game poses it with the gun's own animations:
    /// "PATROL" to hold it, and every few seconds a random weapon move (look, check ammo, reload). The Thundergun's
    /// animations are all first-person ones (its PATROL is BO3's sprint loop), which swing the gun around and leave the
    /// body's hands reaching for nothing. So on these screens it holds still: the gun is posed once in its idle, its
    /// animator is stopped, and the character skips the random weapon moves.
    /// First person is untouched.
    /// </summary>
    internal static class ThundergunMenuPose
    {
        // Animators this froze: only these are switched back on when their model is built again (ThundergunTracker),
        // so models the game keeps still on purpose (PaP's display gun, loot) stay still
        public static readonly HashSet<Animator> Frozen = new HashSet<Animator>();

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(PlayerModelLoader), nameof(PlayerModelLoader.CreateWeapon)),
                postfix: new HarmonyMethod(Method(nameof(CreateWeaponPostfix))));
            harmony.Patch(AccessTools.Method(typeof(MenuPlayerPoser), nameof(MenuPlayerPoser.OnRandomizeAnimation)),
                new HarmonyMethod(Method(nameof(RandomizePrefix))));
            harmony.Patch(AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.Kinematics)),
                postfix: new HarmonyMethod(Method(nameof(KinematicsPostfix))));
            harmony.Patch(AccessTools.Method(typeof(PlayerBones), nameof(PlayerBones.ShiftWeaponRoot)),
                postfix: new HarmonyMethod(Method(nameof(ShiftWeaponRootPostfix))));
        }

        private static MethodInfo Method(string name) => typeof(ThundergunMenuPose).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // Bones of the menu characters holding the Thundergun; in-raid bodies never get in here
        private static readonly Dictionary<PlayerBones, Holding> Holds = new Dictionary<PlayerBones, Holding>();

        /// <summary>
        /// On a menu character holding the Thundergun (it also skips the random weapon moves). Each frame the poser
        /// places the gun, then sends both hands to their markers: either it hangs the gun off the body's right hand
        /// by the right marker (PlayerBones.Kinematics), or the body's third person animation places it
        /// (ShiftWeaponRoot) and the right hand reaches for it. The Thundergun is so long that the body's left arm
        /// can't reach the front grip either way (first person moves the shoulder forward instead), so right after
        /// the gun is placed it is swung about the right marker until its front grip sits just in front of the left
        /// shoulder: a cross-body carry with both hands on the grips. Worked out fresh every frame from where the
        /// game just put the gun, so nothing builds up (an earlier version turned the marker a little each frame,
        /// which spun the right hand forever whenever the animation, not the hand, placed the gun).
        /// </summary>
        private class Holding : MonoBehaviour
        {
            public MenuPlayerPoser Poser;
            public Transform RightMarker;
            public Transform LeftMarker;

            // The carry, in the body's frame (x right, y up, z forward), metres: the front grip low and ahead of the
            // left shoulder, the whole gun nudged forward and to the left, off the torso, and a slight roll about the
            // barrel line so it sits upright
            private static readonly Vector3 Reach = new Vector3(0.021f, -0.291f, 0.324f);
            private static readonly Vector3 Shift = new Vector3(-0.080f, -0.002f, 0.099f);
            private const float Roll = -5.07f;

            public void Swing()
            {
                Transform shoulder = Poser?.PBones?.LeftShoulder?.Original;
                Transform root = Poser?.PBones?.Weapon_Root_Anim;
                if (shoulder == null || root == null || LeftMarker == null || RightMarker == null)
                    return;
                Quaternion body = Poser.transform.rotation;
                // Off the torso first (the right hand follows its marker)
                root.position += body * Shift;
                Vector3 pivot = RightMarker.position;
                Vector3 target = shoulder.position + body * Reach;
                Vector3 now = LeftMarker.position - pivot, want = target - pivot;
                if (now.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f)
                    return;
                // Front grip onto the target, then rolled about the barrel line to sit upright
                Quaternion swing = Quaternion.AngleAxis(Roll, want.normalized) * Quaternion.FromToRotation(now, want);
                root.SetPositionAndRotation(pivot + swing * (root.position - pivot), swing * root.rotation);
            }

            private void OnDestroy()
            {
                if (Poser?.PBones != null && Holds.TryGetValue(Poser.PBones, out Holding hold) && hold == this)
                    Holds.Remove(Poser.PBones);
            }
        }

        // The gun hangs off the right hand: swing it about there
        private static void KinematicsPostfix(PlayerBones __instance)
        {
            if (Holds.TryGetValue(__instance, out Holding hold) && hold != null)
                hold.Swing();
        }

        // The body animation placed the gun: hang it off the right hand instead (as the other branch does, so the
        // hold looks the same whichever the menu animation picks), which swings it via KinematicsPostfix
        private static void ShiftWeaponRootPostfix(PlayerBones __instance)
        {
            if (Holds.TryGetValue(__instance, out Holding hold) && hold != null && hold.RightMarker != null)
                __instance.Kinematics(hold.RightMarker, 0f);
        }

        private static void CreateWeaponPostfix(PlayerModelLoader __instance, Item weapon)
        {
            try
            {
                MenuPlayerPoser poser = __instance.ModelPlayerPoser;
                bool thundergun = ThundergunIds.Is(weapon);
                Holding hold = poser?.GetComponent<Holding>();
                if (!thundergun)
                {
                    if (hold != null)
                    {
                        Holds.Remove(poser.PBones);
                        Object.Destroy(hold);
                    }
                    return;
                }
                // The game's animator wrapper drives this same Unity Animator
                Animator animator = __instance._weaponPrefab?.GetComponentInChildren<Animator>(true);
                if (animator == null || animator.layerCount < 2)
                    return;
                animator.enabled = true;
                animator.Play("IDLE", 1, 0f);
                animator.Update(0f);
                // Frozen in that pose: nothing the menu sets afterwards (patrol, reload triggers) moves it
                animator.enabled = false;
                Frozen.Add(animator);

                if (poser == null)
                    return;
                hold = hold ?? poser.gameObject.AddComponent<Holding>();
                hold.Poser = poser;
                Transform[] all = animator.GetComponentsInChildren<Transform>(true);
                hold.RightMarker = all.FirstOrDefault(t => t.name == "weapon_R_IK_marker") ?? all.FirstOrDefault(t => t.name == "weapon_R_hand_marker");
                hold.LeftMarker = all.FirstOrDefault(t => t.name == "weapon_L_IK_marker") ?? all.FirstOrDefault(t => t.name == "weapon_L_hand_marker");
                if (poser.PBones != null)
                    Holds[poser.PBones] = hold;
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Thundergun menu pose skipped: {e.Message}");
            }
        }

        // The character just stands holding it
        private static bool RandomizePrefix(MenuPlayerPoser __instance) => __instance.GetComponent<Holding>() == null;
    }
}
