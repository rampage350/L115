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

// Items MUST be added before profiles load, otherwise the server aborts with
// "item(s) were added to the database after profiles loaded". OnLoadOrder.Preload is what the error message asks for.
[Injectable(TypePriority = OnLoadOrder.Preload)]
public class L115Mod(
    ISptLogger<L115Mod> logger,
    TemplateTable templateTable,
    TradersTable tradersTable,
    ICloner cloner,
    WTTServerCommonLib.WTTServerCommonLib wttCommon) : IOnLoad
{
    private const string Roubles = "5449016a4bdc2d6f028b456f";
    private const string Tag = "[L115] ";

    private sealed class Offer { public int loyaltyLevel { get; set; } = 1; public double priceRoubles { get; set; } }
    private sealed class TraderEntry { public string id { get; set; } = ""; public string name { get; set; } = ""; public List<Offer> offers { get; set; } = new(); }
    private sealed class Config
    {
        public string ammoCaliber { get; set; } = "Caliber338LapuaMag";
        public List<string> ammoTpls { get; set; } = new();
        public bool addToTraders { get; set; }
        public List<TraderEntry> traders { get; set; } = new();
        public List<string> weapons { get; set; } = new();
        public List<string> magazines { get; set; } = new();
        public string copyMuzzleAndMountCompatFrom { get; set; } = "";
        public Dictionary<string, int> magazineCapacity { get; set; } = new();
        // inventory size: rifle alone = weaponWidth x weaponHeight; each magazine adds magazineExtraSizeDown rows below the rifle when attached
        public int weaponWidth { get; set; } = 6;
        public int weaponHeight { get; set; } = 1;
        public int magazineExtraSizeDown { get; set; } = 1;
        // slots inherited from the cloned M700 that the L115 mesh already has built in
        public List<string> removeSlots { get; set; } = new() { "mod_barrel", "mod_stock" };
        // slots that exist as transforms in the L115 prefab (mod_tactical / mod_tactical_000) but are missing in the cloned M700 template
        public List<string> addTacticalSlots { get; set; } = new() { "mod_tactical", "mod_tactical_000" };
        // bottom slot (mod_tactical): only these items. Default = DD offset flashlight mount (M600) + Atlas V8 BT10 bipod
        public List<string> bottomSlotItems { get; set; } = new() { "6267c6396b642f77f56f5c1c", "6644920d49817dc7d505ca71" };
        // extra item ids to hide from the top slot (mod_tactical_000), on top of the plain flashlights that are already left out
        public List<string> topSlotExclude { get; set; } = new();
        // extra item ids (e.g. from other mods) that mod_mount should accept on top of the built-in allowlist
        public List<string> mountExtraItems { get; set; } = new();
    }

    public async Task OnLoadAsync(CancellationToken cancellationToken)
    {
        var asm = Assembly.GetExecutingAssembly();
        var dir = System.IO.Path.GetDirectoryName(asm.Location)!;
        var cfgPath = System.IO.Path.Combine(dir, "config.json");
        var cfg = File.Exists(cfgPath)
            ? JsonSerializer.Deserialize<Config>(File.ReadAllText(cfgPath)) ?? new Config()
            : new Config();

        // 1. items from db/CustomItems/*.json (clone, locales, handbook, flea) + bundle registration
        await wttCommon.CustomItemServiceExtended.CreateCustomItems(asm);

        // 2. caliber / chamber / magazine wiring
        try { Wire(cfg); }
        catch (Exception e) { logger.Error(Tag + "wiring failed: " + e); }

        // 3. trader stock
        if (cfg.addToTraders)
        {
            try { AddToTraders(cfg); }
            catch (Exception e) { logger.Error(Tag + "trader assort failed: " + e); }
        }
        logger.Info(Tag + "loaded");
    }

    private bool Get(string tpl, out TemplateItem item)
    {
        if (templateTable.Items.TryGetValue(new MongoId(tpl), out var found) && found != null) { item = found; return true; }
        item = null!;
        return false;
    }

    // Sets a numeric template property by name (reflection keeps this independent of int / int? / double? property types).
    private void SetNum(object? target, string name, int value)
    {
        if (target == null) return;
        var pi = target.GetType().GetProperty(name);
        if (pi == null || !pi.CanWrite) { logger.Warning(Tag + "property not found/writable: " + name); return; }
        var type = Nullable.GetUnderlyingType(pi.PropertyType) ?? pi.PropertyType;
        pi.SetValue(target, Convert.ChangeType(value, type));
    }

    private static void Fill(IEnumerable<Slot>? slots, IEnumerable<MongoId> ids, bool replace)
    {
        if (slots == null) return;
        foreach (var s in slots)
        {
            var filters = s.Properties?.Filters;
            if (filters == null) continue;
            foreach (var f in filters)
            {
                if (f.Filter == null) continue;
                if (replace) f.Filter.Clear();
                foreach (var id in ids) f.Filter.Add(id);
            }
        }
    }

    private void Wire(Config cfg)
    {
        // Only keep ammo ids that really exist: an unknown id in a filter makes the client crash (Magazine..ctor NRE).
        var ammo = new List<MongoId>();
        foreach (var a in cfg.ammoTpls)
        {
            if (Get(a, out _)) ammo.Add(new MongoId(a));
            else logger.Warning(Tag + "ammo tpl not found in database, skipped: " + a + " (check config.json ammoTpls)");
        }
        logger.Info(Tag + "ammo types in use: " + string.Join(", ", ammo.Select(x => x.ToString())));
        // only magazines that really exist may be listed in the weapon's magazine slot
        var mags = cfg.magazines.Where(m => Get(m, out _)).Select(m => new MongoId(m)).ToList();

        // A real magazine that takes our ammo, used as a source for the Cartridges slot when the cloned item has none.
        var magParent = new MongoId("5448bc234bdc2d3c308b4569");
        var ours = cfg.magazines.Select(x => new MongoId(x)).ToHashSet();
        TemplateItem? donorMag = null;
        if (ammo.Count > 0)
        {
            foreach (var kv in templateTable.Items)
            {
                var it = kv.Value;
                if (it == null || ours.Contains(kv.Key) || !it.Parent.Equals(magParent)) continue;
                var c0 = it.Properties?.Cartridges?.FirstOrDefault();
                var f0 = c0?.Properties?.Filters?.FirstOrDefault()?.Filter;
                if (f0 != null && f0.Contains(ammo[0])) { donorMag = it; logger.Info(Tag + "donor magazine for Cartridges: " + kv.Key + " (" + it.Name + ")"); break; }
            }
        }

        foreach (var m in cfg.magazines)
        {
            if (!Get(m, out var mag)) { logger.Error(Tag + "magazine " + m + " was not created. Is the clone id in db/CustomItems/mag_l115_awm.json valid?"); continue; }
            if (mag.Properties == null) continue;

            var hasCart = mag.Properties.Cartridges != null && mag.Properties.Cartridges.Any();
            if (!hasCart)
            {
                if (donorMag?.Properties?.Cartridges == null) { logger.Error(Tag + "magazine " + m + " has no Cartridges slot and no donor magazine was found. The itemTplToClone in mag_l115_awm.json is not a magazine."); continue; }
                logger.Warning(Tag + "magazine " + m + " had no Cartridges slot (itemTplToClone is not a magazine); copying it from the donor magazine");
                var copy = cloner.Clone(donorMag.Properties.Cartridges)!;
                foreach (var slot in copy) { slot.Id = new MongoId(); slot.Parent = new MongoId(m); }
                mag.Properties.Cartridges = copy;
            }

            // The clone is not a real magazine, so the other magazine-only fields are empty too. Copy them from the donor
            // (only where still null) so the client's MagazineTemplate is complete. Reflection keeps this independent of exact property names.
            if (!hasCart && donorMag?.Properties != null)
            {
                foreach (var name in new[] { "ReloadMagType", "LoadUnloadModifier", "CheckTimeModifier", "CheckOverride", "MalfunctionChance",
                                             "MagAnimationIndex", "magAnimationIndex", "CanFast", "CanHit", "CanAdmin", "VisibleAmmoRangesString",
                                             "IsMagazineForStationaryWeapon", "MagazineWithBelt" })
                {
                    var pi = mag.Properties.GetType().GetProperty(name);
                    if (pi == null || !pi.CanWrite || !pi.CanRead) continue;
                    if (pi.GetValue(mag.Properties) == null) pi.SetValue(mag.Properties, pi.GetValue(donorMag.Properties));
                }
            }

            // inventory size: the magazine grows the rifle downwards by N rows while it is attached
            SetNum(mag.Properties, "ExtraSizeLeft", 0);
            SetNum(mag.Properties, "ExtraSizeRight", 0);
            SetNum(mag.Properties, "ExtraSizeUp", 0);
            SetNum(mag.Properties, "ExtraSizeDown", cfg.magazineExtraSizeDown);
            logger.Info(Tag + "magazine " + m + ": ExtraSizeDown = " + cfg.magazineExtraSizeDown);

            Fill(mag.Properties.Cartridges, ammo, true);
            if (cfg.magazineCapacity.TryGetValue(m, out var cap))
                foreach (var slot in mag.Properties.Cartridges!) slot.MaxCount = cap;
        }

        TemplateItem? donor = null;
        if (cfg.copyMuzzleAndMountCompatFrom != "" && !Get(cfg.copyMuzzleAndMountCompatFrom, out donor))
            logger.Warning(Tag + "donor weapon " + cfg.copyMuzzleAndMountCompatFrom + " not found; attachment compat stays M700");

        var sightIds = MountSlotItemIds(cfg);
        logger.Info(Tag + "mod_mount will accept " + sightIds.Count + " items (allowlist)");

        foreach (var w in cfg.weapons)
        {
            if (!Get(w, out var gun)) { logger.Error(Tag + "weapon " + w + " was not created. Is itemTplToClone valid?"); continue; }
            var p = gun.Properties!;

            // drop slots that are baked into the L115 model (inherited from the M700 clone)
            if (p.Slots != null && cfg.removeSlots.Count > 0)
            {
                var before = p.Slots.Count();
                p.Slots = p.Slots.Where(sl => !cfg.removeSlots.Contains(sl.Name)).ToList();
                logger.Info(Tag + "weapon " + w + ": removed " + (before - p.Slots.Count()) + " slot(s) (" + string.Join(", ", cfg.removeSlots) + ")");
            }
            SetNum(p, "Width", cfg.weaponWidth);
            SetNum(p, "Height", cfg.weaponHeight);
            logger.Info(Tag + "weapon " + w + ": inventory size " + cfg.weaponWidth + "x" + cfg.weaponHeight + " (+" + cfg.magazineExtraSizeDown + " row with magazine)");
            p.AmmoCaliber = cfg.ammoCaliber;
            Fill(p.Chambers, ammo, true);

            foreach (var slot in p.Slots ?? Enumerable.Empty<Slot>())
            {
                if (slot.Name == "mod_magazine") { Fill(new[] { slot }, mags, true); continue; }
                // mod_mount: only the allowlisted scope rings (25/30/34/35mm) and sights, see MountSlotItemIds (replaces the cloned M700 filter).
                if (slot.Name == "mod_mount") { Fill(new[] { slot }, sightIds, true); continue; }
                if (donor?.Properties?.Slots == null) continue;
                var d = donor.Properties.Slots.FirstOrDefault(x => x.Name == slot.Name);
                if (d?.Properties?.Filters == null) continue;
                var union = d.Properties.Filters.Where(f => f.Filter != null).SelectMany(f => f.Filter!).ToList();
                Fill(new[] { slot }, union, false);
            }
            logger.Info(Tag + "weapon " + w + " set to " + cfg.ammoCaliber + ", " + mags.Count + " magazine(s)");

            AddTacticalSlots(gun, w, cfg);
        }
    }

    // mod_mount allowlist. Everything else (rails, accessory mounts, iron sights, other scopes, ...) is removed.
    //  - ring scope mounts: 25mm, 30mm, 34mm, 35mm
    //  - EOTech HHS-1 (HHS), Trijicon ACOG (TA01NSN / TA11D)
    //  - thermal scopes
    //  - every scope with x1..x6 magnification (max magnification <= 6x)
    //  - every reflex / red dot sight: all items of the "Reflex sight" and "Compact reflex sight" categories (read from the live database)
    // Ids that do not exist in the database are skipped, so the list is safe if a mod / game version lacks some of them.
    private static readonly string[] MountAllowlist =
    {
        // 25mm ring scope mount
        "5dff77c759400025ea5150cf", // Leapers UTG 25mm ring scope mount
        // 30mm ring scope mounts
        "5b2389515acfc4771e1be0c0", // Burris AR-P.E.P.R. 30mm
        "618b9643526131765025ab35", // Geissele Super Precision 30mm
        "618bab21526131765025ab3f", // Geissele Super Precision 30mm (DDC)
        "5a37ca54c4a282000d72296a", // JP Enterprises Flat-Top 30mm
        "57c69dd424597774c03b7bbc", // Lobaev Arms 30mm
        "5bfebc5e0db834001a6694e5", // M700 30mm integral ring
        "5b3b99265acfc4704b4a1afb", // Nightforce Magmount 30mm
        "6171407e50224f204c1da3c5", // Recknagel Era-Tac 30mm
        "6567e751a715f85433025998", // SIG Sauer ALPHA4 30mm
        // 34mm ring scope mounts
        "62811f461d5df4475f46a332", // AI AX-50 34mm
        "5c86592b2e2216000e69e77c", // IEA Mil-Optics KH/F 34mm magmount
        "5aa66a9be5b5b0214e506e89", // Nightforce Magmount 34mm
        "5aa66c72e5b5b00016327c93", // Nightforce Magmount 34mm with Ruggedized Accessory Platform
        "61713cc4d8e3106d9806c109", // Recknagel Era-Tac 34mm
        // 35mm ring scope mount
        "6761759e7ee06333f108bf86", // Leupold Mark IMS 35mm
        // HHS
        "5c07dd120db834001c39092d", // EOTech HHS-1 hybrid sight
        "5c0a2cec0db834001b7ce47d", // EOTech HHS-1 hybrid sight (Tan)
        // ACOG
        "5c05293e0db83400232fff80", // Trijicon ACOG TA01NSN 4x32 (Black)
        "5c052a900db834001a66acbd", // Trijicon ACOG TA01NSN 4x32 (Tan)
        "59db7e1086f77448be30ddf3", // Trijicon ACOG TA11D 3.5x35
        // thermal scopes
        "63fc44e2429a8a166c7f61e6", // Armasight Zeus-Pro 640 2-8x50 thermal
        "67641b461c2eb66ade05dba6", // Cyclone Shakhin 3.7x thermal
        "5a1eaa87fcdbcb001865f75e", // Trijicon REAP-IR thermal
        "5d1b5e94d7ad1a2b865a96b0", // FLIR RS-32 2.25-9x 35mm thermal
        "6478641c19d732620e045e17", // SIG Sauer ECHO1 1-2x30mm thermal reflex
        "609bab8b455afd752b2e6138", // Torrey Pines Logic T12W thermal reflex
        // x1..x6 scopes
        "544a3f024bdc2d1d388b4568", // ELCAN Specter OS4x
        "57ac965c24597706be5f975c", // ELCAN SpecterDR 1x/4x
        "57aca93d2459771f2c7e26db", // ELCAN SpecterDR 1x/4x (FDE)
        "544a3a774bdc2d3a388b4567", // Leupold Mark 4 HAMR 4x24
        "5c1cdd512e22161b267d91ae", // Kiba Arms Short Prism 2.5x
        "5d2dc3e548f035404a1a4798", // Monstrum Compact Prism 2x32
        "626bb8532c923541184624b4", // SwampFox Trihawk Prism 3x30
        "62ea7c793043d74a0306e19f", // Steyr AUG A1 STG77 1.5x
        "62ebd290c427473eff0baafb", // Steyr AUG A3 M1 1.5x
        "618a75f0bd321d49084cd399", // NPZ 1P78-1 2.8x
        "57adff4f24597737f373b6e6", // SIG Sauer BRAVO4 4x30
        "5c0517910db83400232ffee5", // Valday PS-320 1/6x
    };

    // Explicitly removed from mod_mount, even if they belong to the reflex categories.
    private static readonly string[] MountExclude =
    {
        "57486e672459770abd687134", // OKP-7 reflex sight (Dovetail)
        "5947db3f86f77447880cf76f", // Axion Kobra EKP-8-02 reflex sight (Dovetail)
        "6113d6c3290d254f5e6b27db", // BelOMO PK-AA dovetail reflex sight
        "618a5d5852ecee1505530b2a", // NPZ PK1 Obzor dovetail reflex sight
        "6544d4187c5457729210d277", // Axion Kobra EKP-1S-03 reflex sight (Dovetail)
    };

    private List<MongoId> MountSlotItemIds(Config cfg)
    {
        var reflexRoots = new[]
        {
            new MongoId("55818ad54bdc2ddc698b4569"), // Reflex sight (collimators, red dots, holographic)
            new MongoId("55818acf4bdc2dde698b456b")  // Compact reflex sight
        };

        // category nodes are the ids that some item uses as its parent; real items are everything else
        var nodes = new HashSet<MongoId>();
        foreach (var kv in templateTable.Items)
            if (kv.Value != null) nodes.Add(kv.Value.Parent);

        bool UnderAny(TemplateItem it)
        {
            var cur = it.Parent;
            for (var i = 0; i < 12; i++)
            {
                if (reflexRoots.Any(r => r.Equals(cur))) return true;
                if (!templateTable.Items.TryGetValue(cur, out var n) || n == null) return false;
                cur = n.Parent;
            }
            return false;
        }

        var result = new HashSet<MongoId>();
        foreach (var id in MountAllowlist.Concat(cfg.mountExtraItems))
        {
            if (Get(id, out _)) result.Add(new MongoId(id));
            else logger.Warning(Tag + "mod_mount allowlist item not found in database, skipped: " + id);
        }
        foreach (var kv in templateTable.Items)
            if (kv.Value != null && !nodes.Contains(kv.Key) && UnderAny(kv.Value)) result.Add(kv.Key);
        foreach (var id in MountExclude) result.Remove(new MongoId(id));
        return result.ToList();
    }

    // Top slot (mod_tactical_000): laser / combo devices. Plain flashlights (the "Flashlight" category: WF-501B, XHP35, 2D)
    // are left out, plus anything listed in config "topSlotExclude".
    private List<MongoId> TopSlotDevices(Config cfg)
    {
        var parents = new HashSet<MongoId>
        {
            new MongoId("55818b164bdc2ddc698b456c"), // TacticalCombo
            new MongoId("55818b0e4bdc2dde698b456e")  // LightLaser
        };
        var exclude = cfg.topSlotExclude.Select(x => new MongoId(x)).ToHashSet();
        var set = new HashSet<MongoId>();
        foreach (var kv in templateTable.Items)
            if (kv.Value != null && parents.Contains(kv.Value.Parent) && !exclude.Contains(kv.Key)) set.Add(kv.Key);
        return set.ToList();
    }

    // The M700 template only has mod_magazine/mod_mount left, so the weapon builder shows no tactical slots.
    // Clone the existing mod_mount slot, rename it and give it its own filter:
    //   mod_tactical_000 (top rail)      -> laser/combo devices
    //   mod_tactical     (bottom point)  -> only the items in config "bottomSlotItems" (M600 mount + BT10 bipod)
    private void AddTacticalSlots(TemplateItem gun, string gunTpl, Config cfg)
    {
        var p = gun.Properties;
        if (p?.Slots == null || cfg.addTacticalSlots.Count == 0) return;
        var list = p.Slots.ToList();
        var template = list.FirstOrDefault(sl => sl.Name == "mod_mount") ?? list.FirstOrDefault();
        if (template == null) return;

        var top = TopSlotDevices(cfg);
        var bottom = cfg.bottomSlotItems.Where(x => Get(x, out _)).Select(x => new MongoId(x)).ToList();
        foreach (var x in cfg.bottomSlotItems) if (!Get(x, out _)) logger.Warning(Tag + "bottom slot item not found in database: " + x);

        foreach (var name in cfg.addTacticalSlots)
        {
            if (list.Any(sl => sl.Name == name)) continue;
            var slot = cloner.Clone(template)!;
            // reflection so this works whether Name has a public, init-only or private setter
            slot.GetType().GetProperty("Name")?.GetSetMethod(true)?.Invoke(slot, new object[] { name });
            slot.Id = new MongoId();
            slot.Parent = new MongoId(gunTpl);
            var useIds = name == "mod_tactical" ? bottom : top;
            Fill(new[] { slot }, useIds, true);
            list.Add(slot);
            logger.Info(Tag + "weapon " + gunTpl + ": added slot " + name + " (" + useIds.Count + " allowed items)");
        }
        p.Slots = list;
    }

    private void AddToTraders(Config cfg)
    {
        foreach (var t in cfg.traders)
        {
            var trader = tradersTable.GetTrader(new MongoId(t.id));
            if (trader?.Assort == null) { logger.Warning(Tag + "trader " + t.name + " (" + t.id + ") has no assort"); continue; }
            if (t.offers.Count == 0) continue;
            foreach (var tpl in cfg.weapons)
                foreach (var o in t.offers)
                    AddOffer(trader, tpl, o.loyaltyLevel, o.priceRoubles);
            var first = t.offers.OrderBy(o => o.loyaltyLevel).First();
            foreach (var tpl in cfg.magazines)
                AddOffer(trader, tpl, first.loyaltyLevel, Math.Max(1000, first.priceRoubles / 15));
            logger.Info(Tag + "added " + cfg.weapons.Count + " weapon(s) x " + t.offers.Count + " offer(s) and " + cfg.magazines.Count + " magazine(s) to " + t.name);
        }
    }

    private static void AddOffer(Trader trader, string tpl, int loyalty, double price)
    {
        var id = new MongoId();
        trader.Assort!.Items.Add(new Item
        {
            Id = id,
            Template = new MongoId(tpl),
            ParentId = "hideout",
            SlotId = "hideout",
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999 }
        });
        trader.Assort.BarterScheme[id] = new List<List<BarterScheme>>
        {
            new() { new BarterScheme { Count = price, Template = new MongoId(Roubles) } }
        };
        trader.Assort.LoyalLevelItems[id] = loyalty;
    }
}

