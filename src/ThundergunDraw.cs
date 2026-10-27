using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.InputSystem;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// A Thundergun bought off the wall into an empty primary slot goes straight into the player's hands. In the
    /// hideout's weapons mode the hands hold a copy of the gear made when the mode started, so the new gun is copied
    /// into it first. With Hideout Uncensored (weapons anywhere in the hideout) it's drawn through its slot key, as
    /// that mod expects; otherwise only in the shooting range, where the game allows weapons.
    /// </summary>
    internal static class ThundergunDraw
    {
        private static readonly string[] Slots = { "FirstPrimaryWeapon", "SecondPrimaryWeapon" };

        /// <summary>The primary slots empty right now (taken before the purchase is sent: the server fills the first).</summary>
        public static string[] EmptySlots(HideoutPlayerOwner owner)
        {
            HideoutPlayer player = owner?.HideoutPlayer;
            return player == null ? new string[0] : Slots.Where(s => RealSlot(player, s)?.ContainedItem == null).ToArray();
        }

        /// <summary>True if drawing the bought gun was started (a slot was free, so it was equipped, not stashed).</summary>
        public static bool TryDrawBought(HideoutPlayerOwner owner, string[] emptyBefore)
        {
            if (owner?.HideoutPlayer == null || emptyBefore == null || emptyBefore.Length == 0)
                return false;
            ThundergunPlugin.Runner.StartCoroutine(DrawWhenEquipped(owner, emptyBefore));
            return true;
        }

        private static Slot RealSlot(HideoutPlayer player, string id) =>
            (player.OriginalInventory?.Inventory ?? player.Inventory)?.Equipment?.GetAllSlots().FirstOrDefault(s => s.ID == id);

        private static IEnumerator DrawWhenEquipped(HideoutPlayerOwner owner, string[] slots)
        {
            HideoutPlayer player = owner.HideoutPlayer;
            // The server's reply is applied to the profile's inventory as it arrives: wait for the gun (2 s at most)
            Weapon gun = null;
            string slotId = null;
            for (float t = 0f; t < 2f && gun == null && player != null; t += Time.unscaledDeltaTime)
            {
                foreach (string s in slots)
                {
                    if (RealSlot(player, s)?.ContainedItem is Weapon w && ThundergunIds.Is(w))
                    {
                        gun = w;
                        slotId = s;
                    }
                }
                if (gun == null)
                    yield return null;
            }
            if (gun == null)
            {
                Notify("Thundergun bought.");
                yield break;
            }

            try
            {
                // Weapons mode: the hands' copy of the gear gets the new gun too
                if (owner.InShootingRange && player.ShootingRangeInventory is InventoryController copies)
                {
                    Slot copySlot = copies.Inventory?.Equipment?.GetAllSlots().FirstOrDefault(s => s.ID == slotId);
                    if (copySlot != null && copySlot.ContainedItem == null)
                    {
                        var added = ItemManipulator.AddWithoutRestrictions(gun.CloneItem(), copySlot.CreateItemAddress(), copies);
                        if (added.Failed)
                            ThundergunPlugin.Log.LogWarning($"Couldn't put the bought Thundergun into the weapons-mode gear: {added.Error}");
                    }
                }
                if (HideoutUncensored.Draw(owner, slotId))
                    Notify("Thundergun bought and equipped.");
                else if (owner.InShootingRange && Enum.TryParse(slotId, out EquipmentSlot slot))
                {
                    player.SetSlotItem(slot, _ => { });
                    Notify("Thundergun bought and equipped.");
                }
                else
                    Notify("Thundergun bought and equipped (draw it in the shooting range).");
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Drawing the bought Thundergun failed: {e.Message}");
                Notify("Thundergun bought and equipped.");
            }
        }

        private static void Notify(string message) => EFT.Communications.NotificationManager.DisplayMessageNotification(message);

        /// <summary>
        /// Hideout Uncensored (optional) keeps weapons out anywhere in the hideout and decides every frame whether they
        /// should be; it's asked to draw through its own slot-key handler, by reflection, as the PaP machine does.
        /// </summary>
        private static class HideoutUncensored
        {
            private static PropertyInfo _want;
            private static MethodInfo _onWeaponKey;
            private static bool _looked;

            public static bool Draw(HideoutPlayerOwner owner, string slotId)
            {
                if (!_looked)
                {
                    _looked = true;
                    Type session = AccessTools.TypeByName("HideoutUncensored.HideoutSession");
                    _want = session == null ? null : AccessTools.Property(session, "Want");
                    _onWeaponKey = session == null ? null : AccessTools.Method(session, "OnWeaponKey");
                }
                if (_want?.GetSetMethod(true) == null)
                    return false;
                ECommand command = slotId == "FirstPrimaryWeapon" ? ECommand.SelectFirstPrimaryWeapon : ECommand.SelectSecondPrimaryWeapon;
                try
                {
                    _onWeaponKey?.Invoke(null, new object[] { owner, command });
                }
                catch (Exception e)
                {
                    ThundergunPlugin.Log.LogWarning($"Drawing by slot failed, drawing normally: {e.GetBaseException().Message}");
                }
                _want.GetSetMethod(true).Invoke(null, new object[] { true });
                return true;
            }
        }
    }
}
