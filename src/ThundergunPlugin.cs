using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// Client side of the Thundergun. Pack-a-Punch support is optional (soft dependency): nothing here touches PaP
    /// unless it's installed. Hideout Uncensored is required: weapons in the hideout, where the wall buy is, come from it.
    /// </summary>
    [BepInPlugin(PluginGuid, "Kennerz-Thundergun", "1.1.0")] // Forge rule: "Username-ModName"
    [BepInDependency("com.kennerz.paphideoutmodule", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency(ThundergunDraw.HideoutUncensoredGuid, "1.3.0")] // hard: the wall buy hands you the gun through it
    public class ThundergunPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.kennerz.thundergun"; // same GUID as the server mod (Forge rule)

        internal static ManualLogSource Log;

        /// <summary>Runs the mod's coroutines (the corpse fling).</summary>
        internal static MonoBehaviour Runner;

        private void Awake()
        {
            Log = Logger;
            Runner = this;
            ThundergunSettings.Bind(Config);
            ThundergunSounds.Enable();            var harmony = new HarmonyLib.Harmony(PluginGuid);
            try
            {
                ThundergunRootReset.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Weapon root reset patch failed: {e}");
            }
            try
            {
                ThundergunCooldown.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Recharge cooldown patches failed: {e}");
            }
            try
            {
                ThundergunCharges.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Canister reload rules failed: {e}");
            }
            try
            {
                ThundergunCartridge.Enable(harmony);
                ThundergunIcons.Enable(harmony);
                ThundergunLock.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Pressure Cartridge display patches failed: {e}");
            }
            try
            {
                ThundergunBlast.Enable(harmony);
                ThundergunMenuPose.Enable(harmony);
                ThundergunHandling.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Thundergun blast patches failed: {e}");
            }
            try
            {
                ThundergunSellPrice.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Sell price patch failed: {e}");
            }
            try
            {
                ThundergunAmmoCheck.Enable(harmony);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Ammo check patches failed: {e}");
            }
            try
            {
                ThundergunStations.Enable(harmony, gameObject);
            }
            catch (System.Exception e)
            {
                Log.LogError($"Hideout wall buy failed: {e}");
            }
            ThundergunPaP.Setup();
            gameObject.AddComponent<ThundergunTracker>();
            Log.LogInfo("Thundergun loaded");
        }
    }
}
