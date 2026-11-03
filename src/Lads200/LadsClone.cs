using System;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
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
    /// <summary>The source unit's encyclopedia key / display name as shipped by the game.</summary>
    public const string SourceKey = "MSV_LADS";
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
    static readonly string[] CostNames = { "emptyCost", "cost" };
    static readonly string[] NameNames = { "unitName", "displayName", "name" };
    static readonly string[] KeyNames = { "jsonKey" };
    static readonly string[] PowerNames = { "power", "powerKW", "powerKw" };
    static readonly string[] MaxPowerNames = { "maxPower", "maximumPower" };
    static readonly string[] BeamColorNames = { "beamColor", "laserColor", "color" };

    /// <summary>Set by the plugin in Awake; every line this class writes goes through it.</summary>
    internal static ModLog Log;

    static GameObject cloneGo;           // our live clone of the prefab (kept referenced so Unity won't unload it)
    static UnitDefinition cloneDef;      // our cloned definition (destroyed again on rollback)
    static bool registered;

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

            GameObject sourcePrefab = GetGameObjectField(source.GetType(), "prefab", source);
            if (sourcePrefab == null)
            {
                throw new MissingFieldException(source.GetType().FullName, "prefab (GameObject)");
            }

            // Clone the definition by hand: every instance field of the concrete type and each base up to ScriptableObject,
            // including the private backing fields Unity serializes (jsonKey, unitName, emptyCost, prefab, ...).
            UnitDefinition def = UnityEngine.Object.Instantiate(source);
            def.name = NewKey;

            // Restamp identity + price on the cloned definition...
            SetFirstFound(def.GetType(), def, KeyNames, NewKey);
            SetFirstFound(def.GetType(), def, NameNames, NewUnitName);
            SetNumericChain(def, CostNames, Cost, "definition cost");

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

            SetField(def.GetType(), def, "prefab", go);

            // Register: the lookup plus whichever typed list mirrors it (aircraft/ships/vehicles/ground/buildings...).
            lookup.Add(NewKey, def);
            AddToTypedList(source, def);
            cloneGo = go;
            cloneDef = def;
            registered = true;
            log.Info($"registered '{NewUnitName}' (key {NewKey}) as a clone of '{SourceUnitName}': cost ${Cost:N0}, laser {LaserPowerKw:F0} kW blue");
            return true;
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
        if (lookup.Contains(SourceKey) && lookup[SourceKey] is UnitDefinition byKey) return byKey;

        foreach (DictionaryEntry entry in lookup)
        {
            if (entry.Value is UnitDefinition def
                && string.Equals(GetStringField(def.GetType(), "unitName", def), SourceUnitName,
                        StringComparison.OrdinalIgnoreCase))
            {
                return def;
            }
        }

        return null;
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

    static void AddToTypedList(UnitDefinition source, UnitDefinition def)
    {
        foreach (IList list in ListsContaining(source))
        {
            if (!list.Contains(def)) list.Add(def);
        }
    }

    static void RemoveFromTypedLists(UnitDefinition def)
    {
        foreach (IList list in TypedLists())
        {
            list.Remove(def);
        }
    }

    static IEnumerable<IList> ListsContaining(UnitDefinition source)
    {
        foreach (IList list in TypedLists())
        {
            if (list.Contains(source)) yield return list;
        }
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
