using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using Comfort.Common;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;
using HarmonyLib;
using UnityEngine;

namespace Thundergun
{
    /// <summary>
    /// The canister rules. A canister is only swapped once every charge in the gun is fired, and the spent one always
    /// drops to the floor. A fresh canister is armed when its tech beep finishes: no shot before that. A Zeus Cannon
    /// (Pack-a-Punched) tops every canister it's given up to four charges, and the one it held when upgraded too.
    /// </summary>
    internal static class ThundergunCharges
    {
        public const int PlainCharges = 2;
        public const int ZeusCharges = 4;

        // reload_empty: the end segment starts at frame 62, the beep at frame 80 (30 fps), and the beep runs 1.4 s
        public const float BeepStart = (80 - 62) / 30f;
        public const float BeepLength = 1.4f;
        // A canister that arrives without the end of a reload being seen (swapped some other way): a whole reload's worth
        public const float FallbackLightsDelay = 80 / 30f;

        public static void Enable(Harmony harmony)
        {
            Prefix(harmony, typeof(Player.FirearmController), "ReloadMag", nameof(ReloadMagPrefix));
            Prefix(harmony, typeof(ClientFirearmController), "ReloadMag", nameof(ReloadMagPrefix));
            Prefix(harmony, typeof(Player.FirearmController), "QuickReloadMag", nameof(QuickReloadPrefix));
            Prefix(harmony, typeof(ClientFirearmController), "QuickReloadMag", nameof(QuickReloadPrefix));
            Prefix(harmony, typeof(Player.FirearmController), "ReloadWithAmmo", nameof(ReloadWithAmmoPrefix));
            Prefix(harmony, typeof(ClientFirearmController), "ReloadWithAmmo", nameof(ReloadWithAmmoPrefix));
        }

        private static void Prefix(Harmony harmony, Type type, string method, string prefix)
        {
            MethodInfo target = AccessTools.DeclaredMethod(type, method);
            if (target == null)
            {
                ThundergunPlugin.Log.LogWarning($"{type.Name}.{method} not found; that reload rule is off");
                return;
            }
            harmony.Patch(target, new HarmonyMethod(typeof(ThundergunCharges).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic)));
        }

        // ---------- Reload rules ----------

        /// <summary>Charges in the gun: the canister's plus the chambered one.</summary>
        public static int Count(Weapon weapon) =>
            (weapon.GetCurrentMagazine()?.Count ?? 0) + (weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem != null ? 1 : 0);

        private static bool Refuse(Weapon weapon, Callback callback)
        {
            if (!ThundergunIds.Is(weapon) || weapon.GetCurrentMagazine() == null || Count(weapon) == 0)
                return false;
            NotificationManager.DisplayWarningNotification("The canister still holds a charge. Fire it first.");
            callback?.Fail("Thundergun canister not spent");
            return true;
        }

        // No place given for the old canister means the reload drops it at the player's feet
        private static bool ReloadMagPrefix(Player.FirearmController __instance, ref ItemAddress itemAddress, Callback callback)
        {
            try
            {
                if (!(__instance.Item is Weapon weapon) || !ThundergunIds.Is(weapon))
                    return true;
                if (Refuse(weapon, callback))
                    return false;
                itemAddress = null;
            }
            catch (Exception e)
            {
                Failed("reload", e);
            }
            return true;
        }

        // The fast reload already drops the old canister; it only has to wait for the canister to be spent
        private static bool QuickReloadPrefix(Player.FirearmController __instance, Callback callback)
        {
            try
            {
                return !(__instance.Item is Weapon weapon) || !Refuse(weapon, callback);
            }
            catch (Exception e)
            {
                Failed("quick reload", e);
                return true;
            }
        }

        // Charges only come in a canister: none are loaded loose
        private static bool ReloadWithAmmoPrefix(Player.FirearmController __instance, Callback callback)
        {
            try
            {
                if (!(__instance.Item is Weapon weapon) || !ThundergunIds.Is(weapon))
                    return true;
                callback?.Fail("Thundergun charges only come in canisters");
                return false;
            }
            catch (Exception e)
            {
                Failed("loose ammo", e);
                return true;
            }
        }

        // A rule that throws must never leave the hands waiting on a half-handled reload: log it, the game's own runs
        private static void Failed(string rule, Exception e) =>
            ThundergunPlugin.Log.LogError($"Thundergun {rule} rule failed, using the game's own: {e}");

        // ---------- Arming ----------

        // Per gun (the one in the hands): when the loaded canister's lights come on, and when it may fire
        private static readonly Dictionary<string, (float lights, float armed)> Arming = new Dictionary<string, (float, float)>();

