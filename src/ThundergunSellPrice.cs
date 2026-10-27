using System;
using System.Linq;
using EFT.InventoryLogic;
using EFT.Trading;
using HarmonyLib;

namespace Thundergun
{
    /// <summary>
    /// What traders pay for the Thundergun's items (the server pays the price the client offers). The gun always sells
    /// for what the wall charged, plus the Pack-a-Punch fee when infused. A cartridge sells for what the ammo case
    /// charged, less 2,000 for each charge spent, and for nothing once empty.
    /// </summary>
    internal static class ThundergunSellPrice
    {
        public const int GunRub = 935_000;
        public const int CartridgeRub = 10_000;
        public const int SpentChargeRub = 2_000;

        public static void Enable(Harmony harmony)
        {
            // Last, so PaP's own postfix (which adds its fee to the vanilla price) has already run and is replaced
            harmony.Patch(AccessTools.Method(typeof(Trader), nameof(Trader.GetUserItemPrice)),
                postfix: new HarmonyMethod(typeof(ThundergunSellPrice).GetMethod(nameof(PricePostfix),
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)) { priority = Priority.Last });
        }

        private static void PricePostfix(Trader __instance, Item item, ref Trader.ItemPrice? __result)
        {
            try
            {
                // No price means this trader doesn't buy it; that stays so
                if (__result == null || item == null)
                    return;
                long rub;
                if (ThundergunIds.Is(item))
                    rub = GunRub + (ThundergunPaP.IsInfused(item) ? ThundergunPaP.InfusedFeeRub : 0);
                else if (ThundergunIds.IsCartridge(item))
                    rub = CartridgeValue(item);
                else
                    return;
                Trader.ItemPrice price = __result.Value;
                double course = price.CurrencyId.HasValue && __instance.CurrencyCourses != null
                                && __instance.CurrencyCourses.TryGetValue(price.CurrencyId.Value.ToString(), out double c) && c > 0 ? c : 1.0;
                __result = new Trader.ItemPrice(price.CurrencyId, (int)Math.Min((long)Math.Floor(rub / course), int.MaxValue));
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogError($"Sell price failed: {e.Message}");
            }
        }

        private static long CartridgeValue(Item cartridge)
        {
            int charges = (int)cartridge.GetAllItems().Where(i => i.TemplateId.ToString() == ThundergunIds.Charge).Sum(i => i.StackObjectsCount);
            if (charges <= 0)
                return 0;
            // A cartridge topped up by a Zeus Cannon holds four; otherwise two is full
            int full = Math.Max(2, charges);
            return Math.Max(0, CartridgeRub - (long)(full - charges) * SpentChargeRub);
        }
    }
}
