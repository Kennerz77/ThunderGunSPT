using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Profile;
using SPTarkov.Server.Core.Servers;
using SPTarkov.Server.Core.Utils;
using Path = System.IO.Path;

namespace ThundergunServer;

/// <summary>
/// Keeps the profile files vanilla, so uninstalling the mod can't break a profile (PaP's pattern). Thundergun items
/// (the gun, canisters, charges, and anything inside them), in the inventory, unclaimed mail or insurance returns, and
/// everything that points at them (wishlist, fast panel, favourites, insurance cover, saved builds), are taken out of
/// the JSON as SPT writes the file and kept in profiles/&lt;id&gt;.json next to the DLL. After SPT loads the profiles they're
/// put back in memory, so while the mod is installed nothing changes. Without the mod they're simply gone.
/// </summary>
internal static class ThundergunProfileStore
{
    // A side file older than its profile by more than this was left by an earlier install: the profile has been
    // saved without the mod since, and its items could now overlap whatever took their place
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    private static readonly Regex ProfilePath = new(@"user[\\/]profiles[\\/]([0-9a-f]{24})\.json$", RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Dir => Path.Combine(Path.GetDirectoryName(typeof(ThundergunProfileStore).Assembly.Location)!, "profiles");

    private static string StorePath(string profileId) => Path.Combine(Dir, profileId + ".json");

    internal static Action<string>? LogError;

    // Profiles whose side file is back in memory (or that never had one). Only these may have it rewritten: if a
    // restore failed, saving would otherwise find nothing to keep and delete the stored items for good
    private static readonly HashSet<string> Restored = [];

    /// <summary>The profile JSON SPT is about to write, minus the mod's items (kept in the side file). Fails open.</summary>
    public static string Strip(string filePath, string json)
    {
        Match match = ProfilePath.Match(filePath);
        if (!match.Success)
            return json;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject root)
                return json;
            var store = new JsonObject();

            if (root["characters"]?["pmc"] is JsonObject pmc)
            {
                if (pmc["Inventory"]?["items"] is JsonArray items && TakeOurs(items) is { Count: > 0 } taken)
                {
                    store["items"] = taken;
                    TakeReferences(pmc, IdsOf(taken), store);
                }
                // The wishlist is by template, so it's checked even with none of our items in the inventory
                if (pmc["WishList"] is JsonObject wishList && TakeKeys(wishList, ThundergunIds.IsOurs) is { Count: > 0 } wished)
                    store["wishList"] = wished;
                // "Examined" marks for our templates (they'd point at items that don't exist without the mod)
                if (pmc["Encyclopedia"] is JsonObject encyclopedia)
                {
                    var seen = new JsonObject();
                    foreach (string tpl in encyclopedia.Select(p => p.Key).Where(ThundergunIds.IsOurs).ToList())
                    {
                        JsonNode? value = encyclopedia[tpl];
                        encyclopedia.Remove(tpl);
                        seen[tpl] = value;
                    }
                    if (seen.Count > 0)
                        store["encyclopedia"] = seen;
                }
            }

            if (root["dialogues"] is JsonObject dialogues)
            {
                var mail = new JsonArray();
                foreach (var (dialogueId, dialogue) in dialogues)
                {
                    if (dialogue?["messages"] is not JsonArray messages)
                        continue;
                    foreach (JsonNode? message in messages)
                    {
                        if (message?["items"]?["data"] is JsonArray data && TakeOurs(data) is { Count: > 0 } taken)
                            mail.Add(new JsonObject { ["dialogue"] = dialogueId, ["message"] = message["_id"]?.GetValue<string>(), ["items"] = taken });
                    }
                }
                if (mail.Count > 0)
                    store["mail"] = mail;
            }

            if (root["insurance"] is JsonArray insurance && TakeInsurance(insurance) is { Count: > 0 } returns)
                store["insurance"] = returns;

            if (root["userbuilds"] is JsonObject builds)
            {
                var ourBuilds = new JsonObject();
                foreach (string kind in BuildKinds)
                {
                    if (builds[kind] is JsonArray list && TakeWhere(list, Mentions) is { Count: > 0 } taken)
                        ourBuilds[kind] = taken;
                }
                if (ourBuilds.Count > 0)
                    store["builds"] = ourBuilds;
            }

            string profileId = match.Groups[1].Value.ToLowerInvariant(), storePath = StorePath(profileId);
            lock (Restored)
            {
                if (!Restored.Contains(profileId) && File.Exists(storePath))
                    return json; // not restored this session: leave both files as they are
                Restored.Add(profileId);
            }
            if (store.Count == 0)
            {
                if (File.Exists(storePath))
                    File.Delete(storePath);
                return json;
            }
            Directory.CreateDirectory(Dir);
            string temp = storePath + ".tmp";
            File.WriteAllText(temp, store.ToJsonString(Indented));
            File.Move(temp, storePath, true); // written whole before the profile, never half
            return root.ToJsonString(json.Contains('\n') ? Indented : Compact);
        }
        catch (Exception e)
        {
            // The profile then keeps the items in its own file, exactly as before; nothing is lost
            LogError?.Invoke($"[Thundergun] Keeping mod items out of {filePath} failed, saved it as is: {e.Message}");
            return json;
        }
    }

    /// <summary>Removes our items (and anything inside them) from an item list; returns them.</summary>
    private static JsonArray TakeOurs(JsonArray items)
    {
        var ids = new HashSet<string>();
        foreach (JsonNode? item in items)
        {
            if (ThundergunIds.IsOurs(item?["_tpl"]?.GetValue<string>()) && item!["_id"]?.GetValue<string>() is string id)
                ids.Add(id);
        }
        // Children of a taken item go with it
        for (bool grew = ids.Count > 0; grew;)
        {
            grew = false;
            foreach (JsonNode? item in items)
            {
                if (item?["parentId"]?.GetValue<string>() is string parent && ids.Contains(parent)
                    && item["_id"]?.GetValue<string>() is string id && ids.Add(id))
                    grew = true;
            }
        }
        var taken = new JsonArray();
        for (int i = items.Count - 1; i >= 0; i--)
        {
            if (items[i]?["_id"]?.GetValue<string>() is string id && ids.Contains(id))
            {
                JsonNode item = items[i]!;
                items.RemoveAt(i);
                taken.Insert(0, item);
            }
        }
        return taken;
    }

    private static readonly string[] BuildKinds = ["weaponBuilds", "equipmentBuilds", "magazineBuilds"];

    private static HashSet<string> IdsOf(JsonArray items) =>
        items.Select(i => i?["_id"]?.GetValue<string>()).OfType<string>().ToHashSet();

    /// <summary>
    /// What else in the PMC points at the items just taken: fast-panel bindings, favourites and insurance on them.
    /// Left in, they'd name items that don't exist without the mod.
    /// </summary>
    private static void TakeReferences(JsonObject pmc, HashSet<string> ours, JsonObject store)
    {
        if (pmc["Inventory"]?["fastPanel"] is JsonObject fastPanel
            && TakeKeys(fastPanel, slot => fastPanel[slot]?.GetValue<string>() is string id && ours.Contains(id)) is { Count: > 0 } bound)
            store["fastPanel"] = bound;
        if (pmc["Inventory"]?["favoriteItems"] is JsonArray favourites
            && TakeWhere(favourites, f => f?.GetValue<string>() is string id && ours.Contains(id)) is { Count: > 0 } favoured)
            store["favorites"] = favoured;
        if (pmc["InsuredItems"] is JsonArray insured
            && TakeWhere(insured, i => i?["itemId"]?.GetValue<string>() is string id && ours.Contains(id)) is { Count: > 0 } covered)
            store["insured"] = covered;
    }

    /// <summary>Removes the matching keys of an object; returns them with their values.</summary>
    private static JsonObject TakeKeys(JsonObject obj, Func<string, bool> match)
    {
        var taken = new JsonObject();
        foreach (string key in obj.Select(p => p.Key).Where(match).ToList())
        {
            JsonNode? value = obj[key];
            obj.Remove(key);
            taken[key] = value;
        }
        return taken;
    }

    /// <summary>Removes the matching entries of an array, keeping their order; returns them.</summary>
    private static JsonArray TakeWhere(JsonArray list, Func<JsonNode?, bool> match)
    {
        var taken = new JsonArray();
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (!match(list[i]))
                continue;
            JsonNode? node = list[i];
            list.RemoveAt(i);
            taken.Insert(0, node);
        }
        return taken;
    }

    /// <summary>True if any string anywhere in the node is one of our template ids (a build that holds our gun, say).</summary>
    private static bool Mentions(JsonNode? node) => node switch
    {
        JsonObject obj => obj.Any(p => Mentions(p.Value)),
        JsonArray array => array.Any(Mentions),
        JsonValue value => value.GetValueKind() == JsonValueKind.String && ThundergunIds.IsOurs(value.GetValue<string>()),
        _ => false,
    };

    /// <summary>
    /// Insurance returns waiting to be sent: our items come out of each. A return that would be left with nothing
    /// is taken whole, so the game never mails an empty package.
    /// </summary>
    private static JsonArray TakeInsurance(JsonArray insurance)
    {
        var taken = new JsonArray();
        for (int i = insurance.Count - 1; i >= 0; i--)
        {
            if (insurance[i] is not JsonObject package || package["items"] is not JsonArray items || !items.Any(Mentions))
                continue;
            var key = new JsonObject { ["trader"] = package["traderId"]?.DeepClone(), ["time"] = package["scheduledTime"]?.DeepClone() };
            JsonArray ours = TakeOurs(items);
            if (items.Count == 0)
            {
                // Whole, as it was (a node can only have one parent: out of the taken list first)
                foreach (JsonNode? node in ours.ToList())
                {
                    ours.Remove(node);
                    items.Add(node);
                }
                insurance.RemoveAt(i);
                key["whole"] = package;
            }
            else
                key["items"] = ours;
            taken.Insert(0, key);
        }
        return taken;
    }

    /// <summary>Puts a profile's stored items back after SPT loaded it. Returns what it restored, for the log.</summary>
    public static string? Restore(MongoId profileId, SptProfile profile, JsonUtil jsonUtil)
    {
        string id = profileId.ToString().ToLowerInvariant(), storePath = StorePath(id);
        if (!File.Exists(storePath))
            return null;
        string profilePath = Path.Combine("user/profiles/", $"{profileId}.json");
        if (File.Exists(profilePath) && File.GetLastWriteTimeUtc(profilePath) - File.GetLastWriteTimeUtc(storePath) > StaleAfter)
        {
            File.Move(storePath, storePath + ".stale", true);
            return "nothing (side file older than the profile, set aside as .stale)";
        }

        if (JsonNode.Parse(File.ReadAllText(storePath)) is not JsonObject store)
            return null;
        PmcData? pmc = profile.CharacterData?.PmcData;
        int items = 0, mailItems = 0;

        if (store["items"] is JsonArray stored && pmc?.Inventory?.Items is { } inventory)
            items = AddMissing(inventory, stored, jsonUtil);

        if (store["encyclopedia"] is JsonObject seen && pmc != null)
        {
            pmc.Encyclopedia ??= new Dictionary<MongoId, bool>();
            foreach (var (tpl, value) in seen)
            {
                if (value?.GetValueKind() is JsonValueKind.True or JsonValueKind.False)
                    pmc.Encyclopedia.TryAdd(new MongoId(tpl), value.GetValue<bool>());
            }
        }

        if (store["mail"] is JsonArray mail && profile.DialogueRecords is { } dialogues)
        {
            foreach (JsonNode? entry in mail)
            {
                string? dialogueId = entry?["dialogue"]?.GetValue<string>(), messageId = entry?["message"]?.GetValue<string>();
                if (dialogueId == null || messageId == null || entry!["items"] is not JsonArray mailStored)
                    continue;
                Dialogue? dialogue = dialogues.FirstOrDefault(d => d.Key.ToString() == dialogueId).Value;
                Message? message = dialogue?.Messages?.FirstOrDefault(m => m.Id.ToString() == messageId);
                if (message == null)
                    continue; // the mail is gone (collected or expired)
                message.Items ??= new MessageItems();
                message.Items.Data ??= [];
                mailItems += AddMissing(message.Items.Data, mailStored, jsonUtil);
            }
        }
        int references = pmc != null ? RestoreReferences(pmc, store, jsonUtil) : 0;
        int returns = store["insurance"] is JsonArray insurance ? RestoreInsurance(profile, insurance, jsonUtil) : 0;
        int builds = 0;
        if (store["builds"] is JsonObject storedBuilds)
        {
            profile.UserBuildData ??= new UserBuilds();
            UserBuilds b = profile.UserBuildData;
            if (storedBuilds["weaponBuilds"] is JsonArray weapon)
                builds += AddBuilds(b.WeaponBuilds ??= [], weapon, jsonUtil);
            if (storedBuilds["equipmentBuilds"] is JsonArray equipment)
                builds += AddBuilds(b.EquipmentBuilds ??= [], equipment, jsonUtil);
            if (storedBuilds["magazineBuilds"] is JsonArray magazine)
                builds += AddBuilds(b.MagazineBuilds ??= [], magazine, jsonUtil);
        }
        lock (Restored)
            Restored.Add(id);
        return $"{items} item(s), {mailItems} in mail, {references} reference(s), {returns} in insurance, {builds} build(s)";
    }

    /// <summary>Wishlist, fast panel, favourites and insurance cover, each only where nothing has taken its place.</summary>
    private static int RestoreReferences(PmcData pmc, JsonObject store, JsonUtil jsonUtil)
    {
        int restored = 0;
        if (store["wishList"] is JsonObject wished)
        {
            pmc.WishList ??= new Dictionary<MongoId, int>();
            foreach (var (tpl, group) in wished)
            {
                if (group?.GetValueKind() == JsonValueKind.Number && pmc.WishList.TryAdd(new MongoId(tpl), group.GetValue<int>()))
                    restored++;
            }
        }
        if (pmc.Inventory is not { } inventory)
            return restored;
        if (store["fastPanel"] is JsonObject bound)
        {
            inventory.FastPanel ??= new Dictionary<string, MongoId>();
            foreach (var (slot, itemId) in bound)
            {
                if (itemId?.GetValueKind() == JsonValueKind.String && inventory.FastPanel.TryAdd(slot, new MongoId(itemId.GetValue<string>())))
                    restored++;
            }
        }
        if (store["favorites"] is JsonArray favoured)
        {
            List<MongoId> favourites = inventory.FavoriteItems?.ToList() ?? [];
            foreach (JsonNode? node in favoured)
            {
                if (node?.GetValueKind() == JsonValueKind.String && new MongoId(node.GetValue<string>()) is var itemId && !favourites.Contains(itemId))
                {
                    favourites.Add(itemId);
                    restored++;
                }
            }
            inventory.FavoriteItems = favourites;
        }
        if (store["insured"] is JsonArray covered)
        {
            pmc.InsuredItems ??= [];
            foreach (JsonNode? node in covered)
            {
                if (node != null && jsonUtil.Deserialize<InsuredItem>(node.ToJsonString()) is { } insured && pmc.InsuredItems.All(i => i.ItemId != insured.ItemId))
                {
                    pmc.InsuredItems.Add(insured);
                    restored++;
                }
            }
        }
        return restored;
    }

    /// <summary>Our items back into their insurance returns (matched by trader and time), or whole returns back in.</summary>
    private static int RestoreInsurance(SptProfile profile, JsonArray stored, JsonUtil jsonUtil)
    {
        profile.InsuranceList ??= [];
        int restored = 0;
        foreach (JsonNode? entry in stored)
        {
            string? trader = entry?["trader"]?.GetValue<string>();
            int? time = entry?["time"]?.GetValue<int>();
            Insurance? package = profile.InsuranceList.FirstOrDefault(p => p.TraderId.ToString() == trader && p.ScheduledTime == time);
            if (entry?["whole"] is JsonObject whole)
            {
                // Already sent while the mod was away: nothing to put back
                if (package == null && jsonUtil.Deserialize<Insurance>(whole.ToJsonString()) is { } back)
                {
                    profile.InsuranceList.Add(back);
                    restored += back.Items?.Count ?? 0;
                }
            }
            else if (package != null && entry?["items"] is JsonArray items)
                restored += AddMissing(package.Items ??= [], items, jsonUtil);
        }
        return restored;
    }

    private static int AddBuilds<T>(List<T> list, JsonArray stored, JsonUtil jsonUtil) where T : UserBuild
    {
        int added = 0;
        foreach (JsonNode? node in stored)
        {
            if (node == null || jsonUtil.Deserialize<T>(node.ToJsonString()) is not { } build || list.Any(b => b.Id == build.Id))
                continue;
            list.Add(build);
            added++;
        }
        return added;
    }

    private static int AddMissing(List<Item> list, JsonArray stored, JsonUtil jsonUtil)
    {
        int added = 0;
        foreach (JsonNode? node in stored)
        {
            if (node == null || jsonUtil.Deserialize<Item>(node.ToJsonString()) is not { } item || list.Any(i => i.Id == item.Id))
                continue;
            list.Add(item);
            added++;
        }
        return added;
    }

    /// <summary>Every profile file SPT writes goes through FileUtil.WriteFileAsync (SaveServer.SaveProfileAsync).</summary>
    internal sealed class ProfileWritePatch : AbstractPatch
    {
        protected override MethodBase? GetTargetMethod() =>
            typeof(FileUtil).GetMethod(nameof(FileUtil.WriteFileAsync), [typeof(string), typeof(string), typeof(CancellationToken)]);

        [PatchPrefix]
        private static void Prefix(string filePath, ref string fileContent) => fileContent = Strip(filePath, fileContent);
    }
}

