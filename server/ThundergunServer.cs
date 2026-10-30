using System.Text.Json;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Services.Items;
using SPTarkov.Server.Core.Services.Modding.Custom;
using Path = System.IO.Path;

namespace ThundergunServer;

public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.kennerz.thundergun"; // same GUID as the client plugin (Forge rule)
    public string Name { get; init; } = "Thundergun";
    public string Author { get; init; } = "Kennerz";
    public List<string>? Contributors { get; init; }
    public SemanticVersioning.Version Version { get; init; } = new("1.1.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.0");
    public List<string>? Incompatibilities { get; init; }
    /// <summary>Hideout Uncensored provides weapons in the hideout, where the wall buy is (the client depends on it too).</summary>
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; } = new()
    {
        ["com.trappuss.hideoutuncensored"] = new SemanticVersioning.Range(">=1.3.0"),
    };
    public string? Url { get; init; } = "https://github.com/Kennerz77/ThunderGunSPT";
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; } = false;
}

/// <summary>Fixed template ids. They share a prefix so the profile store can spot them in any item list.</summary>
internal static class ThundergunIds
{
    public const string Prefix = "6a7e5a71d9a11500000000";

    public static readonly MongoId Weapon = new(Prefix + "01");
    public static readonly MongoId Canister = new(Prefix + "02"); // the Pressure Cartridge
    // Prefix + "03" was the Pack-a-Punched canister (0.1.x testing); retired, not reused
    public static readonly MongoId Charge = new(Prefix + "04");

    // Vanilla templates the Thundergun items are cloned from
    public static readonly MongoId Saiga = new("576165642459773c7a400233");
    public static readonly MongoId SaigaMagazine = new("57616a9e2459773c7a400234");
    public static readonly MongoId Buckshot = new("560d5e524bdc2d25448b4571");
    public static readonly MongoId DefaultInventory = new("55d7217a4bdc2d86028b456d");

    public const string WeaponParent = "5447b6094bdc2dc3278b4567";   // Shotgun
    public const string MagazineParent = "5448bc234bdc2d3c308b4569"; // Magazine
    public const string AmmoParent = "5485a8684bdc2da71d8b4567";     // Ammo
    public const string HandbookShotguns = "5b5f794b86f77409407a7f92";
    public const string HandbookMagazines = "5b5f754a86f774094242f19b";
    public const string HandbookRounds = "5b47574386f77428ca22b33b";

    // Bundle keys: paths under the mod's bundles folder (bundles.json), built by build_bundle.ps1
    public const string BundleFolder = "Thundergun";
    public const string WeaponBundle = "weapon_thundergun_container.bundle";
    public const string CanisterBundle = "mag_thundergun_canister.bundle";

    public const int CanisterCharges = 2;
    public const int UpgradedCanisterCharges = 4; // BO1's upgraded clip size

    public static bool IsOurs(string? templateId) => templateId != null && templateId.StartsWith(Prefix);
}

/// <summary>Settings read from config.json next to the DLL. Never shipped, so an update can't overwrite it.</summary>
public record ThundergunConfig
{
    /// <summary>The hideout chalk wall's price for a Thundergun, in roubles.</summary>
    public int WallBuyPrice { get; init; } = 935_000;

    /// <summary>The hideout ammo box's price for one full pressure cartridge, in roubles.</summary>
    public int CartridgePrice { get; init; } = 10_000;

    internal static ThundergunConfig Load(Action<string> logError)
    {
        string path = Path.Combine(Path.GetDirectoryName(typeof(ThundergunConfig).Assembly.Location)!, "config.json");
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<ThundergunConfig>(File.ReadAllText(path)) ?? new();
        }
        catch (Exception e)
        {
            logError($"[Thundergun] Could not read {path}, using defaults: {e.Message}");
        }
        return new();
    }
}

