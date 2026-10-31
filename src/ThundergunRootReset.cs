using System.Collections.Generic;
using System.Reflection;
using EFT.Animations;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// EFT's procedural weapon animation layers sway, look and recoil rotation onto Weapon_root_anim's current local
    /// rotation every frame, trusting the weapon's animation to have put it back to rest first. On the Thundergun
    /// that reset didn't happen, so the rotations piled up and the gun slowly cartwheeled around the player. Our
    /// clips never rotate Weapon_root_anim (the gun moves through the weapon bone below it), so resetting it to rest
    /// here is exactly what the animation should do. Only Thundergun rigs are touched.
    /// The rest pose carries the F12 "View position" sliders, and the gun is sized by <see cref="ThundergunFit"/>.
    /// </summary>
    internal static class ThundergunRootReset
    {
        public static void Enable(Harmony harmony)
        {
            var prefix = new HarmonyMethod(typeof(ThundergunRootReset).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic));
            foreach (string method in new[] { "ApplyComplexRotation", "ApplySimpleRotation" })
                harmony.Patch(AccessTools.Method(typeof(ProceduralWeaponAnimation), method), prefix);
        }

        private static void Prefix(ProceduralWeaponAnimation __instance)
        {
            Transform rootAnim = __instance.HandsContainer?.WeaponRootAnim;
            if (rootAnim == null || !rootAnim.root.name.StartsWith("weapon_thundergun"))
                return;

            rootAnim.localRotation = ThundergunFit.ViewRotation;
            // The view offset goes on top of wherever the game put the root this frame: a root still showing what was
            // written last frame wasn't moved since, so its own place is the remembered one (nothing builds up)
            Vector3 pos = rootAnim.localPosition;
            if (Placed.TryGetValue(rootAnim, out var last) && pos == last.wrote)
                pos = last.own;
            rootAnim.localPosition = pos + ThundergunFit.ViewOffset;
            if (Placed.Count > 64)
                Placed.Clear(); // old rigs from earlier raids: keyed by destroyed transforms
            Placed[rootAnim] = (rootAnim.localPosition, pos);
            ThundergunFit.ApplyView(rootAnim);
        }

        private static readonly Dictionary<Transform, (Vector3 wrote, Vector3 own)> Placed = new Dictionary<Transform, (Vector3, Vector3)>();
    }
}
