using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lads200;

/// <summary>
/// Registers the "MSV LADS 200" into the game exactly the way the Blueprinter loader registers a .nobp unit
/// (NOBlueprinter-Releases, EncyclopediaLoader.cs / EncyclopediaAfterLoad.cs), so the clone is indistinguishable from a
/// stock unit for every consumer: the mission editor, the encyclopedia, AI depots and Mirage networking.
///
///   1. Hook: Harmony postfix on Encyclopedia.AfterLoad() - the load entry point Blueprinter itself uses. Registering
///      inside it lands BEFORE the game builds menus or registers network prefabs. (v1.0.x registered from an Update
///      retry loop instead, after the game had already consumed the catalog: the unit never appeared.)
///   2. Clone the stock MSV LADS VehicleDefinition (Object.Instantiate keeps every serialized field: model refs,
///      vehicleType, icons, spawn offset, sounds) and restamp jsonKey / unitName / value (the price).
///   3. Clone the stock prefab once under an INACTIVE DontDestroyOnLoad holder (no Awake runs, nothing renders in the
///      menu), then repoint every root-component member that referenced the STOCK definition at OUR definition.
///      v2.0.0 looked for a component literally named "Unit" and threw MissingFieldException when the LADS root turned
///      out to be a GroundVehicle (a Unit subclass) - which stood the whole feature down and is why v2.0.0 never showed
///      the unit. Reference-matching needs no type names at all and cannot miss.
///   4. Upgrade every Laser on the cloned prefab - power 80 -> 200 kW, fire damage scaled with the power, beam tinted
///      blue, weapon display name restamped. Non-fatal: a game update that renames the weapon class logs an error and
///      still registers the unit (v2.0.0 aborted the whole registration instead).
///   5. Register the Blueprinter way: Encyclopedia.Lookup[key], encyclopedia.IndexLookup + definition.LookupIndex (the
///      network definition index), every catalog list carrying the stock unit (vehicles etc., fields AND properties),
///      then definition.CacheMass().
///
/// The stock MSV LADS itself is never modified: it keeps its 80 kW laser, its price and its catalog rows.
/// </summary>
static class LadsCatalog
{
    // Config, wired from the plugin.
    internal static ModLog Log;
    internal static string SourceKey = "MSV_LADS";
    internal static string SourceUnitName = "MSV LADS";
    internal static string NewKey = "MSV_LADS_200";
    internal static string NewUnitName = "MSV LADS 200";
    internal static float Cost = 50_000_000f;

    // Definition member candidates. jsonKey/unitName/value/unitPrefab are public and verified (Blueprinter's
    // EncyclopediaLoader reads unit.jsonKey; PlayableVehicles reads definition.unitName/.value/.unitPrefab).
    static readonly string[] KeyNames = { "jsonKey" };
    static readonly string[] NameNames = { "unitName" };
    static readonly string[] CostNames = { "value", "emptyCost", "cost" };
    static readonly string[] PrefabNames = { "unitPrefab", "prefab" };
    static readonly string[] IndexNames = { "LookupIndex" };

    static Object cloneDef;            // our cloned VehicleDefinition (kept referenced; destroyed on rollback)
    static GameObject clonePrefab;     // our cloned unit prefab, parked under an inactive holder
    static GameObject holder;          // inactive DontDestroyOnLoad parent that keeps the prefab alive across scenes
    static bool registered;

    /// <summary>True once the clone is registered (Encyclopedia.Lookup carries our key).</summary>
    public static bool IsRegistered => registered;

    /// <summary>The cloned definition object (typed object: this file never references Assembly-CSharp).</summary>
    public static object Definition => cloneDef;

    // ------------------------------------------------------------------ registration

