using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace Lads200;

/// <summary>
/// Turns the stock MSV LADS ground unit into the "MSV LADS 200": a runtime deep clone of its UnitDefinition with
///   - unitName    = "MSV LADS 200"
///   - emptyCost   = $10,000,000 (the field Blueprinter authors set for a mod unit's price, CenturionAuthor.cs)
///   - every Laser on its prefab: power 80 kW -> 200 kW, beam tinted blue, and the carrier PowerSupply scaled so it can feed it.
/// Nothing ships but this DLL: the clone shares the stock MSV LADS model, colliders and sounds (Object.Instantiate of the
/// definition's prefab reference), exactly like Blueprinter mods reuse ripped stock assets, only done live in memory.
/// </summary>
static class LadsClone
{
    /// <summary>The source unit's encyclopedia key / display name as shipped by the game. SourceKey is overridable via
    /// config (SourceKeyOverride) for builds whose stock key differs from our guess; FindSource also matches tolerantly.</summary>
    public static string SourceKey = "MSV_LADS";
    public const string SourceUnitName = "MSV LADS";

    /// <summary>Configurable through BepInEx/config/razor.lads200.cfg (Lads200Plugin).</summary>
    public static string NewKey = "MSV_LADS_200";
    public static string NewUnitName = "MSV LADS 200";
    public static float Cost = 10_000_000f;
    public static float LaserPowerKw = 200f;

    /// <summary>Blue laser beam color (a bright electric blue; alpha is whatever the stock beam material uses).</summary>
    static readonly Color Blue = new Color(0.25f, 0.6f, 1.0f);

    // Field names verified against the Centurion repo: WeaponMount.emptyCost (CenturionAuthor.Cram.cs),
    // Laser.power (same file), Encyclopedia.Lookup / UnitDefinition.jsonKey / .unitName (MissionGeometryLog.cs, StartupHold.cs).
    // "value": the price the PlayableVehicles shop actually charges (VehicleShop.Price() returns definition.value + ammo value;
    // a cloned stock LADS would otherwise keep the cheap original price there even after emptyCost was restamped).
    static readonly string[] CostNames = { "emptyCost", "cost", "value" };
    static readonly string[] NameNames = { "unitName", "displayName", "name" };
    static readonly string[] KeyNames = { "jsonKey" };
    static readonly string[] PowerNames = { "power", "powerKW", "powerKw" };
    static readonly string[] MaxPowerNames = { "maxPower", "maximumPower" };
    static readonly string[] BeamColorNames = { "beamColor", "laserColor", "color" };
    static readonly string[] PrefabNames = { "unitPrefab", "prefab" };

    /// <summary>Set by the plugin in Awake; every line this class writes goes through it.</summary>
    internal static ModLog Log;

    static GameObject cloneGo;           // our live clone of the prefab (kept referenced so Unity won't unload it)
    static UnitDefinition cloneDef;      // our cloned definition (destroyed again on rollback)
    static bool registered;

    /// <summary>
    /// v1.0.6: a mod unit is only ever *cloned* from a stock UnitDefinition - nothing in this game spawns one directly.
    /// The encyclopedia lists are typed (List&lt;VehicleDefinition&gt;, List&lt;AircraftDefinition&gt;, ...) and Object.Instantiate
    /// of a ScriptableObject asset makes a plain UnitDefinition, which an IList.Add would reject with ArgumentException -
    /// that exception used to hit Tick's catch and stand the whole feature down before anything was logged. So we walk the
    /// source's class chain until Instantiate returns something assignable to the list element type (Unity clones at the
    /// concrete type where the serialized data actually lives), and fall back to MemberwiseClone for exotic hierarchies.
    /// </summary>
    static UnitDefinition CloneDefinition(UnitDefinition source)
    {
        for (Type t = source.GetType(); t != null && typeof(UnitDefinition).IsAssignableFrom(t) && t != typeof(object); t = t.BaseType)
        {
            if (t.IsAbstract || t.IsInterface) continue;
            try
            {
                UnityEngine.Object proto = UnityEngine.Object.Instantiate(source, null, false);
                proto.name = NewKey;
                if (proto is UnitDefinition ud && t.IsInstanceOfType(ud)) return ud;
                UnityEngine.Object.Destroy(proto); // wrong runtime type: retry against the next class in the chain
            }
            catch { /* keep walking */ }
        }

        try
        {
            object shallow = source.MemberwiseClone(); // not a Unity "Object" clone: fine as an in-memory catalog row
            ((UnitDefinition)shallow).name = NewKey;
            return (UnitDefinition)shallow;
        }
        catch
        {
            UnitDefinition last = UnityEngine.Object.Instantiate(source); // let the caller's catch report it
            last.name = NewKey;
            return last;
        }
    }

