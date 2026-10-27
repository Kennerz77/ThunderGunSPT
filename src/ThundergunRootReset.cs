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

            rootAnim.localRotation = Quaternion.identity;
        }
    }
}
