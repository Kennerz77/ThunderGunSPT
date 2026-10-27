using System.Reflection;
using EFT;
using EFT.InventoryLogic;
using EFT.UI;
using HarmonyLib;

namespace Thundergun
{
    /// <summary>
    /// Checking the Thundergun's ammo shows exactly what's left in it, chamber included: "1 charge left" up to "4
    /// charges left", or "Empty". The game's own text counts only the cartridge (never the chambered charge), calls
    /// three of four "Full", and names the ammo; none of that fits a gun with at most four shots.
    /// </summary>
    internal static class ThundergunAmmoCheck
    {
        // Set while the game builds the check text for the Thundergun in the hands; -1 otherwise
        private static int _charges = -1;

        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(GamePlayerOwner), nameof(GamePlayerOwner.PlayerOnOnShowAmmoDetails)),
                new HarmonyMethod(Method(nameof(ShowPrefix))), finalizer: new HarmonyMethod(Method(nameof(ShowFinalizer))));
            harmony.Patch(AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.GetAmmoCountByLevel)),
                postfix: new HarmonyMethod(Method(nameof(TextPostfix))));
            harmony.Patch(AccessTools.Method(typeof(AmmoCountPanel), nameof(AmmoCountPanel.GetAmmoCountByLevelForFoldingMechanismWeapon)),
                postfix: new HarmonyMethod(Method(nameof(TextPostfix))));
        }

        private static MethodInfo Method(string name) => typeof(ThundergunAmmoCheck).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        private static void ShowPrefix(ref string details)
        {
            if (!(LocalPlayer.Get()?.HandsController?.Item is Weapon weapon) || !ThundergunIds.Is(weapon))
                return;
            _charges = ThundergunCharges.Count(weapon);
            // In place of the charge's name: the cartridge is the only ammo the player knows about
            details = weapon.GetCurrentMagazine() != null ? "Pressure Cartridge" : null;
        }

        private static System.Exception ShowFinalizer(System.Exception __exception)
        {
            _charges = -1;
            return __exception;
        }

        private static void TextPostfix(ref string __result)
        {
            if (_charges < 0)
                return;
            __result = _charges == 0 ? "Empty" : _charges == 1 ? "1 charge left" : $"{_charges} charges left";
        }
    }
}