// Diagnostic: runs after everything else has added its items. Reports magazines the client cannot build
// (the client crashes in Magazine..ctor when a magazine template has no usable Cartridges slot).
[Injectable(TypePriority = OnLoadOrder.PostLoad)]
public class L115Diag(ISptLogger<L115Diag> logger, TemplateTable templateTable) : IOnLoad
{
    private const string Tag = "[L115 diag] ";

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var magParent = new MongoId("5448bc234bdc2d3c308b4569");
            int total = 0, bad = 0;
            foreach (var kv in templateTable.Items)
            {
                var it = kv.Value;
                if (it == null || !it.Parent.Equals(magParent)) continue;
                total++;
                var c = it.Properties?.Cartridges?.ToList();
                string? problem = null;
                if (c == null || c.Count == 0) problem = "no Cartridges slot";
                else if (c[0].Properties?.Filters == null || !c[0].Properties!.Filters!.Any()) problem = "cartridge slot has no Filters";
                else if (c[0].Properties!.Filters!.First().Filter == null || !c[0].Properties!.Filters!.First().Filter!.Any()) problem = "empty cartridge filter";
                if (problem != null)
                {
                    bad++;
                    logger.Warning(Tag + "magazine " + kv.Key + " (" + it.Name + "): " + problem);
                }
            }
            logger.Info(Tag + total + " magazines scanned, " + bad + " with problems");

            // dump our magazines and the donor TRG M10 magazine for comparison
            var dump = new Dictionary<string, object?>();
            foreach (var id in new[] { "eec8a5a46424c950d2cc58f7", "3a98207c131501610751e6d1", "5fc23636016cce60e8341b05" })
                dump[id] = templateTable.Items.TryGetValue(new MongoId(id), out var t) ? t : null;
            var dir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
            var path = System.IO.Path.Combine(dir, "l115_mag_dump.json");
            File.WriteAllText(path, JsonSerializer.Serialize(dump, new JsonSerializerOptions { WriteIndented = true }));
            logger.Info(Tag + "wrote " + path);
        }
        catch (Exception e) { logger.Warning(Tag + "diagnostic failed: " + e.Message); }
        return Task.CompletedTask;
    }
}