        public static void Disarm(string weaponId) => Arming[weaponId] = (float.PositiveInfinity, float.PositiveInfinity);

        /// <summary>The beep starts <paramref name="beepIn"/> seconds from now: the lights come on with it, the gun at its end.</summary>
        public static void ArmAfterBeep(string weaponId, float beepIn) =>
            Arming[weaponId] = (Time.time + beepIn, Time.time + beepIn + BeepLength);

        public static bool Armed(Weapon weapon) => !Arming.TryGetValue(weapon.Id, out var a) || Time.time >= a.armed;

        /// <summary>0 before the beep, rising to 1 over it: how far the lights have come on.</summary>
        public static float LightLevel(Weapon weapon)
        {
            if (!Arming.TryGetValue(weapon.Id, out var a) || float.IsPositiveInfinity(a.lights))
                return Arming.ContainsKey(weapon.Id) ? 0f : 1f;
            return Mathf.Clamp01((Time.time - a.lights) / BeepLength);
        }

        // ---------- Zeus Cannon top-up ----------

        private static readonly string ToppedFile = Path.Combine(BepInEx.Paths.ConfigPath, ThundergunPlugin.PluginGuid + ".topped.txt");
        private static Dictionary<string, int> _topped;

        /// <summary>
        /// Canisters (profile ids) a Zeus Cannon has already topped up, kept between sessions so one isn't topped up
        /// again after being part-fired. Hideout weapons-mode copies aren't saved, so they're never recorded.
        /// </summary>
        private static Dictionary<string, int> Topped
        {
            get
            {
                if (_topped != null)
                    return _topped;
                _topped = new Dictionary<string, int>();
                try
                {
                    if (File.Exists(ToppedFile))
                    {
                        foreach (string line in File.ReadAllLines(ToppedFile).Where(l => l.Length > 0))
                        {
                            // "id=charges"; older files have the id alone (topped to four)
                            string[] parts = line.Split('=');
                            _topped[parts[0]] = parts.Length > 1 && int.TryParse(parts[1], out int n) ? n : ZeusCharges;
                        }
                    }
                }
                catch (Exception e)
                {
                    ThundergunPlugin.Log.LogWarning($"Couldn't read {ToppedFile}: {e.Message}");
                }
                return _topped;
            }
        }

        /// <summary>The charges a profile canister held when last seen (or when topped up).</summary>
        private static void Record(string canisterId, int charges)
        {
            if (Topped.TryGetValue(canisterId, out int known) && known == charges)
                return;
            Topped[canisterId] = charges;
            try
            {
                File.WriteAllLines(ToppedFile, Topped.Select(p => p.Key + "=" + p.Value));
            }
            catch (Exception e)
            {
                ThundergunPlugin.Log.LogWarning($"Couldn't write {ToppedFile}: {e.Message}");
            }
        }

        // Canister models (profile or copy) already looked at this session
        private static readonly HashSet<string> LiveSeen = new HashSet<string>();

        /// <summary>
        /// A Zeus Cannon's canister is topped up to four charges the first time it's seen. The top-up is only the
        /// client's (the server's own count is the plain two), so it's lost whenever the profile is reloaded: a canister
        /// seen again in a later session with fewer charges than it was last known to hold is topped up again, while
        /// charges fired down in play are remembered and stay spent.
        /// </summary>
        public static void CheckZeus(Weapon weapon)
        {
            Item canister = weapon.GetCurrentMagazine();
            if (canister == null || !ThundergunPaP.IsInfused(weapon))
            {
                if (canister != null && Seen.Add("skip:" + canister.Id))
                    ThundergunPlugin.Log.LogInfo($"Thundergun {weapon.Id}: canister {canister.Id} not topped up: gun counts as infused = {ThundergunPaP.IsInfused(weapon)}");
                return;
            }
            string original = ThundergunPaP.OriginalId(canister.Id);
            bool profile = original == canister.Id;
            int count = Count(weapon);
            if (Topped.TryGetValue(original, out int last))
            {
                if (LiveSeen.Add(canister.Id) && count < last)
                {
                    ThundergunPlugin.Log.LogInfo($"Zeus Cannon {weapon.Id}: canister {canister.Id} (original {original}) held {last}, now {count} after a reload; topping it up");
                    TopUp(weapon);
                    Record(original, ZeusCharges);
                    return;
                }
                // Fired down in the profile's own canister: remembered, so it stays spent
                if (profile && count < last)
                    Record(original, count);
                return;
            }
            if (ToppedCopies.Contains(canister.Id))
                return;
            if (Seen.Add(canister.Id))
                ThundergunPlugin.Log.LogInfo($"Zeus Cannon {weapon.Id}: new canister {canister.Id} (original {original}) holds {count}, topping it up");
            LiveSeen.Add(canister.Id);
            TopUp(weapon);
            if (profile)
                Record(original, ZeusCharges);
            else
                ToppedCopies.Add(canister.Id);
        }