    /// <summary>
    /// One registration attempt. Returns false while the game has not loaded its units yet so the caller can retry
    /// (fallback timer in the plugin; the primary path is the Encyclopedia.AfterLoad postfix). Only truly fatal states
    /// throw (types missing, source not cloneable, no prefab member): the plugin then stands the feature down with the
    /// reason. Everything else - a name field that would not set, a missing weapon class, a missing IndexLookup - logs
    /// a warning and keeps the registration, because a visible unit beats an invisible one.
    /// </summary>
    /// <param name="encyclopedia">The Encyclopedia instance the hook observed (AfterLoad's __instance); null = resolve via Encyclopedia.i.</param>
    /// <param name="via">Where this attempt came from, for the log ("Encyclopedia.AfterLoad" / "fallback timer").</param>
    public static bool TryRegister(object encyclopedia = null, string via = "retry")
    {
        ModLog log = Log;
        if (registered) return true;
        if (GameTypes.Encyclopedia == null || GameTypes.UnitDefinition == null)
            throw new MissingMemberException("Encyclopedia/UnitDefinition types not found in the loaded game assemblies");

        object enc = encyclopedia ?? EncyclopediaInstance();

        IDictionary lookup = GameReflect.AsDict(GameReflect.GetMember(GameTypes.Encyclopedia, null, "Lookup"));
        if (lookup == null)
        {
            log.StageOnce("lookup-null", $"Encyclopedia.Lookup is not created yet ({via}); retrying");
            return false;
        }
        if (lookup.Count == 0)
        {
            log.StageOnce("lookup-empty", $"Encyclopedia.Lookup is still empty ({via}); retrying");
            return false;
        }

        object source = FindSource(lookup);
        if (source == null) return false; // stock units not in yet (or the key differs): FindSource logged the candidates once

        // --- clone the definition at the source's own concrete type (VehicleDefinition): every serialized field copies.
        Object def = Object.Instantiate((Object)source);
        def.name = NewKey;
        Type defType = def.GetType();
        if (!ReferenceEquals(defType, source.GetType()))
            throw new InvalidOperationException($"definition clone came out as {defType.FullName}, expected {source.GetType().FullName}");

        SetAll(defType, def, KeyNames, NewKey);
        SetAll(defType, def, NameNames, NewUnitName);
        SetAll(defType, def, CostNames, Cost); // `value` is what shops charge (PlayableVehicles: definition.value + ammo)

        // --- clone the prefab under an inactive holder: no Awake/OnEnable runs, nothing renders, survives scene loads.
        GameObject sourcePrefab = GameReflect.FindMember(defType, source, PrefabNames, out object prefabBoxed) != null
            ? prefabBoxed as GameObject
            : null;
        if (sourcePrefab == null)
            throw new MissingFieldException(defType.FullName, "none of [" + string.Join(", ", PrefabNames) + "] (GameObject)");

        holder = new GameObject(NewKey + "_holder");
        holder.SetActive(false);                                  // children instantiate inert (Unity skips Awake)
        Object.DontDestroyOnLoad(holder);
        clonePrefab = Object.Instantiate(sourcePrefab, holder.transform, false);
        clonePrefab.name = NewKey;

        try
        {
            // The spawned unit's identity: every root member that pointed at the STOCK definition now points at ours,
            // so kill feeds, target value, AI and the encyclopedia all see the MSV LADS 200 (v1.0.x never did).
            RepointDefinitionReferences(clonePrefab, source, def, log);

            // Mirage spawns by NetworkIdentity.PrefabHash; the clone must not share the stock prefab's hashes.
            LadsNet.AssignFreshPrefabHashes(clonePrefab, log);

            // The weapon upgrade lives on the prefab: every spawned instance inherits it. Non-fatal on purpose.
            LadsWeapon.UpgradeClonePrefab(clonePrefab, log);

            SetAll(defType, def, PrefabNames, clonePrefab);

            // --- Blueprinter's exact registration (EncyclopediaLoader.AddUnit + Load):
            cloneDef = def; // assigned before registering so a partial failure rolls the catalogs back too
            RegisterWithEncyclopedia(enc, lookup, source, def, defType, log);
            registered = true;
        }
        catch
        {
            Rollback(lookup); // a partial registration must leave no trace
            throw;
        }

        log.Info($"registered '{NewUnitName}' (key {NewKey}) via {via}: cost ${GameReflect.FindNumeric(defType, def, CostNames, out _, out float cost):N0}" +
                 (Math.Abs(cost - Cost) > 0.01f ? $" (configured ${Cost:N0} did not land - report this)" : "") +
                 $", laser {LadsWeapon.PowerKw:F0} kW blue, prefab '{clonePrefab.name}'");
        Diagnose(log, lookup, def, defType);
        return true;
    }

