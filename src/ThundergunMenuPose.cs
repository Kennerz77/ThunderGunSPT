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
        }

        private static MethodInfo Method(string name) => typeof(ThundergunMenuPose).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        /// <summary>
        /// On a menu character holding the Thundergun (it also skips the random weapon moves). The menu hangs the gun
        /// off the body's right hand by the right hand marker; the left hand then reaches for its marker on the front
        /// grip. The Thundergun is so long that the body's left arm can't reach it (first person moves the shoulder
        /// forward instead), so the gun is swung about the right hand, a little each frame, until its front grip
        /// points to just in front of the left shoulder: a cross-body carry with both hands on the grips. Done by
        /// turning the right hand marker the other way, since the poser lines the gun up by it.
        /// </summary>
        private class Holding : MonoBehaviour
        {
            public MenuPlayerPoser Poser;
            public Transform[] RightMarkers = new Transform[0];
            public Transform LeftMarker;

            // Where the front grip should be: ahead of and a little below the left shoulder, an easy reach
            private static readonly Vector3 ReachAhead = new Vector3(0f, -0.12f, 0.32f);

            // Before the poser's LateUpdate lines the gun up by the right marker
            private void Update()
            {
                Transform shoulder = Poser?.PBones?.LeftShoulder?.Original;
                Transform palm = Poser?.PBones?.RightPalm;
                if (shoulder == null || palm == null || LeftMarker == null || RightMarkers.Length == 0)
                    return;
                Transform body = Poser.transform;
                Vector3 pivot = palm.position;
                Vector3 target = shoulder.position + body.rotation * ReachAhead;
                Vector3 now = LeftMarker.position - pivot, want = target - pivot;
                if (now.sqrMagnitude < 1e-6f || want.sqrMagnitude < 1e-6f)
                    return;
                // A fraction of the way each frame: settles in a few frames and stays put
                Quaternion swing = Quaternion.Slerp(Quaternion.identity, Quaternion.FromToRotation(now, want), 0.35f);
                Quaternion back = Quaternion.Inverse(swing);
                foreach (Transform marker in RightMarkers)
                {
                    if (marker != null)
                        marker.rotation = back * marker.rotation;
                }
            }
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
                        Object.Destroy(hold);
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
                hold.RightMarkers = all.Where(t => t.name == "weapon_R_IK_marker" || t.name == "weapon_R_hand_marker").ToArray();
                hold.LeftMarker = all.FirstOrDefault(t => t.name == "weapon_L_IK_marker") ?? all.FirstOrDefault(t => t.name == "weapon_L_hand_marker");
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
