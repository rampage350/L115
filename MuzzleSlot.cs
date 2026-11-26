using System.Reflection;
using System.Text.Json;

using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Utils;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;
using SPTarkov.Common.Models.Logging;

namespace L115AWM;

// Adds the "mod_muzzle" slot to the L115 and lets it accept ONLY silencers that fit 7.62x51 / .308, 7.62x54R, .338 and .50 weapons.
//
// Why a second pass at PostLoad: the main class (L115Mod) runs at Preload, before other mods add their weapons and silencers.
// This one runs after them, so silencers / rifles added by other mods are picked up too. It only edits existing templates
// (no items are added), so it is safe after profiles have loaded.
//
// How the list is built (live database, nothing hard-coded): every weapon whose ammoCaliber is in "muzzle.calibers" is walked
// through all of its slots; every item listed in one of its muzzle slots (mod_muzzle*) that sits under the Silencer category is allowed.
// That is exactly "the silencers that vanilla / modded rifles of those calibers can take". Then:
//   + muzzle.extraItems     (item ids, e.g. a .50 silencer from another mod)
//   - muzzle.excludeItems   (item ids)
//   - silencers whose internal name contains one of muzzle.excludeNameContains (other-caliber suppressors that only reach
//     these rifles through AK-style adapters, e.g. "_762x39")
//
// config.json:
//   "muzzle": { "enabled": true, "slotName": "mod_muzzle", "calibers": [...], "extraItems": [], "excludeItems": [], "excludeNameContains": [...] }
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class L115MuzzleSlot(
    ISptLogger<L115MuzzleSlot> logger,
    TemplateTable templateTable,
    ICloner cloner) : IOnLoad
{
    private const string Tag = "[L115 muzzle] ";
    private static readonly MongoId SilencerRoot = new("550aa4cd4bdc2dd8348b456c");
    private static readonly MongoId WeaponRoot   = new("5422acb9af1c889c16000029");

    private sealed class MuzzleCfg
    {
        public bool enabled { get; set; } = true;
        public string slotName { get; set; } = "mod_muzzle";
        // both spellings of .338 are listed because the caliber id differs between SPT versions; 12.7x99 = .50 BMG
        public List<string> calibers { get; set; } = new()
        {
            "Caliber762x51", "Caliber308", "Caliber762x54R",
            "Caliber338LapuaMag", "Caliber86x70",
            "Caliber127x99", "Caliber50BMG"
        };
        public List<string> extraItems { get; set; } = new();
        public List<string> excludeItems { get; set; } = new();
        public List<string> excludeNameContains { get; set; } = new()
        {
            "_762x39", "_545x39", "_556x45", "_9x39", "_9x19", "_9x18", "_9x21", "_57x28", "_46x30", "_366tkm", "_1143x23", "_12g", "_22lr", "_68x51", "_65x35"
        };
    }

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try { Run(); }
        catch (Exception e) { logger.Error(Tag + "failed: " + e); }
        return Task.CompletedTask;
    }

    private void Run()
    {
        var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        var cfgPath = System.IO.Path.Combine(dir, "config.json");
        if (!File.Exists(cfgPath)) { logger.Warning(Tag + "config.json not found, skipped"); return; }

        var opt = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
        var cfg = doc.RootElement.TryGetProperty("muzzle", out var m)
            ? JsonSerializer.Deserialize<MuzzleCfg>(m.GetRawText(), opt) ?? new MuzzleCfg()
            : new MuzzleCfg();
        if (!cfg.enabled) { logger.Info(Tag + "disabled in config.json"); return; }

        var ourWeapons = new List<string>();
        if (doc.RootElement.TryGetProperty("weapons", out var w))
            foreach (var e in w.EnumerateArray()) { var s = e.GetString(); if (!string.IsNullOrEmpty(s)) ourWeapons.Add(s); }
        if (ourWeapons.Count == 0) { logger.Warning(Tag + "no weapons in config.json"); return; }

        var ids = BuildSilencerList(cfg);
        logger.Info(Tag + ids.Count + " silencer(s) allowed in " + cfg.slotName);
        if (ids.Count == 0) { logger.Warning(Tag + "no silencers found, slot not added"); return; }

        foreach (var tpl in ourWeapons)
        {
            if (!templateTable.Items.TryGetValue(new MongoId(tpl), out var gun) || gun?.Properties == null)
            { logger.Warning(Tag + "weapon " + tpl + " not found"); continue; }
            AddOrFillSlot(gun, tpl, cfg.slotName, ids);
        }
    }

    // -------------------------------------------------------------------------------------------------------------
    private bool IsUnder(TemplateItem it, MongoId root)
    {
        var cur = it.Parent;
        for (var i = 0; i < 12; i++)
        {
            if (cur.Equals(root)) return true;
            if (!templateTable.Items.TryGetValue(cur, out var n) || n == null) return false;
            cur = n.Parent;
        }
        return false;
    }

    private List<MongoId> BuildSilencerList(MuzzleCfg cfg)
    {
        var calibers = new HashSet<string>(cfg.calibers, StringComparer.OrdinalIgnoreCase);
        var result = new HashSet<MongoId>();
        var matchedCalibers = new HashSet<string>();
        var seenCalibers127 = new HashSet<string>();
        var weaponCount = 0;

        foreach (var kv in templateTable.Items)
        {
            var gun = kv.Value;
            var cal = gun?.Properties?.AmmoCaliber;
            if (gun == null || string.IsNullOrEmpty(cal)) continue;
            if (cal.Contains("127")) seenCalibers127.Add(cal);
            if (!calibers.Contains(cal) || !IsUnder(gun, WeaponRoot)) continue;
            weaponCount++;
            matchedCalibers.Add(cal);

            // walk every slot of this weapon, collect what its muzzle slots allow
            var seen = new HashSet<MongoId>();
            var stack = new Stack<MongoId>();
            stack.Push(kv.Key);
            while (stack.Count > 0)
            {
                var id = stack.Pop();
                if (!seen.Add(id)) continue;
                if (!templateTable.Items.TryGetValue(id, out var it) || it?.Properties?.Slots == null) continue;
                foreach (var slot in it.Properties.Slots)
                {
                    var isMuzzle = slot.Name != null && slot.Name.StartsWith("mod_muzzle");
                    var slotFilters = slot.Properties?.Filters;
                    if (slotFilters == null) continue;
                    foreach (var f in slotFilters)
                    {
                        if (f.Filter == null) continue;
                        foreach (var cid in f.Filter)
                        {
                            stack.Push(cid);
                            if (isMuzzle && templateTable.Items.TryGetValue(cid, out var c) && c != null && IsUnder(c, SilencerRoot))
                                result.Add(cid);
                        }
                    }
                }
            }
        }

        logger.Info(Tag + weaponCount + " weapon(s) in calibers [" + string.Join(", ", matchedCalibers) + "]");
        foreach (var c in cfg.calibers)
            if (!matchedCalibers.Contains(c)) logger.Debug(Tag + "no weapon uses caliber " + c);
        if (seenCalibers127.Count > 0)
            logger.Info(Tag + "12.7mm calibers present in the database: " + string.Join(", ", seenCalibers127) + " (add the .50 one to muzzle.calibers if it is not listed)");

        // other-caliber suppressors that came in through adapters
        result.RemoveWhere(id =>
            templateTable.Items.TryGetValue(id, out var it) && it?.Name != null &&
            cfg.excludeNameContains.Any(s => it.Name.Contains(s, StringComparison.OrdinalIgnoreCase)));

        foreach (var x in cfg.extraItems)
        {
            if (templateTable.Items.ContainsKey(new MongoId(x))) result.Add(new MongoId(x));
            else logger.Warning(Tag + "extraItems id not found in database: " + x);
        }
        foreach (var x in cfg.excludeItems) result.Remove(new MongoId(x));

        return result.ToList();
    }

    // -------------------------------------------------------------------------------------------------------------
    private void AddOrFillSlot(TemplateItem gun, string gunTpl, string slotName, List<MongoId> ids)
    {
        var p = gun.Properties!;
        var list = (p.Slots ?? Enumerable.Empty<Slot>()).ToList();
        var slot = list.FirstOrDefault(s => s.Name == slotName);
        var created = false;

        if (slot == null)
        {
            // same trick as the tactical slots in L115Mod: clone a slot the weapon already has and rename it
            var template = list.FirstOrDefault(s => s.Name == "mod_mount") ?? list.FirstOrDefault();
            if (template == null) { logger.Warning(Tag + "weapon " + gunTpl + " has no slot to clone"); return; }
            slot = cloner.Clone(template)!;
            slot.GetType().GetProperty("Name")?.GetSetMethod(true)?.Invoke(slot, new object[] { slotName });
            slot.Id = new MongoId();
            slot.Parent = new MongoId(gunTpl);
            list.Add(slot);
            created = true;
        }

        var filters = slot.Properties?.Filters?.ToList();
        if (filters == null || filters.Count == 0) { logger.Warning(Tag + "slot " + slotName + " has no filter object"); return; }
        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i].Filter == null) continue;
            filters[i].Filter!.Clear();
            if (i == 0) foreach (var id in ids) filters[i].Filter!.Add(id);
        }
        p.Slots = list;
        logger.Info(Tag + "weapon " + gunTpl + ": " + (created ? "added" : "updated") + " slot " + slotName + " (" + ids.Count + " silencers)");
    }
}