/// <summary>Hooks profile writing before anything is saved.</summary>
[Injectable(TypePriority = OnLoadOrder.Preload + 2)]
public class ThundergunProfileStoreHook(ISptLogger<ThundergunProfileStoreHook> logger) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        ThundergunProfileStore.LogError = message => logger.Error(message);
        try
        {
            new ThundergunProfileStore.ProfileWritePatch().Enable();
        }
        catch (Exception e)
        {
            logger.Error($"[Thundergun] Profile write hook failed and is disabled (profiles keep the mod's items): {e.GetBaseException().Message}");
        }
        return Task.CompletedTask;
    }
}

/// <summary>Right after SPT loads the profiles (SaveCallbacks): the mod's items back into them.</summary>
[Injectable(TypePriority = OnLoadOrder.SaveCallbacks + 1)]
public class ThundergunProfileStoreLoader(ISptLogger<ThundergunProfileStoreLoader> logger, SaveServer saveServer, JsonUtil jsonUtil) : IOnLoad
{
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        foreach (var (id, profile) in saveServer.GetProfiles())
        {
            try
            {
                string? restored = ThundergunProfileStore.Restore(id, profile, jsonUtil);
                if (restored != null)
                    logger.Info($"[Thundergun] Profile {id}: restored {restored} from the mod's side file");
            }
            catch (Exception e)
            {
                logger.Error($"[Thundergun] Restoring the mod's items into profile {id} failed: {e.Message}");
            }
        }
        return Task.CompletedTask;
    }
}