/// <summary>
/// Adds the Thundergun, its two canister magazines and the charge they hold. Items must exist before profiles load,
/// hence Preload. Until the Thundergun bundle exists the clones keep the vanilla Saiga-12K's model.
/// </summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 1)]
public class ThundergunItemLoader(
    ISptLogger<ThundergunItemLoader> logger,
    CustomItemService customItemService,
    TemplateTable templateTable,
    LocaleTable localeTable,
    ItemConfig itemConfig,
    ItemFilterService itemFilterService) : IOnLoad
{

    /// <summary>
    /// Wall-buys and ammo boxes only: out of random loot, bot gear, flea and daily rewards. (A Pack-a-Punched enemy gun
    /// is PaP's own 1-in-200 roll, not this item showing up.)
    /// </summary>
    private void KeepOutOfTheWorld(IEnumerable<MongoId> ids)
    {
        List<MongoId> all = ids.ToList();
        foreach (MongoId id in all)
        {
            itemConfig.Blacklist.Add(id);
            itemConfig.LootableItemBlacklist.Add(id);
            itemConfig.RewardItemBlacklist.Add(id);
        }
        itemFilterService.AddItemToBlacklistCache(all);
        itemFilterService.AddItemToLootableBlacklistCache(all);
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var items = templateTable.Items;

        TemplateItem? charge = Clone(ThundergunIds.Buckshot, ThundergunIds.AmmoParent, ThundergunIds.Charge, "patron_thundergun_charge",
            ThundergunIds.HandbookRounds, 1_000, "Thundergun charge", "Charge",
            "A sealed pulse of compressed air. Only exists inside a pressure cartridge.");
        TemplateItem? canister = Clone(ThundergunIds.SaigaMagazine, ThundergunIds.MagazineParent, ThundergunIds.Canister, "mag_thundergun_pressure_cartridge",
            ThundergunIds.HandbookMagazines, 25_000, "Pressure Cartridge", "Pressure",
            "A Group 935 pressure cartridge for the Thundergun. Every charge in it must be fired before the gun lets it go, " +
            "and it can't be refilled: a spent one is dead weight. A Pack-a-Punched Thundergun overcharges any cartridge loaded into it.");
        TemplateItem? weapon = Clone(ThundergunIds.Saiga, ThundergunIds.WeaponParent, ThundergunIds.Weapon, "weapon_group935_thundergun",
            ThundergunIds.HandbookShotguns, 250_000, "Thundergun", "Thundergun",
            "A Group 935 wonder weapon. It fires a crushing blast of air that throws anything in front of it off its feet. Feeds from pressure cartridges.");
        if (charge?.Properties == null || canister?.Properties == null || weapon?.Properties == null)
            return Task.CompletedTask;

        // The tyre cartridge is a big drum: 2x2 in the inventory
        canister.Properties.Width = 2;
        canister.Properties.Height = 2;
        // Loaded, it adds nothing to the gun's footprint (the Saiga magazine it's cloned from hangs a cell below)
        canister.Properties.ExtraSizeLeft = 0;
        canister.Properties.ExtraSizeRight = 0;
        canister.Properties.ExtraSizeUp = 0;
        canister.Properties.ExtraSizeDown = 0;
        canister.Properties.ExtraSizeForceAdd = false;

        HashSet<MongoId> chargeOnly = [ThundergunIds.Charge];
        // One kind of cartridge: two charges as issued, room for four because a Zeus Cannon tops it up where it sits
        SetCartridges(canister.Properties, ThundergunIds.UpgradedCanisterCharges, chargeOnly);

        // A canister only ever exists with its charges inside: loose charges stack no higher than a full canister
        charge.Properties.StackMaxSize = ThundergunIds.UpgradedCanisterCharges;
        // No bullet at all: the game only fires a projectile for ammo with a speed, so the shot is just the gun's
        // animation, sound and the client mod's air blast (no buckshot spread, tracer or impacts)
        charge.Properties.InitialSpeed = 0;
        charge.Properties.ProjectileCount = 1;
        charge.Properties.Tracer = false;
        // Nothing wears the gun out: no durability burn from its shots
        charge.Properties.DurabilityBurnModificator = 0;
        // Its own "calibre" in place of 12 gauge: the cell shows the id without "Caliber", the stats its translation
        charge.Properties.Caliber = Caliber;

        TemplateItemProperties gun = weapon.Properties;
        gun.AmmoCaliber = Caliber;
        // As heavy as a light machine gun, and then some: 9.35 kg, for Group 935
        gun.Weight = 9.35;
        gun.DurabilityBurnRatio = 0;
        // Nothing can go wrong with it: a jam, misfeed or misfire needs a magazine pulled or a bolt cycled, and the
        // cartridge is locked in until it's spent, so the hands would never be freed (the "busy hands" glitch)
        gun.AllowJam = false;
        gun.AllowFeed = false;
        gun.AllowMisfire = false;
        gun.AllowSlide = false;
        gun.AllowOverheat = false;
        gun.BaseMalfunctionChance = 0;
        charge.Properties.MisfireChance = 0;
        charge.Properties.MalfMisfireChance = 0;
        charge.Properties.MalfFeedChance = 0;
        // Always 9.35 kg in all: the cartridge and its charges add nothing, loaded or spare
        canister.Properties.Weight = 0;
        charge.Properties.Weight = 0;
        // Always 4x2, loaded or not: nothing it takes changes its size, and there's no stock to fold
        gun.Width = 4;
        gun.Height = 2;
        gun.Foldable = false;
        // Only the canister slot: the Saiga's required handguard and grip slots would leave the gun unusable bare
        Slot? magazineSlot = gun.Slots?.FirstOrDefault(s => s.Name == "mod_magazine");
        gun.Slots = magazineSlot == null ? [] : [WithFilter(magazineSlot, [ThundergunIds.Canister])];
        gun.Chambers = gun.Chambers?.Select(c => WithFilter(c, chargeOnly)).ToList();
        gun.DefMagType = ThundergunIds.Canister;
        gun.DefAmmo = ThundergunIds.Charge;

        // No real recoil: the kick back into the shoulder is part of the fire animation
        gun.RecoilForceUp = 0;
        gun.RecoilForceBack = 0;
        gun.RecoilCamera = 0;
        if (gun.WeaponRecoilSettings != null)
            gun.WeaponRecoilSettings.Enable = false;
        // Half a second between shots: the second follows quickly, as in BO
        gun.BFirerate = 120;
        gun.SingleFireRate = 120;

        foreach (TemplateItem item in new[] { charge, canister, weapon })
        {
            item.Properties!.CanSellOnRagfair = false; // the hideout's wall buy and ammo case are the only sources
            items[item.Id] = item;
        }
        AllowInPrimarySlots(items);
        UseOwnModels(weapon.Properties, canister.Properties);
        AddCaliberName();
        KeepOutOfTheWorld([ThundergunIds.Weapon, ThundergunIds.Canister, ThundergunIds.Charge]);
        logger.Success("[Thundergun] Items added: Thundergun, Pressure Cartridge, Thundergun charge");
        return Task.CompletedTask;
    }

    private const string Caliber = "CaliberPressure";

    /// <summary>The calibre's name in the stats, every language: the game looks it up both with and without "Caliber".</summary>
    private void AddCaliberName()
    {
        foreach (var locale in localeTable.Global.Values)
        {
            locale.AddTransformer(dictionary =>
            {
                if (dictionary != null)
                {
                    dictionary[Caliber] = "Pressure Cartridge";
                    dictionary[Caliber.Replace("Caliber", "")] = "Pressure Cartridge";
                }
                return dictionary;
            });
        }
    }

    private TemplateItem? Clone(MongoId from, string parent, MongoId id, string name, string handbookParent, int price,
        string locName, string shortName, string description)
    {
        CreateItemResult result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
        {
            ItemTplToClone = from,
            ParentId = parent,
            NewId = id,
            NewItemName = name,
            AddToHandbook = true,
            HandbookParentId = handbookParent,
            HandbookPriceRoubles = price,
            AddToFleaPriceDb = false,
            AddToWeaponShelf = false,
            Locales = new Dictionary<string, LocaleDetails>
            {
                ["en"] = new() { Name = locName, ShortName = shortName, Description = description },
            },
        });
        if (result.Success != true || !templateTable.Items.TryGetValue(id, out TemplateItem? item))
        {
            logger.Error($"[Thundergun] Could not add {name}: {string.Join("; ", result.Errors ?? [])}");
            return null;
        }
        return item;
    }

    /// <summary>
    /// Points the items at the Thundergun's own bundles (bundles/Thundergun/, served through bundles.json). Without
    /// them the clones keep the Saiga's models, so the items still work while the bundles are being built.
    /// </summary>
    private void UseOwnModels(TemplateItemProperties gun, TemplateItemProperties canister)
    {
        string dir = Path.Combine(Path.GetDirectoryName(typeof(ThundergunItemLoader).Assembly.Location)!, "bundles", ThundergunIds.BundleFolder);
        bool Has(string bundle) => File.Exists(Path.Combine(dir, bundle));
        if (!Has(ThundergunIds.WeaponBundle) || !Has(ThundergunIds.CanisterBundle))
        {
            logger.Warning($"[Thundergun] Bundles missing in {dir}; using the Saiga-12K's models");
            return;
        }
        gun.Prefab = new Prefab { Path = $"{ThundergunIds.BundleFolder}/{ThundergunIds.WeaponBundle}", Rcid = "" };
        canister.Prefab = new Prefab { Path = $"{ThundergunIds.BundleFolder}/{ThundergunIds.CanisterBundle}", Rcid = "" };
    }

    /// <summary>
    /// The player's primary weapon slots list shotguns one template at a time (not the Shotgun class), so a clone is
    /// "incompatible" until it's added by id, like the Saiga itself.
    /// </summary>
    private void AllowInPrimarySlots(Dictionary<MongoId, TemplateItem> items)
    {
        if (!items.TryGetValue(ThundergunIds.DefaultInventory, out TemplateItem? inventory) || inventory.Properties?.Slots == null)
        {
            logger.Error("[Thundergun] Default inventory template not found; the Thundergun can't be equipped");
            return;
        }
        foreach (Slot slot in inventory.Properties.Slots.Where(s => s.Name is "FirstPrimaryWeapon" or "SecondPrimaryWeapon"))
        {
            foreach (SlotFilter filter in slot.Properties?.Filters ?? [])
                filter.Filter?.Add(ThundergunIds.Weapon);
        }
    }

    /// <summary>The clone shares nothing with the vanilla magazine: a fresh cartridges slot that takes only charges.</summary>
    private static void SetCartridges(TemplateItemProperties props, int count, HashSet<MongoId> filter)
    {
        Slot? cartridges = props.Cartridges?.FirstOrDefault();
        if (cartridges == null)
            return;
        props.Cartridges = [WithFilter(cartridges, filter) with { MaxCount = count }];
    }

    // New records all the way down, so the vanilla template's filter lists are never touched
    private static Slot WithFilter(Slot slot, HashSet<MongoId> filter) => slot with
    {
        Properties = new SlotProperties { Filters = [new SlotFilter { Filter = [.. filter], Shift = 0 }] },
    };
}
