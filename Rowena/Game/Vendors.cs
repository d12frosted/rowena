using System.Numerics;
using Dalamud.Plugin.Services;
using Dalamud.Utility;
using Lumina.Excel.Sheets;

namespace Rowena.Game;

/// <summary>Where a counter stands: who, and at what spot on what map.</summary>
/// <param name="World">The position the game uses, for walking to.</param>
/// <param name="Map">The coordinates the map shows, for reading and flagging.</param>
internal readonly record struct Spot(
    string Npc,
    uint TerritoryId,
    string Zone,
    uint MapId,
    Vector3 World,
    Vector2 Map);

/// <summary>
/// Which NPCs offer which shops, and where those NPCs stand.
/// </summary>
/// <remarks>
/// A shop's name is the one thing about it nobody can act on. "Allagan Tomestones of
/// Mathematics (Other)" is true and useless; the question is who to walk up to and where.
///
/// The routes from a shop to an NPC. Some shops are named directly in an NPC's data. Most
/// are not: the NPC offers a menu, a TopicSelect, or a PreHandler gate, or a CustomTalk
/// script whose arguments name the next step, and the scrip and tomestone exchanges sit a
/// further level down behind an InclusionShop, the category picker, with the real shops
/// hanging off its categories and series. Each indirection is followed until a shop id
/// falls out. Seal shops are GCShop rows reached the same way. Every route ends in an NPC
/// id, and the Level sheet says where each NPC is placed, on which map.
///
/// An exchange is often offered in several cities, Rowena's representatives being the
/// obvious case, so a shop maps to a list of spots rather than one. Which of them to show is
/// the window's business, since it knows where you are standing.
/// </remarks>
internal sealed class Vendors(IDataManager data, IPluginLog log)
{
    /// <summary>Level rows of this type place event NPCs.</summary>
    private const byte EventNpcLevel = 8;

    private Dictionary<uint, List<uint>>? npcsByShop;
    private Dictionary<uint, List<Spot>>? spotsByNpc;

    /// <summary>Everywhere a shop is offered, empty when no NPC in the world offers it.</summary>
    public IReadOnlyList<Spot> ForShop(uint shopId)
    {
        Build();

        if (!npcsByShop!.TryGetValue(shopId, out var npcs))
            return [];

        return
        [
            .. npcs
                .SelectMany(npc => spotsByNpc!.GetValueOrDefault(npc) ?? [])
                .DistinctBy(spot => (spot.TerritoryId, spot.Npc)),
        ];
    }

    /// <summary>The seal shops of a grand company, which share one quartermaster per city.</summary>
    public IReadOnlyList<Spot> ForGrandCompany(uint grandCompanyRow)
    {
        Build();

        return
        [
            .. data.GetExcelSheet<GCShop>()
                .Where(shop => shop.GrandCompany.RowId == grandCompanyRow)
                .SelectMany(shop => ForShop(shop.RowId))
                .DistinctBy(spot => (spot.TerritoryId, spot.Npc)),
        ];
    }

