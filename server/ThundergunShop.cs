using System.Text.Json.Serialization;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Helpers.Profile;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Request;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Inventory;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Enums.Hideout;
using SPTarkov.Server.Core.DI.Routing;
using SPTarkov.Server.Core.Services.Commerce;
using SPTarkov.Server.Core.Utils;

namespace ThundergunServer;

/// <summary>Body of the client's purchase: "weapon" (the chalk wall) or "cartridge" (the ammo box).</summary>
public record ThundergunBuyRequest : BaseInteractionRequestData
{
    [JsonPropertyName("item")]
    public string? Item { get; set; }
}

/// <summary>
/// The hideout's Thundergun wall buy and cartridge box, the mod's own way to get the gun (no Pack-a-Punch needed).
/// Paid in roubles from the stash; the bought items go into the stash through the reply, so the client sees them
/// at once. Prices are in config.json.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ThundergunShop(ISptLogger<ThundergunShop> logger, PaymentService paymentService, InventoryHelper inventoryHelper,
    HttpResponseUtil httpResponseUtil)
{
    public const string BuyAction = "ThundergunBuy";
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    /// <summary>The Emergency Wall level at which the hideout's side room is fully open.</summary>
    public const int SideRoomLevel = 6;

    private readonly ThundergunConfig _config = ThundergunConfig.Load(message => logger.Error(message));

    public ItemEventRouterResponse Buy(PmcData pmcData, ThundergunBuyRequest body, MongoId sessionId, ItemEventRouterResponse output)
    {
        (List<Item> items, int price, string what) = body.Item switch
        {
            "weapon" => (Weapon(), _config.WallBuyPrice, "Thundergun"),
            "cartridge" => (Cartridge(null, null, ThundergunIds.CanisterCharges), _config.CartridgePrice, "Pressure Cartridge"),
            _ => (new List<Item>(), 0, ""),
        };
        if (items.Count == 0)
            return httpResponseUtil.AppendErrorToOutput(output, "Nothing to buy there.");
        // The stations only exist once the hideout's side room is open (the Emergency Wall upgrades done)
        if (pmcData.Hideout?.Areas?.Any(a => a.Type == HideoutAreas.EmergencyWall && a.Level >= SideRoomLevel) != true)
            return httpResponseUtil.AppendErrorToOutput(output, "Nothing to buy there.");

        // Checked before any money moves: enough roubles, and room for it
        double roubles = pmcData.Inventory?.Items?.Where(i => i.Template.ToString() == Roubles).Sum(i => i.Upd?.StackObjectsCount ?? 1) ?? 0;
        if (roubles < price)
            return httpResponseUtil.AppendErrorToOutput(output, "Not enough funds.");
        // The gun goes straight into an empty primary weapon slot when there is one
        string? slot = body.Item == "weapon" ? FreePrimarySlot(pmcData) : null;
        if (slot == null && !inventoryHelper.CanPlaceItemsInInventory(sessionId, [items]))
            return httpResponseUtil.AppendErrorToOutput(output, "No room in your stash.");

        // The 5-argument overload exists on every SPT 4.1.x (the newer one with requestedStackIds may not); it only
        // passes null for those stack ids, so it behaves the same. Marked obsolete for removal in a later SPT.
#pragma warning disable CS0618
        paymentService.AddPaymentToOutput(pmcData, Money.ROUBLES, price, sessionId, output);
#pragma warning restore CS0618
        if (output.Warnings?.Count > 0)
            return output;
        if (slot != null)
            Equip(pmcData, items, slot, sessionId, output);
        else
            inventoryHelper.AddItemsToStash(sessionId, new AddItemsDirectRequest { ItemsWithModsToAdd = [items], FoundInRaid = false }, pmcData, output);
        logger.Info($"[Thundergun] {pmcData.Info?.Nickname} bought a {what} for {price} RUB{(slot != null ? $", equipped in {slot}" : "")}");
        return output;
    }

    private static readonly string[] PrimarySlots = ["FirstPrimaryWeapon", "SecondPrimaryWeapon"];

    private static string? FreePrimarySlot(PmcData pmcData)
    {
        string? equipment = pmcData.Inventory?.Equipment?.ToString();
        if (equipment == null || pmcData.Inventory?.Items == null)
            return null;
        return PrimarySlots.FirstOrDefault(s => !pmcData.Inventory.Items.Any(i => i.ParentId == equipment && i.SlotId == s));
    }

    /// <summary>Into the equipment slot; the reply carries it as new items under the equipment, which the client adds there.</summary>
    private static void Equip(PmcData pmcData, List<Item> items, string slot, MongoId sessionId, ItemEventRouterResponse output)
    {
        items[0].ParentId = pmcData.Inventory!.Equipment.ToString();
        items[0].SlotId = slot;
        pmcData.Inventory.Items!.AddRange(items);
        if (output.ProfileChanges?.TryGetValue(sessionId, out ProfileChange? change) == true && change?.Items != null)
        {
            change.Items.NewItems ??= [];
            change.Items.NewItems.AddRange(items);
        }
    }

    /// <summary>The gun ready to fire: one charge chambered, one in its cartridge (a full cartridge's worth).</summary>
    private static List<Item> Weapon()
    {
        MongoId gun = new();
        var items = new List<Item>
        {
            new() { Id = gun, Template = ThundergunIds.Weapon, Upd = new Upd { FireMode = new UpdFireMode { FireMode = "single" } } },
            new() { Id = new MongoId(), Template = ThundergunIds.Charge, ParentId = gun.ToString(), SlotId = "patron_in_weapon", Upd = new Upd { StackObjectsCount = 1 } },
        };
        items.AddRange(Cartridge(gun.ToString(), "mod_magazine", ThundergunIds.CanisterCharges - 1));
        return items;
    }

    private static List<Item> Cartridge(string? parent, string? slot, int charges)
    {
        MongoId cartridge = new();
        var items = new List<Item> { new() { Id = cartridge, Template = ThundergunIds.Canister, ParentId = parent, SlotId = slot } };
        if (charges > 0)
            items.Add(new Item { Id = new MongoId(), Template = ThundergunIds.Charge, ParentId = cartridge.ToString(), SlotId = "cartridges", Location = 0, Upd = new Upd { StackObjectsCount = charges } });
        return items;
    }
}

/// <summary>Routes the client's ThundergunBuy item event.</summary>
[Injectable(TypePriority = OnLoadOrder.Routers)]
public class ThundergunShopRouter(ThundergunShop shop) : ItemEventRouter([
    new ItemRouteAction<ThundergunBuyRequest>(ThundergunShop.BuyAction,
        (url, pmcData, body, sessionID, output, cancellationToken) => ValueTask.FromResult(shop.Buy(pmcData, body, sessionID, output))),
]) { }
