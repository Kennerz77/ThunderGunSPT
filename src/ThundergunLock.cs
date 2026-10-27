using System.Reflection;
using System.Threading.Tasks;
using EFT.Communications;
using EFT.InventoryLogic;
using EFT.UI;
using EFT.UI.DragAndDrop;
using HarmonyLib;

namespace Thundergun
{
    /// <summary>
    /// A loaded Pressure Cartridge stays in the gun until it's spent and the reload drops it: no unloading it in the
    /// inventory (the Unload and Unload Ammo buttons, or dragging it out of the gun's slot), and its charges never
    /// come out loose. The gun's chamber isn't shown as a slot either: the cartridge is the only thing the player
    /// deals with. The reload itself doesn't go through any of this.
    /// </summary>
    internal static class ThundergunLock
    {
        public static void Enable(Harmony harmony)
        {
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.UnloadWeapon)),
                new HarmonyMethod(Method(nameof(UnloadWeaponPrefix))));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.UnloadAmmo)),
                new HarmonyMethod(Method(nameof(UnloadAmmoPrefix))));
            harmony.Patch(AccessTools.Method(typeof(SlotView), nameof(SlotView.CanDrag)),
                postfix: new HarmonyMethod(Method(nameof(CanDragPostfix))));
            // Every drag starts here, whatever screen it's on (Edit Build and the modding screen included)
            harmony.Patch(AccessTools.Method(typeof(ItemView), nameof(ItemView.CanDrag), new[] { typeof(UnityEngine.EventSystems.PointerEventData) }),
                postfix: new HarmonyMethod(Method(nameof(ItemViewCanDragPostfix))));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.Disassemble)),
                new HarmonyMethod(Method(nameof(DisassemblePrefix))));
            // Destroying (discarding) it, or taking it off through the context menu
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.ThrowItem)),
                new HarmonyMethod(Method(nameof(ThrowPrefix))));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.Uninstall)),
                new HarmonyMethod(Method(nameof(UninstallPrefix))));
            // The Thundergun's only slot is its cartridge: there's nothing to mod, and a build could only be used to
            // swap cartridges outside a reload
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.ModWeapon)),
                new HarmonyMethod(Method(nameof(ModWeaponPrefix))));
            harmony.Patch(AccessTools.Method(typeof(ItemUiContext), nameof(ItemUiContext.EditWeaponBuild)),
                new HarmonyMethod(Method(nameof(ModWeaponPrefix))));
            harmony.Patch(AccessTools.Method(typeof(EditBuildScreen), nameof(EditBuildScreen.SaveAsBuild)),
                new HarmonyMethod(Method(nameof(SaveBuildPrefix))));
            harmony.Patch(AccessTools.Method(typeof(EditBuildScreen), nameof(EditBuildScreen.TryToAssembleBuild)),
                new HarmonyMethod(Method(nameof(AssembleBuildPrefix))));
            // Show has overloads: the inspect window's (slot, context, controller, ui context)
            Safe(() => harmony.Patch(AccessTools.Method(typeof(ModSlotView), nameof(ModSlotView.Show),
                    new[] { typeof(Slot), typeof(ItemContext), typeof(ItemController), typeof(ItemUiContext) }),
                postfix: new HarmonyMethod(Method(nameof(ShowSlotPostfix)))), "hide the charge slots");
        }

        private static MethodInfo Method(string name) => typeof(ThundergunLock).GetMethod(name, BindingFlags.Static | BindingFlags.NonPublic);

        // One patch failing (a game update renaming something) mustn't leave the others off
        private static void Safe(System.Action patch, string what)
        {
            try
            {
                patch();
            }
            catch (System.Exception e)
            {
                ThundergunPlugin.Log.LogError($"Cartridge lock: couldn't {what}: {e.GetBaseException().Message}");
            }
        }

        private static bool Refuse(ref Task __result)
        {
            NotificationManager.DisplayWarningNotification("A pressure cartridge stays in the Thundergun until it's spent.");
            __result = Task.CompletedTask;
            return false;
        }

        private static bool UnloadWeaponPrefix(Weapon weapon, ref Task __result) =>
            !ThundergunIds.Is(weapon) || Refuse(ref __result);

        // The gun (its chamber), a cartridge, or a charge: none give up their charges
        private static bool UnloadAmmoPrefix(ItemContext itemContext, ref Task __result)
        {
            Item item = itemContext?.Item;
            return !(ThundergunIds.Is(item) || ThundergunIds.IsCartridge(item) || IsCharge(item)) || Refuse(ref __result);
        }

        public static bool IsCharge(Item item) => item != null && item.TemplateId.ToString() == ThundergunIds.Charge;

        /// <summary>The cartridge can't be dragged out of the gun, nor a charge out of anything.</summary>
        private static void CanDragPostfix(ItemContext itemContext, ref bool __result)
        {
            if (__result && Locked(itemContext?.Item))
                __result = false;
        }

        private static void ItemViewCanDragPostfix(ItemView __instance, ref bool __result)
        {
            if (__result && Locked(__instance.ItemContext?.Item))
                __result = false;
        }

        private static bool Locked(Item item) =>
            item != null && (IsCharge(item) || ThundergunIds.IsCartridge(item) && item.Parent?.Container is Slot slot && ThundergunIds.Is(slot.ParentItem));

        private static bool ThrowPrefix(Item item, ref Task __result) => !Locked(item) || Refuse(ref __result);

        private static bool UninstallPrefix(ItemContext itemContext, ref Task __result) => !Locked(itemContext?.Item) || Refuse(ref __result);

        private const string NothingToMod = "The Thundergun has nothing to modify: its only part is the pressure cartridge.";

        private static bool ModWeaponPrefix(Item __0)
        {
            if (!ThundergunIds.Is(__0))
                return true;
            NotificationManager.DisplayWarningNotification(NothingToMod);
            return false;
        }

        private static readonly AccessTools.FieldRef<EditBuildScreen, Item> BuildBody = AccessTools.FieldRefAccess<EditBuildScreen, Item>("_itemBody");

        private static bool ThundergunBuild(EditBuildScreen screen)
        {
            if (!ThundergunIds.Is(BuildBody(screen)))
                return false;
            NotificationManager.DisplayWarningNotification(NothingToMod);
            return true;
        }

        private static bool SaveBuildPrefix(EditBuildScreen __instance, ref Task __result)
        {
            if (!ThundergunBuild(__instance))
                return true;
            __result = Task.CompletedTask;
            return false;
        }

        private static bool AssembleBuildPrefix(EditBuildScreen __instance) => !ThundergunBuild(__instance);

        // Taking the gun apart would take its cartridge out with the rest
        private static bool DisassemblePrefix(ItemContext itemContext, ref Task __result)
        {
            Item item = itemContext?.Item;
            return !(ThundergunIds.Is(item) || Locked(item)) || Refuse(ref __result);
        }

        // Charges never show as slots: not the gun's chamber, nor the charges listed inside a cartridge's inspect window
        private static void ShowSlotPostfix(ModSlotView __instance, Slot slot)
        {
            if (slot == null)
                return;
            if (ThundergunIds.Is(slot.ParentItem) && slot.ID.StartsWith("patron_in_weapon") || IsCharge(slot.ContainedItem))
                __instance.gameObject.SetActive(false);
        }
    }
}
