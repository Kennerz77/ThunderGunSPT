using PaPHideoutServer;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;

namespace ThundergunServer;

/// <summary>
/// The Pack-a-Punched Thundergun (the Zeus Cannon) holds four charges per cartridge, as in BO1. Registered with PaP's
/// server API (PaPServerApi), which runs it inside PaP's own upgrade and strip and sends the changed gun back to the
/// client: upgrading tops the loaded cartridge up to four, however many it had left; stripping trims it back to a
/// plain gun's two. Without PaP installed none of this loads.
/// </summary>
internal static class ThundergunPaPServer
{
    private const string Chamber = "patron_in_weapon";

    internal static ISptLogger<ThundergunPaPHook>? Logger;

    public static PaPWeaponHandler Handler => new()
    {
        Name = "Thundergun",
        Upgraded = Recharge,
        Stripped = Trim,
    };

    /// <summary>Tops the loaded cartridge up to four charges where it sits: the chamber first, then the cartridge's stack.</summary>
    private static void Recharge(PaPWeaponContext ctx)
    {
        Item? cartridge = ctx.InSlot("mod_magazine");
        if (cartridge == null)
            return;
        Item? stack = ctx.InSlot("cartridges", cartridge);
        bool chambered = ctx.InSlot(Chamber) != null;
        int have = (int)(stack?.Upd?.StackObjectsCount ?? (stack == null ? 0 : 1)) + (chambered ? 1 : 0);
        int need = ThundergunIds.UpgradedCanisterCharges - have;
        if (need <= 0)
            return;

        if (!chambered)
        {
            ctx.Attach(ThundergunIds.Charge, Chamber, upd: new Upd { StackObjectsCount = 1 });
            need--;
        }
        if (need > 0 && stack != null)
            ctx.SetStack(stack, (stack.Upd?.StackObjectsCount ?? 1) + need);
        else if (need > 0)
            ctx.Attach(ThundergunIds.Charge, "cartridges", cartridge, new Upd { StackObjectsCount = need }).Location = 0;
        Logger?.Info($"[Thundergun] Zeus Cannon {ctx.Weapon.Id}: cartridge {cartridge.Id} topped up to {ThundergunIds.UpgradedCanisterCharges} charges");
    }

    /// <summary>The loaded cartridge keeps at most a plain gun's two charges (the chambered one counts first).</summary>
    private static void Trim(PaPWeaponContext ctx)
    {
        Item? cartridge = ctx.InSlot("mod_magazine");
        Item? stack = cartridge == null ? null : ctx.InSlot("cartridges", cartridge);
        if (stack == null)
            return;
        bool chambered = ctx.InSlot(Chamber) != null;
        int keep = Math.Max(0, ThundergunIds.CanisterCharges - (chambered ? 1 : 0));
        if ((stack.Upd?.StackObjectsCount ?? 1) <= keep)
            return;
        if (keep == 0)
            ctx.Remove(stack);
        else
            ctx.SetStack(stack, keep);
        Logger?.Info($"[Thundergun] Thundergun {ctx.Weapon.Id}: 115 powers stripped, cartridge back to {ThundergunIds.CanisterCharges} charges");
    }
}

/// <summary>Registers the Zeus Cannon with PaP when the PaP Hideout Module's server mod is loaded.</summary>
[Injectable(TypePriority = OnLoadOrder.PostLoad + 1)]
public class ThundergunPaPHook(ISptLogger<ThundergunPaPHook> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        // Our assembly references PaPHideoutServer, but only this class touches its types (at JIT time), so check first
        if (!AppDomain.CurrentDomain.GetAssemblies().Any(a => a.GetName().Name == "PaPHideoutServer"))
        {
            logger.Info("[Thundergun] PaP Hideout Module not installed; no Zeus Cannon");
            return Task.CompletedTask;
        }
        Enable(logger);
        return Task.CompletedTask;
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Enable(ISptLogger<ThundergunPaPHook> logger)
    {
        ThundergunPaPServer.Logger = logger;
        try
        {
            PaPServerApi.Register(ThundergunIds.Weapon.ToString(), ThundergunPaPServer.Handler);
            logger.Info("[Thundergun] Zeus Cannon registered with Pack-a-Punch: upgrades top the loaded cartridge up to four charges");
        }
        catch (Exception e)
        {
            // An older PaP without the server API
            logger.Error($"[Thundergun] Zeus Cannon registration failed (PaP 1.1.0 or newer needed): {e.GetBaseException().Message}");
        }
    }
}