        private static readonly HashSet<string> ToppedCopies = new HashSet<string>();
        private static readonly HashSet<string> Seen = new HashSet<string>();

        /// <summary>
        /// The gun just came out of the machine (the profile's gun): its canister gets four charges, as the server
        /// gives it, and so does the copy in the hands if this is the hideout's weapons mode.
        /// </summary>
        public static void OnUpgraded(Item item)
        {
            if (!(item is Weapon weapon) || !ThundergunIds.Is(weapon))
                return;
            TopUp(weapon);
            Item canister = weapon.GetCurrentMagazine();
            if (canister != null)
                Record(canister.Id, ZeusCharges);
            foreach (Player player in UnityEngine.Object.FindObjectsOfType<Player>())
            {
                if (player.IsYourPlayer && player.HandsController?.Item is Weapon held && held != weapon && ThundergunPaP.OriginalId(held.Id) == weapon.Id)
                {
                    TopUp(held);
                    if (held.GetCurrentMagazine() is Item copy)
                        ToppedCopies.Add(copy.Id);
                }
            }
        }

        // ---------- Pack-a-Punch stripped ----------

        /// <summary>
        /// Guns just stripped of their 115 powers (profile ids) are plain Thunderguns again: each one's canister goes
        /// back to two charges, as the server's strip did. Run on the local inventory and, in the hideout, the
        /// original one behind weapons mode, so the copy in the hands and the profile's gun both match the server.
        /// </summary>
        public static void TrimStripped(ICollection<string> stripped)
        {
            Player player = LocalPlayer.Get();
            if (player == null)
                return;
            var inventories = new List<Inventory> { player.Inventory, player.Profile?.Inventory };
            if (player is HideoutPlayer hideout)
                inventories.Add(hideout.OriginalInventory?.Inventory);
            IEnumerable<Weapon> guns = inventories.Where(i => i != null).Distinct()
                .SelectMany(i => i.GetPlayerItems(EPlayerItems.All))
                .OfType<Weapon>()
                .Where(w => ThundergunIds.Is(w) && stripped.Contains(ThundergunPaP.OriginalId(w.Id)) && !ThundergunPaP.IsInfused(w))
                .Distinct();
            foreach (Weapon gun in guns.ToList())
                Trim(gun);
        }

        /// <summary>The same as the server's trim: the chambered charge counts first, the canister keeps the rest of two.</summary>
        private static void Trim(Weapon weapon)
        {
            Magazine canister = weapon.GetCurrentMagazine();
            if (canister == null || !(canister.Cartridges.Items.FirstOrDefault() is Ammo stack))
                return;
            bool chambered = weapon.Chambers.Length > 0 && weapon.Chambers[0].ContainedItem != null;
            // At least one stays in the canister (two charges, at most one chambered), so the stack is never emptied
            int keep = PlainCharges - (chambered ? 1 : 0);
            if (stack.StackObjectsCount <= keep)
                return;
            stack.StackObjectsCount = keep;
            ThundergunPlugin.Log.LogInfo($"Thundergun {weapon.Id}: 115 powers stripped, canister {canister.Id} back to {PlainCharges} charges");
        }

        /// <summary>The same as the server's top-up: the chamber first, the rest into the canister's stack.</summary>
        private static void TopUp(Weapon weapon)
        {
            Magazine canister = weapon.GetCurrentMagazine();
            if (canister == null)
                return;
            int need = ZeusCharges - Count(weapon);
            if (need <= 0)
                return;
            string charge = ThundergunIds.Charge;
            Slot chamber = weapon.Chambers.Length > 0 ? weapon.Chambers[0] : null;
            if (chamber != null && chamber.ContainedItem == null && NewCharge(charge, 1) is Item round && chamber.AddWithoutRestrictions(round).Succeeded)
                need--;
            if (need <= 0)
                return;
            if (canister.Cartridges.Items.FirstOrDefault() is Ammo stack)
                stack.StackObjectsCount += need;
            else if (NewCharge(charge, need) is Item charges)
                canister.Cartridges.Add(charges, false);
            ThundergunPlugin.Log.LogInfo($"Zeus Cannon {weapon.Id}: canister {canister.Id} topped up to {ZeusCharges} charges");
        }

        private static Item NewCharge(string template, int count)
        {
            Item item = Singleton<ItemFactory>.Instance.CreateItem(MongoID.Generate(), template, null);
            if (item != null)
                item.StackObjectsCount = count;
            return item;
        }
    }
}