    private void Build()
    {
        if (npcsByShop is not null)
            return;

        npcsByShop = new Dictionary<uint, List<uint>>();
        spotsByNpc = new Dictionary<uint, List<Spot>>();

        // The picker shops, expanded to the real shops behind them.
        var seriesToShops = new Dictionary<uint, List<uint>>();
        foreach (var row in data.GetSubrowExcelSheet<InclusionShopSeries>().Flatten())
        {
            if (row.SpecialShop.RowId == 0)
                continue;

            if (!seriesToShops.TryGetValue(row.RowId, out var list))
                seriesToShops[row.RowId] = list = [];

            list.Add(row.SpecialShop.RowId);
        }

        var categoryToShops = new Dictionary<uint, List<uint>>();
        foreach (var category in data.GetExcelSheet<InclusionShopCategory>())
        {
            if (seriesToShops.TryGetValue(category.InclusionShopSeries.RowId, out var shops))
                categoryToShops[category.RowId] = shops;
        }

        var pickerToShops = new Dictionary<uint, List<uint>>();
        foreach (var picker in data.GetExcelSheet<InclusionShop>())
        {
            var shops = picker.Category
                .Where(category => category.RowId != 0)
                .SelectMany(category => categoryToShops.GetValueOrDefault(category.RowId) ?? [])
                .Distinct()
                .ToList();

            if (shops.Count > 0)
                pickerToShops[picker.RowId] = shops;
        }

        // The menus an NPC can put between you and a shop. "Purchase items" is a TopicSelect
        // listing shops; a PreHandler gates one behind an unlock; a CustomTalk is a script
        // whose arguments can name any of these. Each is followed until a shop id falls out.
        var topicToShops = new Dictionary<uint, List<uint>>();
        foreach (var topic in data.GetExcelSheet<TopicSelect>())
        {
            var shops = topic.Shop.Select(shop => shop.RowId).Where(id => id != 0).ToList();
            if (shops.Count > 0)
                topicToShops[topic.RowId] = shops;
        }

        var handlerToTarget = new Dictionary<uint, uint>();
        foreach (var handler in data.GetExcelSheet<PreHandler>())
        {
            if (handler.Target.RowId != 0)
                handlerToTarget[handler.RowId] = handler.Target.RowId;
        }

        var talkToArgs = new Dictionary<uint, List<uint>>();
        foreach (var talk in data.GetExcelSheet<CustomTalk>())
        {
            var args = talk.Script.Select(script => script.ScriptArg).Where(arg => arg != 0).Distinct().ToList();
            if (args.Count > 0)
                talkToArgs[talk.RowId] = args;
        }

        void Follow(uint id, uint npcId, int depth)
        {
            // Scripts can reference scripts; four levels is more than the game uses, and a
            // bound is cheaper than proving the graph has no cycles.
            if (id == 0 || depth > 4)
                return;

            if (pickerToShops.TryGetValue(id, out var behind))
            {
                foreach (var shop in behind)
                    Offer(shop, npcId);
            }
            else if (topicToShops.TryGetValue(id, out var listed))
            {
                foreach (var shop in listed)
                    Follow(shop, npcId, depth + 1);
            }
            else if (handlerToTarget.TryGetValue(id, out var target))
            {
                Follow(target, npcId, depth + 1);
            }
            else if (talkToArgs.TryGetValue(id, out var args))
            {
                foreach (var arg in args)
                    Follow(arg, npcId, depth + 1);
            }
            else
            {
                // Any other id is offered as itself; whether it is a shop this plugin reads
                // is decided by whoever asks.
                Offer(id, npcId);
            }
        }

        var residents = data.GetExcelSheet<ENpcResident>();

        foreach (var npc in data.GetExcelSheet<ENpcBase>())
        {
            foreach (var dataRef in npc.ENpcData)
                Follow(dataRef.RowId, npc.RowId, 0);
        }

        var territories = data.GetExcelSheet<TerritoryType>();
        var maps = data.GetExcelSheet<Map>();

        foreach (var level in data.GetExcelSheet<Level>())
        {
            if (level.Type != EventNpcLevel || level.Object.RowId == 0)
                continue;

            var territory = territories.GetRowOrDefault(level.Territory.RowId);
            if (territory is null)
                continue;

            var mapId = level.Map.RowId != 0 ? level.Map.RowId : territory.Value.Map.RowId;
            if (maps.GetRowOrDefault(mapId) is not { } map)
                continue;

            var npcName = residents.GetRowOrDefault(level.Object.RowId)?.Singular.ExtractText();
            var zone = territory.Value.PlaceName.ValueNullable?.Name.ExtractText();

            var spot = new Spot(
                string.IsNullOrWhiteSpace(npcName) ? "vendor" : npcName,
                territory.Value.RowId,
                string.IsNullOrWhiteSpace(zone) ? $"zone {territory.Value.RowId}" : zone,
                mapId,
                new Vector3(level.X, level.Y, level.Z),
                MapUtil.WorldToMap(new Vector2(level.X, level.Z), map));

            if (!spotsByNpc.TryGetValue(level.Object.RowId, out var spots))
                spotsByNpc[level.Object.RowId] = spots = [];

            if (spots.All(existing => existing.TerritoryId != spot.TerritoryId))
                spots.Add(spot);
        }

        log.Information($"Placed {spotsByNpc.Count} vendors on the map.");
    }

    private void Offer(uint shopId, uint npcId)
    {
        if (!npcsByShop!.TryGetValue(shopId, out var npcs))
            npcsByShop[shopId] = npcs = [];

        if (!npcs.Contains(npcId))
            npcs.Add(npcId);
    }
}
