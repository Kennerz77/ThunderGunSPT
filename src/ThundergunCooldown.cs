using System.Collections.Generic;
using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The Thundergun recharges between blasts: after a shot, the next charge in the same canister can't fire for
    /// <see cref="Cooldown"/> seconds. A fresh canister fires as soon as its tech beep ends (see
    /// <see cref="ThundergunCharges"/>), and the dry click once it's spent is instant (the trigger is only held back
    /// while a charge is chambered).
    /// </summary>
    internal static class ThundergunCooldown
    {
        public const float Cooldown = 1.3f;

        // Per gun: when it last fired, and from which canister (a new canister starts with no wait)
        private static readonly Dictionary<string, (float time, string canister)> LastShot = new Dictionary<string, (float, string)>();

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(Player), nameof(Player.OnMakingShot)),
                postfix: new HarmonyMethod(typeof(ThundergunCooldown).GetMethod(nameof(ShotPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
            harmony.Patch(AccessTools.DeclaredMethod(typeof(Player.FirearmController.Idling), nameof(Player.FirearmController.Idling.SetTriggerPressed)),
                new HarmonyMethod(typeof(ThundergunCooldown).GetMethod(nameof(TriggerPrefix), BindingFlags.Static | BindingFlags.NonPublic)));
        }

        /// <summary>Seconds left before this gun may fire again (0 when ready).</summary>
        public static float Remaining(Weapon weapon)
        {
            if (weapon == null || !LastShot.TryGetValue(weapon.Id, out var last) || last.canister != weapon.GetCurrentMagazine()?.Id)
                return 0f;
            return Mathf.Max(0f, last.time + Cooldown - Time.time);
        }

        private static void ShotPostfix(IWeapon weapon)
        {
            if (weapon is Weapon w && ThundergunIds.Is(w))
                LastShot[w.Id] = (Time.time, w.GetCurrentMagazine()?.Id);
        }

        // Returning false swallows the trigger pull: the player just pulls again once it's recharged
        private static bool TriggerPrefix(Player.FirearmController.Idling __instance, bool pressed)
        {
            try
            {
                Weapon weapon = __instance.Weapon;
                if (!pressed || !ThundergunIds.Is(weapon))
                    return true;
                bool charged = weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem != null;
                // A fresh canister waits for its tech beep to finish
                return !charged || (Remaining(weapon) <= 0f && ThundergunCharges.Armed(weapon));
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogError($"Thundergun trigger rule failed, firing normally: {e}");
                return true;
            }
        }
    }
}