    /// <summary>Blueprinter's EncyclopediaLoader.AddUnit + Load, in its exact order, plus one generalization.</summary>
    static void RegisterWithEncyclopedia(object enc, IDictionary lookup, object source, Object def, Type defType, ModLog log)
    {
        string sourceKey = GameReflect.GetString(source.GetType(), source, KeyNames, out _);

        // 1) static key lookup: Encyclopedia.Lookup[key] = def (Blueprinter: Encyclopedia.Lookup.Add(GetJsonKey(unit), unit)).
        lookup[NewKey] = def;

        // 2) the network definition index FIRST (Blueprinter: definition.LookupIndex = IndexLookup.Count; IndexLookup.Add).
        //    v1.0.x never set it and the game could not spawn the unit. Must also run BEFORE the catalog scan below:
        //    IndexLookup carries the source too, and the scan must not add us there a second time.
        if (enc != null && GameReflect.GetMember(GameTypes.Encyclopedia, enc, "IndexLookup") is IList indexLookup)
        {
            if (!ContainsReference(indexLookup, def))
            {
                if (!GameReflect.SetMember(defType, def, IndexNames[0], indexLookup.Count))
                    log.Warning("UnitDefinition.LookupIndex could not be set: multiplayer spawns of the LADS 200 may pick the wrong unit");
                indexLookup.Add(def);
            }
            log.Info($"network index: LookupIndex={indexLookup.Count - 1} of {indexLookup.Count}");
        }
        else
        {
            log.Warning("Encyclopedia.IndexLookup not found: multiplayer spawns of the LADS 200 may fail (single player still works)");
        }

        // 3) the typed catalog lists the game's menus and the HQ AI read (Blueprinter: VehicleDefinition ->
        //    encyclopedia.vehicles). Generalized: EVERY list member of Encyclopedia - field or property, v2.0.0 only
        //    scanned fields - that carries the stock unit gets the clone, so we join vehicles and anything else the
        //    stock MSV LADS belongs to. IndexLookup is handled above and skipped here.
        int joined = 0;
        var joinedNames = new List<string>();
        var seenLists = new HashSet<object>();
        if (enc != null)
        {
            foreach ((string name, object value) in GameReflect.InstanceMembers(GameTypes.Encyclopedia, enc))
            {
                if (name == "IndexLookup") continue;
                if (!(value is IList list) || !seenLists.Add(list)) continue;
                if (!CarriesSource(list, source, sourceKey)) continue;
                try
                {
                    if (!ContainsReference(list, def)) list.Add(def);
                    joined++;
                    joinedNames.Add(name);
                }
                catch (Exception e)
                {
                    log.Warning($"could not add the clone to Encyclopedia.{name} ({e.GetType().Name}): that menu will not list it");
                }
            }
        }
        if (joined == 0)
            log.Warning("no Encyclopedia catalog list carried the stock MSV LADS (expected 'vehicles'): the unit is keyed but menus may not list it - report this line");
        else
            log.Info($"catalog lists joined ({joined}): {string.Join(", ", joinedNames)}");

        // 4) mass cache (Blueprinter: unit.CacheMass()).
        MethodInfo cacheMass = AccessTools.Method(defType, "CacheMass", Type.EmptyTypes);
        if (cacheMass != null)
        {
            try { cacheMass.Invoke(def, null); log.Info("CacheMass ok"); }
            catch (Exception e) { log.Warning($"CacheMass failed ({e.InnerException?.GetType().Name ?? e.GetType().Name}): mass shows stock/zero"); }
        }
        else log.Warning("UnitDefinition.CacheMass not found: mass shows stock/zero");
    }