    /// <summary>True once the clone is in Encyclopedia.Lookup under <see cref="NewKey"/>.</summary>
    public static bool IsRegistered => registered;

    /// <summary>
    /// One attempt: find the stock MSV LADS, clone it, restamp it, add it to the encyclopedia lists. Returns false while the
    /// game has not loaded its definitions yet (Encyclopedia.Lookup is null until the main menu finishes loading, see
    /// StartupHold.cs in the Centurion repo) so the caller retries. Throws if a required member moved in a game update;
    /// the plugin then calls <see cref="Disable"/> and stands the feature down.
    /// </summary>
    public static bool TryRegister()
    {
        ModLog log = Log;
        if (registered) return true;

        var lookup = GetStaticProperty<IDictionary>("Encyclopedia", "Lookup");
        if (lookup == null) return false; // encyclopedia not loaded yet: retry next frame

        lock (lookup)
        {
            if (registered) return true;

            UnitDefinition source = FindSource(lookup);
            if (source == null) return false; // stock units not in yet: retry

            // v1.0.5: this build names the definition's spawned prefab `unitPrefab` (verified against the PlayableVehicles
            // repo: VehicleShop.TryGet -> definition.unitPrefab.GetComponent<GroundVehicle>()); keep trying `prefab`.
            GameObject sourcePrefab = GetGameObjectFieldAny(source.GetType(), source, PrefabNames);
            if (sourcePrefab == null)
            {
                throw new MissingFieldException(source.GetType().FullName, $"none of [{string.Join(", ", PrefabNames)}] (GameObject)");
            }

            // Clone the definition by hand: every instance field of the concrete type and each base up to ScriptableObject,
            // including the private backing fields Unity serializes (jsonKey, unitName, emptyCost, prefab, ...).
            // v1.0.6: typed clone (see CloneDefinition) so it can join the game's List<VehicleDefinition> catalog.
            UnitDefinition def = CloneDefinition(source);

            // Restamp identity + price on the cloned definition...
            SetFirstFound(def.GetType(), def, KeyNames, NewKey);
            SetFirstFound(def.GetType(), def, NameNames, NewUnitName);
            // v1.0.5: set every cost member that exists (emptyCost AND value): the encyclopedia shows one, the
            // PlayableVehicles shop charges the other (VehicleShop.Price = definition.value + ammo). First-found was wrong.
            SetNumericsAll(def, CostNames, Cost, "definition cost");

            // ...then clone the prefab itself so the original MSV LADS keeps its 80 kW laser, and upgrade the clone's lasers.
            GameObject go = UnityEngine.Object.Instantiate(sourcePrefab);
            go.name = sourcePrefab.name + "_200";
            try
            {
                UpgradeLasers(go);
                EnsurePowerSupply(go);
            }
            catch
            {
                UnityEngine.Object.Destroy(go);
                UnityEngine.Object.Destroy(def);
                throw;
            }

            SetFieldAny(def.GetType(), def, PrefabNames, go);

            // Register: the lookup plus whichever typed list mirrors it (aircraft/ships/vehicles/ground/buildings...).
            lookup.Add(NewKey, def);
            AddToTypedList(source, def);
            cloneGo = go;
            cloneDef = def;
            registered = true;
            log.Info($"registered '{NewUnitName}' (key {NewKey}) as a clone of '{SourceUnitName}': cost ${Cost:N0}, laser {LaserPowerKw:F0} kW blue");
            DiagnoseMenus(log, source, def);
            return true;
        }
    }

