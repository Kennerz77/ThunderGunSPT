using System.Collections.Generic;
using System.Linq;
using BepInEx.Bootstrap;
using EFT;
using EFT.InventoryLogic;
using PaPHideoutModule;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// Everything the Thundergun asks of Pack-a-Punch. PaP is optional: callers go through <see cref="ThundergunPaP"/>,
    /// which only touches this class (and so only loads PaPHideoutModule.dll) when PaP is installed.
    /// </summary>
    internal static class ThundergunPaPBridge
    {
        public static bool IsInfused(Item item) => PaPApi.IsInfused(item) && !InMachine(item);

        /// <summary>What the machine added to the gun's worth: its fee, which depends on the mode picked in PaP.</summary>
        public static int InfusedFeeRub => PaPApi.BonusesActive ? 500_000 : 5_000;

        // Weapons infused when the set last changed (profile ids), so a change can tell which ones were just stripped
        private static HashSet<string> _infused = new HashSet<string>();

        // Guns the machine has just infused (profile id -> when): the server says yes before the gun goes in, but
        // like PaP's camo, the Zeus Cannon's look waits until it comes back out (WeaponUpgraded). The timeout only
        // covers a sequence that never finishes (leaving the hideout mid-way); the machine takes about 4 s
        private static readonly Dictionary<string, float> Upgrading = new Dictionary<string, float>();
        private const float MachineTimeout = 15f;

        private static bool InMachine(Item item)
        {
            if (Upgrading.Count == 0)
                return false;
            string id = PaPApi.OriginalId(item.Id);
            if (!Upgrading.TryGetValue(id, out float since))
                return false;
            if (Time.time - since < MachineTimeout)
                return true;
            Upgrading.Remove(id);
            return false;
        }

        public static void Setup()
        {
            PaPApi.RegisterName(ThundergunIds.Weapon, ThundergunIds.UpgradedName);
            // Its upgraded power is the blast and the extra charges, not PaP's bullet damage or fire rate
            PaPApi.RegisterBonuses(ThundergunIds.Weapon, PaPBonusSet.None);
            PaPApi.WeaponUpgraded += OnUpgraded;
            PaPApi.InfusedChanged += OnInfusedChanged;
            _infused = new HashSet<string>(PaPApi.InfusedIds);
        }

        // Out of the machine: the Zeus Cannon shows, and its canister is topped up to four, as the server did
        private static void OnUpgraded(Item item)
        {
            if (item != null && Upgrading.Remove(item.Id))
                foreach (ThundergunView view in ThundergunView.Live)
                    view?.Refresh();
            ThundergunCharges.OnUpgraded(item);
        }

        /// <summary>
        /// The infused set changed (an upgrade, a strip, a profile sync): every live Thundergun model rewrites its
        /// lights and camo at once, and a stripped gun gives back its overcharge, as the server's strip did. The model
        /// in the hands otherwise kept its camo until the gun was put away and drawn again.
        /// </summary>
        private static void OnInfusedChanged()
        {
            var now = new HashSet<string>(PaPApi.InfusedIds);
            // Only guns that were infused and no longer are: an empty set at login mustn't trim a real Zeus Cannon
            var stripped = new HashSet<string>(_infused.Where(id => !now.Contains(id)));
            // One gun newly infused while in the hideout is the machine at work (a profile sync happens at the menu,
            // with no player, and can bring several at once)
            List<string> added = now.Where(id => !_infused.Contains(id)).ToList();
            if (added.Count == 1 && LocalPlayer.Get() is HideoutPlayer)
                Upgrading[added[0]] = Time.time;
            foreach (string id in stripped)
                Upgrading.Remove(id);
            _infused = now;

            foreach (ThundergunView view in ThundergunView.Live.ToList())
            {
                if (view == null)
                    continue;
                view.Refresh();
                Weapon weapon = view.ShownWeapon();
                if (view.Prefab != null && weapon != null)
                    PaPApi.ApplyCamo(view.Prefab.gameObject, weapon);
            }
            if (stripped.Count > 0)
                ThundergunCharges.TrimStripped(stripped);
        }

        public static string OriginalId(string itemId) => PaPApi.OriginalId(itemId);

        public static void ApplyCamo(GameObject model, Item item) => PaPApi.ApplyCamo(model, item);

        public static void RegisterShot(AudioClip shot) => PaPApi.RegisterShotSound(ThundergunIds.Weapon, shot);
    }

    /// <summary>Pack-a-Punch hooks that are safe to call with or without PaP installed.</summary>
    internal static class ThundergunPaP
    {
        public static readonly bool Installed = Chainloader.PluginInfos.ContainsKey("com.kennerz.paphideoutmodule");
        private static bool _shotRegistered;

        public static void Setup()
        {
            if (Installed)
                ThundergunPaPBridge.Setup();
        }

        public static bool IsInfused(Item item) => Installed && item != null && ThundergunPaPBridge.IsInfused(item);

        public static int InfusedFeeRub => Installed ? ThundergunPaPBridge.InfusedFeeRub : 0;

        /// <summary>The profile id behind a hideout weapons-mode copy (the id itself without PaP).</summary>
        public static string OriginalId(string itemId) => Installed ? ThundergunPaPBridge.OriginalId(itemId) : itemId;

        /// <summary>PaP's camo on (or off) a model, by the gun's current state. Nothing without PaP.</summary>
        public static void ApplyCamo(GameObject model, Item item)
        {
            if (Installed && model != null && item != null)
                ThundergunPaPBridge.ApplyCamo(model, item);
        }

        /// <summary>BO3's remastered shot (in the weapon bundle) becomes the Pack-a-Punched Thundergun's sound.</summary>
        public static void RegisterShot(AudioClip shot)
        {
            if (!Installed || _shotRegistered || shot == null)
                return;
            ThundergunPaPBridge.RegisterShot(shot);
            _shotRegistered = true;
        }
    }

    internal static class ThundergunIds
    {
        public const string Weapon = "6a7e5a71d9a1150000000001";
        public const string Cartridge = "6a7e5a71d9a1150000000002"; // the Pressure Cartridge
        public const string Charge = "6a7e5a71d9a1150000000004";
        public const string Name = "Thundergun";
        public const string UpgradedName = "Zeus Cannon";

        public static bool Is(Item item) => item != null && item.TemplateId.ToString() == Weapon;

        public static bool IsCartridge(Item item) => item != null && item.TemplateId.ToString() == Cartridge;
    }
}