    /// <summary>A list carries the source by reference, or by jsonKey (the game may hold a different instance with the same key).</summary>
    static bool CarriesSource(IList list, object source, string sourceKey)
    {
        if (ContainsReference(list, source)) return true;
        if (string.IsNullOrEmpty(sourceKey)) return false;
        foreach (object entry in list)
        {
            if (entry == null || ReferenceEquals(entry, source)) continue;
            if (GameReflect.GetMember(entry.GetType(), entry, KeyNames[0]) is string k && k == sourceKey) return true;
        }
        return false;
    }

    static object EncyclopediaInstance()
        => GameReflect.GetMember(GameTypes.Encyclopedia, null, "i");

    /// <summary>
    /// Repoints every member on the cloned prefab's ROOT components that referenced the stock definition at ours.
    /// Reference-matching instead of name matching: whether the game calls it Unit.definition, Vehicle.definition or
    /// Unit.Type, and whether the root component is a Unit, Vehicle or GroundVehicle, does not matter - whatever held
    /// the stock definition now holds the clone's. Non-fatal: without it the unit still spawns with its 200 kW laser,
    /// it just *identifies* as the stock LADS.
    /// </summary>
    static void RepointDefinitionReferences(GameObject prefab, object source, Object def, ModLog log)
    {
        var repointedAt = new List<string>();
        foreach (Component c in prefab.GetComponents<Component>()) // root object only: child Units keep their identity
        {
            if (c == null) continue;
            Type t = c.GetType();
            foreach ((string name, object value) in GameReflect.InstanceMembers(t, c))
            {
                if (value is Object o && ReferenceEquals(o, source) && GameReflect.SetMember(t, c, name, def))
                    repointedAt.Add($"{t.Name}.{name}");
            }
        }

        if (repointedAt.Count == 0)
            log.Warning("no member on the cloned prefab's root referenced the stock definition: spawned units will identify as the stock 'MSV LADS' (the 200 kW laser still applies) - report this line");
        else
            log.Info($"definition references repointed on the clone's root: {string.Join(", ", repointedAt)}");
    }

    // ------------------------------------------------------------------ rollback / disable

    /// <summary>Takes the unit back out of the catalogs (config off or registration failed): menus forget it.</summary>
    public static void Disable()
    {
        ModLog log = Log;
        try
        {
            Rollback(GameReflect.AsDict(GameReflect.GetMember(GameTypes.Encyclopedia, null, "Lookup")));
        }
        catch (Exception e)
        {
            log?.Warning($"rollback incomplete: {e.GetType().Name}: {e.Message}");
        }

        log?.Warning($"feature off: '{NewUnitName}' will not appear");
    }

    /// <summary>Removes the clone from every catalog it reached, then destroys the clone objects. Never throws.</summary>
    static void Rollback(IDictionary lookup)
    {
        try
        {
            if (cloneDef != null)
            {
                lookup?.Remove(NewKey);
                object enc = EncyclopediaInstance();
                if (enc != null)
                {
                    foreach ((_, object value) in GameReflect.InstanceMembers(GameTypes.Encyclopedia, enc))
                    {
                        if (value is IList list) list.Remove(cloneDef);
                    }
                }
            }
        }
        catch { /* best effort: the game ignores unknown list entries */ }

        if (clonePrefab != null) Object.Destroy(clonePrefab);
        if (holder != null) Object.Destroy(holder);
        if (cloneDef != null) Object.Destroy(cloneDef);
        clonePrefab = null;
        holder = null;
        cloneDef = null;
        registered = false;
    }

    // ------------------------------------------------------------------ source discovery

    /// <summary>
    /// Exact key first, then a tolerant scan (case/underscore/dash-insensitive), then unitName/asset-name matches;
    /// lists candidate keys once if nothing matched (the diagnostic that separates "source key differs" from
    /// "registered but hidden").
    /// </summary>
    static object FindSource(IDictionary lookup)
    {
        if (lookup.Contains(SourceKey) && lookup[SourceKey] is Object byKey) return byKey;