    /// <summary>
    /// One-shot post-registration check against the two consumers users actually look at: the encyclopedia lookup and the
    /// PlayableVehicles ground-vehicle shop (github.com/BruhMov/NO-PlayableVec-personal-mods). That shop builds its catalog
    /// from Encyclopedia.i.vehicles and silently drops any definition whose unitPrefab lacks a GroundVehicle component
    /// (VehicleShop.TryGet), so these lines say exactly which of the three failure modes applies if the unit still doesn't show.
    /// </summary>
    static void DiagnoseMenus(ModLog log, UnitDefinition source, UnitDefinition def)
    {
        try
        {
            // 1) Which prefab field does this build use, and does our cloned prefab carry a GroundVehicle?
            GameObject srcPfb = GetGameObjectFieldAny(source.GetType(), source, PrefabNames);
            GameObject ourPfb = GetGameObjectFieldAny(def.GetType(), def, PrefabNames);
            bool hasGroundVehicle = false;
            string pfbInfo;
            if (ourPfb == null)
            {
                pfbInfo = "clone prefab field 'prefab'/'unitPrefab' is EMPTY";
            }
            else
            {
                foreach (Component c in ourPfb.GetComponentsInChildren<Component>(true))
                {
                    if (c != null && c.GetType().Name == "GroundVehicle") { hasGroundVehicle = true; break; }
                }

                pfbInfo = $"clone prefab '{ourPfb.name}' GroundVehicle={(hasGroundVehicle ? "yes" : "NO -> PlayableVehicles' shop will skip it (TryGet filter)")}";
            }

            log.Info($"menu-check: {pfbInfo}; source prefab '{srcPfb?.name ?? "(none)"}'");

            // 2) Is the clone actually in the vehicle catalog the shop reads (Encyclopedia.i.vehicles)?
            int inCatalogCount = 0, srcInCatalogCount = 0;
            foreach (IList list in AllEncyclopediaLists())
            {
                bool ours = false, srcs = false;
                foreach (object item in list)
                {
                    if (ReferenceEquals(item, def)) ours = true;
                    else if (ReferenceEquals(item, source)) srcs = true;
                }

                if (ours) inCatalogCount++;
                if (srcs) srcInCatalogCount++;
            }

            log.Info($"menu-check: clone present in {inCatalogCount} Encyclopedia list(s) (stock MSV LADS is in {srcInCatalogCount}); " +
                     "if the stock LADS count is >0 but this one is 0, the definition lists are not IList-visible");

            // 3) Did the game's own category metadata (vehicleTypes etc.) get a row for us?
            if (!string.IsNullOrEmpty(GetStringValueOfAny(def, KeyNames2)))
            {
                log.Info("menu-check: encyclopedia key set to '" + NewKey + "'");
            }
        }
        catch (Exception e)
        {
            log.Warning($"menu-check skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>Takes the unit back out again (config off, or a failed registration): the spawn menu forgets it.</summary>
    public static void Disable()
    {
        ModLog log = Log;
        try
        {
            var lookup = GetStaticProperty<IDictionary>("Encyclopedia", "Lookup");
            if (lookup != null && cloneDef != null)
            {
                lookup.Remove(NewKey);
                RemoveFromTypedLists(cloneDef);
            }

            if (cloneGo != null) UnityEngine.Object.Destroy(cloneGo);
            if (cloneDef != null) UnityEngine.Object.Destroy(cloneDef);
        }
        catch (Exception e)
        {
            log.Warning($"rollback incomplete: {e.Message}");
        }

        cloneGo = null;
        cloneDef = null;
        registered = false;
        log.ResetStage();
        log.Warning($"feature off: '{NewUnitName}' will not appear");
    }

    static UnitDefinition FindSource(IDictionary lookup)
    {
        // v1.0.5: the exact key first, then a tolerant scan (case-insensitive, ignoring '_'/'-'). The stock key in this
        // game build's encyclopedia json may be "MSV LADS" / "Msv_Lads" etc.; failing to find the source used to mean the
        // feature silently gave up after 120 s with only a "feature off" line, which reads like "the unit never appears".
        if (lookup.Contains(SourceKey) && lookup[SourceKey] is UnitDefinition byKey) return byKey;

        foreach (DictionaryEntry entry in lookup)
        {
            if (entry.Key is string k && KeyLike(k, SourceKey) && entry.Value is UnitDefinition defByKey) return defByKey;
        }

        foreach (DictionaryEntry entry in lookup)
        {
            if (entry.Value is UnitDefinition def
                && string.Equals(GetStringField(def.GetType(), "unitName", def), SourceUnitName,
                        StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        // Nothing matched: list the candidate keys once per session so the config can be corrected instead of guessing -
        // this is the diagnostic that distinguishes "mod never registered" from "registered but invisible in menus".
        if (!sourceMissingLogged)
        {
            sourceMissingLogged = true;
            var keys = new List<string>();
            foreach (DictionaryEntry entry in lookup)
            {
                if (entry.Key is string ks && ks.IndexOf("LADS", StringComparison.OrdinalIgnoreCase) >= 0) keys.Add(ks);
            }

            Log.Warning($"source '{SourceKey}' not found in Encyclopedia.Lookup ({lookup.Count} entries); keys containing 'LADS': " +
                        (keys.Count == 0 ? "(none)" : string.Join(", ", keys)) + " — if the stock MSV LADS uses another key, set it via SourceKeyOverride in razor.lads200.cfg");
        }

        return null;
    }

    static bool sourceMissingLogged; // one listing per session, not every retry tick

    /// <summary>"MSV_LADS" ~ "MSV LADS" ~ "msvlads": case/dash/underscore-insensitive key comparison.</summary>
    static bool KeyLike(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (c != '_' && c != '-' && c != ' ') sb.Append(c);
        return sb.ToString();
    }

    // ---------------------------------------------------------------- lasers

    static void UpgradeLasers(GameObject prefab)
    {
        ModLog log = Log;
        int upgraded = 0;
        foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
        {
            if (c == null) continue;
            Type t = c.GetType();
            if (t.Name != "Laser") continue;

            SetNumericChain(c, PowerNames, LaserPowerKw, $"Laser ({c.name})");
            TintBeam(c);
            upgraded++;
        }

        if (upgraded == 0)
        {
            throw new InvalidOperationException($"clone prefab has no component named 'Laser' - the MSV LADS moved its weapon class");
        }

        log.StageOnce("lasers", $"{upgraded} laser(s) on the MSV LADS 200 prefab set to {LaserPowerKw:F0} kW, beam tinted blue");
    }

    /// <summary>
    /// The beam's look lives either in a serialized Color field on the Laser (beamColor/laserColor/color) or in the beam
    /// renderer's material color; recolor whichever exists, skip silently if neither does (the transpiler-style "best effort"
    /// the Centurion repo recommends for members whose exact shape can move between game updates).
    /// </summary>
    static void TintBeam(Component laser)
    {
        ModLog log = Log;
        if (!TrySetColor(laser, BeamColorNames, out string fieldName))
        {
            // Fall back to the visual: any child renderer whose material exposes _BaseColor/_Color gets the blue.
            int tinted = 0;
            foreach (Renderer r in laser.GetComponentsInChildren<Renderer>(true))
            {
                foreach (Material m in r.materials)
                {
                    if (m.HasProperty("_BaseColor")) { m.SetColor("_BaseColor", Blue); tinted++; }
                    else if (m.HasProperty("_Color")) { m.SetColor("_Color", Blue); tinted++; }
                }
            }

            if (tinted > 0) log.StageOnce("beam-material", $"blue applied through {tinted} beam material color(s)");
            else log.Warning("no beamColor field or material property found: laser keeps the stock beam color (still 200 kW)");
        }
        else
        {
            log.StageOnce("beam-field", $"blue applied through Laser.{fieldName}");
        }
    }

    // ---------------------------------------------------------------- power

    /// <summary>
    /// A 200 kW laser needs a 200 kW capacitor: raise every PowerSupply.maxPower (or same-named field) on the clone that is
    /// below it. If the stock MSV LADS feeds its 80 kW laser some other way (e.g. per-part generators), the warning tells you
    /// which knob to add here after decompiling Assembly-CSharp.dll with ilspycmd.
    /// </summary>
    static void EnsurePowerSupply(GameObject prefab)
    {
        ModLog log = Log;
        int raised = 0;
        foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
        {
            if (c == null || c.GetType().Name != "PowerSupply") continue;
            float max = GetNumeric(c, MaxPowerNames, out MemberInfo m);
            if (m != null && max < LaserPowerKw)
            {
                SetNumeric(c, m, LaserPowerKw);
                raised++;
            }
        }

        if (raised > 0) log.StageOnce("power", $"{raised} PowerSupply(ies) raised to {LaserPowerKw:F0} kW");
        else log.StageOnce("power-none", "no PowerSupply.maxPower below the laser draw found; assuming the stock supply already covers it");
    }

    // ---------------------------------------------------------------- encyclopedia typed lists
    // Encyclopedia.i exposes List<UnitDefinition>-typed fields (aircraft etc., MissionGeometryLog.cs uses .aircraft);
    // the clone must join every list its source joins, or the spawn menu and the faction HQ never see it.
    //
    // Two extra kinds of list matter for in-game menus and were the reason a registered clone stayed invisible:
    //   - "catalog" lists (Encyclopedia.vehicles / .groundVehicles): PlayableVehicles' vehicle shop builds its whole
    //     catalog from Encyclopedia.i.vehicles (VehicleShop.Catalog) and silently drops entries whose unitPrefab has no
    //     GroundVehicle component (TryGet). The stock MSV LADS is in that list, so joining every list our source is in
    //     covers it - as long as those fields are IList-compatible, which they are (List<T>).
    //   - metadata lists (Encyclopedia.vehicleTypes: List<UnitType>, i.e. {jsonKey?, typeName} category rows; the shop's
    //     tab bar enumerates them by index against VehicleDefinition.vehicleType, e.g. AAA). These hold UnitType objects,
    //     not UnitDefinitions, so reflection on IList-of-definitions misses them entirely. If the game resolves a unit's
    //     encyclopedia row by jsonKey, the clone needs its own row or it renders with an empty name/category. Best-effort
    //     below: add a cloned UnitType row wherever the source has one.

    static void AddToTypedList(UnitDefinition source, UnitDefinition def)
    {
        ModLog log = Log;
        int joined = 0;
        // v1.0.6: join by *key* as well as by reference. The shop catalog (Encyclopedia.i.vehicles) and the lookup can be
        // filled from different sources in some builds, so "the source is in this list" alone was not enough to guarantee
        // we land next to it. Any definition-typed list whose entries carry our source's jsonKey gets our clone too.
        string sourceKey = GetStringField(source.GetType(), "jsonKey", source);
        foreach (IList list in ListsForSource(source, sourceKey))
        {
            if (!list.Contains(def))
            {
                try
                {
                    list.Add(def);
                    joined++;
                }
                catch (Exception e)
                {
                    // Typed List<VehicleDefinition> rejecting a plain UnitDefinition must never kill the feature: skip the
                    // list, say so once, keep the rest of the registration.
                    log.StageOnce($"add-reject-{ShortListName(list)}", $"could not add the clone to {ShortListName(list)} ({e.GetType().Name}); that menu will not show it");
                }
            }
            else joined++;
        }

        log.StageOnce("lists-joined", $"clone joined {joined} Encyclopedia list(s)");
        AddMetadataRows(source, def);
    }

    static void RemoveFromTypedLists(UnitDefinition def)
    {
        foreach (IList list in TypedLists())
        {
            list.Remove(def);
        }

        RemoveMetadataRows(def);
    }

    /// <summary>
    /// For every list on Encyclopedia that contains the source definition *or* a metadata row keyed by the source key
    /// (vehicleTypes etc.), make sure a row keyed by NewKey exists too. Never throws: anything unexpected is skipped with
    /// a StageOnce line so the primary registration still stands.
    /// </summary>
    static void AddMetadataRows(UnitDefinition source, UnitDefinition def)
    {
        ModLog log = Log;
        string sourceKey = GetStringField(source.GetType(), "jsonKey", source);
        foreach (IList list in AllEncyclopediaLists())
        {
            try
            {
                object template = null; // the source's own row in this list, if any
                foreach (object item in list)
                {
                    if (item == null) continue;
                    if (ReferenceEquals(item, source)) continue; // definition list, handled above
                    if (RowMatches(item, sourceKey)) { template = item; break; }
                }

                if (template == null) continue;

                bool exists = false;
                foreach (object item in list)
                {
                    if (item != null && RowMatches(item, NewKey)) { exists = true; break; }
                }

                if (exists) continue;

                object row = CloneRow(template);
                if (row == null)
                {
                    log.StageOnce("meta-row-clone", $"could not clone {template.GetType().Name} row for '{NewKey}' (menu may show it under the stock category name)");
                    continue;
                }

                SetRowKey(row, NewKey);
                list.Add(row);
                log.StageOnce($"meta-row-{ShortListName(list)}", $"added '{NewKey}' row to {ShortListName(list)} ({template.GetType().Name})");
            }
            catch (Exception e)
            {
                log.StageOnce("meta-row-error", $"metadata row copy skipped: {e.GetType().Name}: {e.Message}");
            }
        }
    }

    static void RemoveMetadataRows(UnitDefinition def)
    {
        foreach (IList list in AllEncyclopediaLists())
        {
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    if (RowMatches(list[i], NewKey)) list.RemoveAt(i);
                }
            }
            catch { /* rollback best-effort */ }
        }
    }

    /// <summary>Every List-typed field on the Encyclopedia singleton (definition lists and UnitType category lists alike).</summary>
    static IEnumerable<IList> AllEncyclopediaLists()
    {
        Type encType = AccessTools.TypeByName("Encyclopedia");
        object enc = AccessTools.PropertyGetter(encType, "i")?.Invoke(null, null) ?? AccessTools.Field(encType, "i")?.GetValue(null);
        if (enc == null) yield break;

        foreach (FieldInfo f in enc.GetType().GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
        {
            if (typeof(IList).IsAssignableFrom(f.FieldType) && f.GetValue(enc) is IList list)
            {
                yield return list;
            }
        }
    }

    static string ShortListName(IList list) => list.GetType().Name;

    /// <summary>True if <paramref name="row"/> is a metadata row whose key field equals <paramref name="key"/>.</summary>
    static bool RowMatches(object row, string key)
    {
        if (row == null || string.IsNullOrEmpty(key)) return false;
        // Definition lists are handled by reference, not by key. Checked by type NAME because UnitDefinition lives in the
        // game's global namespace and this file deliberately has no `using` for it (same reason SpawnGuard reflects instead
        // of referencing Mirage types).
        if (row.GetType().Name == "UnitDefinition") return false;
        string k = GetStringValueOfAny(row, KeyNames2);
        return string.Equals(k, key, StringComparison.Ordinal);
    }

    static readonly string[] KeyNames2 = { "jsonKey", "key", "unitKey", "name" };

    static string GetStringValueOfAny(object target, string[] candidates)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        for (Type t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (string name in candidates)
            {
                FieldInfo f = t.GetField(name, flags | BindingFlags.DeclaredOnly);
                if (f != null && f.FieldType == typeof(string)) return f.GetValue(target) as string;
                PropertyInfo p = t.GetProperty(name, flags | BindingFlags.DeclaredOnly);
                if (p != null && p.PropertyType == typeof(string) && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    try { return p.GetValue(target, null) as string; } catch { }
                }
            }
        }

        return null;
    }

    /// <summary>Shallow memberwise copy of a plain metadata row (struct or class), via Activator + field walk.</summary>
    static object CloneRow(object template)
    {
        Type rt = template.GetType();
        if (rt.IsValueType) return template; // boxed struct copy: mutating the box mutates nothing else
        try
        {
            object copy = Activator.CreateInstance(rt, nonPublic: true);
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            foreach (FieldInfo f in rt.GetFields(flags))
            {
                if (f.IsInitOnly) continue;
                f.SetValue(copy, f.GetValue(template));
            }

            return copy;
        }
        catch
        {
            return null;
        }
    }

    static void SetRowKey(object row, string key)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        for (Type t = row.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (string name in KeyNames2)
            {
                FieldInfo f = t.GetField(name, flags);
                if (f != null && f.FieldType == typeof(string)) { f.SetValue(row, key); return; }
            }
        }
    }

    static IEnumerable<IList> ListsContaining(UnitDefinition source)
    {
        foreach (IList list in TypedLists())
        {
            if (list.Contains(source)) yield return list;
        }
    }

    /// <summary>
    /// v1.0.6: every definition-typed Encyclopedia list that is a "home" for the source: it either contains the source by
    /// reference, or its entries carry the source's jsonKey (covers builds where the catalog and the lookup are filled from
    /// different passes). Non-definition lists (UnitType rows etc.) are handled by AddMetadataRows instead.
    /// </summary>
    static IEnumerable<IList> ListsForSource(UnitDefinition source, string sourceKey)
    {
        var seen = new HashSet<IList>(ReferenceComparer.Instance);
        foreach (IList list in AllEncyclopediaLists())
        {
            if (!IsDefinitionList(list) || !seen.Add(list)) continue;
            bool home = false;
            foreach (object item in list)
            {
                if (item == null) continue;
                if (ReferenceEquals(item, source)) { home = true; break; }
                if (!string.IsNullOrEmpty(sourceKey) && item.GetType().Name.EndsWith("Definition", StringComparison.Ordinal)
                    && string.Equals(GetStringField(item.GetType(), "jsonKey", item), sourceKey, StringComparison.Ordinal))
                {
                    home = true;
                    break;
                }
            }

            if (home) yield return list;
        }
    }

    /// <summary>A list whose element type derives from UnitDefinition (checked by name: no compile-time game types here).</summary>
    static bool IsDefinitionList(IList list)
    {
        Type itemType = null;
        foreach (Type g in list.GetType().GetInterfaces())
        {
            if (g.IsGenericType && g.GetGenericTypeDefinition() == typeof(IList<>)) { itemType = g.GetGenericArguments()[0]; break; }
        }

        if (itemType == null) itemType = list.GetType(); // non-generic lists: fall back to element inspection below
        return itemType.Name.EndsWith("Definition", StringComparison.Ordinal) || itemType == typeof(UnitDefinition);
    }

    sealed class ReferenceComparer : IEqualityComparer<IList>
    {
        public static readonly ReferenceComparer Instance = new ReferenceComparer();
        public bool Equals(IList a, IList b) => ReferenceEquals(a, b);
        public int GetHashCode(IList l) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(l);
    }

    static IEnumerable<IList> TypedLists()
    {
        Type encType = AccessTools.TypeByName("Encyclopedia");
        object enc = AccessTools.PropertyGetter(encType, "i")?.Invoke(null, null) ?? AccessTools.Field(encType, "i")?.GetValue(null);
        if (enc == null) yield break;

        foreach (FieldInfo f in enc.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (typeof(IList).IsAssignableFrom(f.FieldType)
                && f.GetValue(enc) is IList list)
            {
                yield return list;
            }
        }
    }

    // ---------------------------------------------------------------- reflection helpers

    static T GetStaticProperty<T>(string typeName, string propName)
    {
        Type t = AccessTools.TypeByName(typeName);
        if (t == null) return default;
        PropertyInfo p = AccessTools.Property(t, propName);
        if (p?.GetMethod == null || !p.GetMethod.IsStatic) return default;
        return (T)p.GetValue(null);
    }

    static GameObject GetGameObjectField(Type type, string fieldName, object target)
        => GetFieldInternal<GameObject>(type, fieldName, target, required: false);

    static GameObject GetGameObjectFieldAny(Type type, object target, string[] fieldNames)
    {
        foreach (string name in fieldNames)
        {
            GameObject go = GetGameObjectField(type, name, target);
            if (go != null) return go;
        }

        return null;
    }

    /// <summary>Sets the first field in <paramref name="fieldNames"/> that exists and accepts the value.</summary>
    static void SetFieldAny(Type type, object target, string[] fieldNames, object value)
    {
        foreach (string name in fieldNames)
        {
            try { SetField(type, target, name, value); return; }
            catch (MissingFieldException) { continue; }
        }

        throw new MissingFieldException(type.FullName, $"none of [{string.Join(", ", fieldNames)}] (settable to {value.GetType().Name})");
    }

    static string GetStringField(Type type, string fieldName, object target)
        => GetFieldInternal<string>(type, fieldName, target, required: false);

    static T GetFieldInternal<T>(Type type, string fieldName, object target, bool required)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null && typeof(T).IsAssignableFrom(f.FieldType)) return (T)f.GetValue(target);
        }

        if (required) throw new MissingFieldException(type.FullName, fieldName);
        return default;
    }