        foreach (DictionaryEntry entry in lookup)
        {
            if (entry.Key is string k && GameReflect.KeyLike(k, SourceKey) && entry.Value != null) return entry.Value;
        }

        foreach (DictionaryEntry entry in lookup)
        {
            object def = entry.Value;
            if (def == null) continue;
            string unitName = GameReflect.GetString(def.GetType(), def, NameNames, out _);
            if (string.Equals(unitName, SourceUnitName, StringComparison.OrdinalIgnoreCase)) return def;
        }

        foreach (DictionaryEntry entry in lookup)
        {
            object def = entry.Value;
            if (def is Object o && o.name != null && o.name.IndexOf("LADS", StringComparison.OrdinalIgnoreCase) >= 0) return def;
        }

        if (!sourceMissingLogged)
        {
            sourceMissingLogged = true;
            var keys = new List<string>();
            foreach (DictionaryEntry entry in lookup)
            {
                if (entry.Key is string ks && ks.IndexOf("LADS", StringComparison.OrdinalIgnoreCase) >= 0) keys.Add(ks);
            }
            Log.Warning($"source '{SourceKey}' not found in Encyclopedia.Lookup ({lookup.Count} entries); keys containing 'LADS': " +
                        (keys.Count == 0 ? "(none)" : string.Join(", ", keys)) +
                        " - if the stock MSV LADS uses another key, set SourceKeyOverride in razor.lads200.cfg");
        }
        return null;
    }

    static bool sourceMissingLogged;

    // ------------------------------------------------------------------ diagnostics

    /// <summary>One-shot post-registration check of the consumers that matter, in one log line each.</summary>
    static void Diagnose(ModLog log, IDictionary lookup, object def, Type defType)
    {
        try
        {
            bool inLookup = lookup.Contains(NewKey);

            var carriers = new List<string>();
            object enc = EncyclopediaInstance();
            if (enc != null)
            {
                foreach ((string name, object value) in GameReflect.InstanceMembers(GameTypes.Encyclopedia, enc))
                {
                    if (value is IList list && ContainsReference(list, def)) carriers.Add(name);
                }
            }

            bool groundVehicle = false;
            if (GameReflect.GetMember(defType, def, PrefabNames[0]) is GameObject go)
            {
                foreach (Component c in go.GetComponentsInChildren<Component>(true))
                {
                    if (c != null && c.GetType().Name == "GroundVehicle") { groundVehicle = true; break; }
                }
            }

            log.Info($"menu-check: Lookup={inLookup}, lists=[{string.Join(", ", carriers)}], GroundVehicle={(groundVehicle ? "yes" : "NO (vehicle shops will skip it)")}, " +
                     $"key='{GameReflect.GetString(defType, def, KeyNames, out _)}', name='{GameReflect.GetString(defType, def, NameNames, out _)}', " +
                     $"cost=${GameReflect.FindNumeric(defType, def, CostNames, out _, out _):N0}");
        }
        catch (Exception e)
        {
            log.Warning($"menu-check skipped: {e.GetType().Name}: {e.Message}");
        }
    }

    // ------------------------------------------------------------------ helpers

    static void SetAll(Type type, object obj, string[] names, object value)
    {
        int set = 0;
        foreach (string name in names)
        {
            if (GameReflect.SetMember(type, obj, name, value)) set++;
        }
        // Best-effort by design: a member this mod could not restamp shows up as the stock value in the menu-check
        // line instead of standing the whole unit down (jsonKey/unitName/value are verified public members anyway).
        if (set == 0)
            Log.Warning($"could not set any of [{string.Join(", ", names)}] on {type.Name}: the clone keeps the stock value there");
    }

    static bool ContainsReference(IList list, object item)
    {
        foreach (object o in list)
        {
            if (ReferenceEquals(o, item)) return true;
        }
        return false;
    }
}