    static void SetField(Type type, object target, string fieldName, object value)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(fieldName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null && f.FieldType.IsInstanceOfType(value))
            {
                f.SetValue(target, value);
                return;
            }
        }

        throw new MissingFieldException(type.FullName, fieldName + " (settable to " + value.GetType().Name + ")");
    }

    /// <summary>Sets the first field/property in <paramref name="candidates"/> that exists on the object and takes a float.</summary>
    static void SetNumericChain(object target, string[] candidates, float value, string what)
    {
        if (SetNumeric(target, candidates, value)) return;
        throw new MissingFieldException(target.GetType().FullName, $"none of [{string.Join(", ", candidates)}] for {what}");
    }

    /// <summary>Sets *every* candidate member that exists and takes a float (throws only if none do).</summary>
    static void SetNumericsAll(object target, string[] candidates, float value, string what)
    {
        int set = 0;
        foreach (string name in candidates)
        {
            if (FindMember(target.GetType(), name) is { } m && SetValue(m, target, value)) set++;
        }

        if (set == 0) throw new MissingFieldException(target.GetType().FullName, $"none of [{string.Join(", ", candidates)}] for {what}");
    }

    static bool SetNumeric(object target, string[] candidates, float value)
    {
        foreach (string name in candidates)
        {
            if (FindMember(target.GetType(), name) is { } m && SetValue(m, target, value)) return true;
        }

        return false;
    }

    static float GetNumeric(object target, string[] candidates, out MemberInfo found)
    {
        foreach (string name in candidates)
        {
            MemberInfo m = FindMember(target.GetType(), name);
            if (m == null) continue;
            object v = GetValue(m, target);
            if (v is float f) { found = m; return f; }
            if (v is double d) { found = m; return (float)d; }
            if (v is int i) { found = m; return i; }
        }

        found = null;
        return 0f;
    }

    static void SetNumeric(object target, MemberInfo m, float value) => SetValue(m, target, value);

    static bool TrySetColor(object target, string[] candidates, out string used)
    {
        foreach (string name in candidates)
        {
            MemberInfo m = FindMember(target.GetType(), name);
            if (m == null) continue;
            object current = GetValue(m, target);
            if (current is Color) { SetValueRaw(m, target, Blue); used = name; return true; }
        }

        used = null;
        return false;
    }

    static MemberInfo FindMember(Type type, string name)
    {
        for (Type t = type; t != null; t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (f != null) return f;
            PropertyInfo p = t.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            if (p != null && p.CanWrite) return p;
        }

        return null;
    }

    static object GetValue(MemberInfo m, object target) => m is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)m).GetValue(target);

    static bool SetValue(MemberInfo m, object target, float value)
    {
        Type vt = m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
        object converted;
        if (vt == typeof(float)) converted = value;
        else if (vt == typeof(double)) converted = (double)value;
        else if (vt == typeof(int)) converted = (int)value;
        else return false;
        SetValueRaw(m, target, converted);
        return true;
    }

    static void SetValueRaw(MemberInfo m, object target, object value)
    {
        if (m is FieldInfo f) f.SetValue(target, value);
        else ((PropertyInfo)m).SetValue(target, value);
    }

    static void SetFirstFound(Type type, object target, string[] candidates, string value)
    {
        foreach (string name in candidates)
        {
            MemberInfo m = FindMember(type, name);
            if (m != null && (m is FieldInfo ff ? ff.FieldType == typeof(string) : ((PropertyInfo)m).PropertyType == typeof(string)))
            {
                SetValueRaw(m, target, value);
                return;
            }
        }

        throw new MissingFieldException(type.FullName, $"none of [{string.Join(", ", candidates)}] (string)");
    }
}
