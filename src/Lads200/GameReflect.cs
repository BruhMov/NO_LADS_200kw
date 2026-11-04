using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;

namespace Lads200;

/// <summary>
/// Every game type this mod touches, resolved by name at runtime and cached. The plugin compiles WITHOUT a reference to
/// Assembly-CSharp.dll (the Centurion repo pins its build against a game install; this mod deliberately does not, so it
/// builds anywhere and survives game patches that only move namespaces). Names verified against the MRB-100 Centurion's
/// sources and the Blueprinter loader (NOBlueprinter-Releases), both compiled against the real game:
///   global namespace: Encyclopedia, UnitDefinition, VehicleDefinition, Unit, GroundVehicle, Laser, PowerSupply,
///   WeaponInfo, Spawner, VehicleType;  Mirage: NetworkIdentity.
/// </summary>
static class GameTypes
{
    /// <summary>Encyclopedia (global namespace; Blueprinter's EncyclopediaAfterLoad.cs uses it unqualified).</summary>
    public static readonly Type Encyclopedia = Resolve("Encyclopedia");
    /// <summary>UnitDefinition (Blueprinter's EncyclopediaLoader.cs).</summary>
    public static readonly Type UnitDefinition = Resolve("UnitDefinition");
    /// <summary>Mirage's NetworkIdentity (Blueprinter's PrefabHashAssigner.cs).</summary>
    public static readonly Type NetworkIdentity = Resolve("Mirage.NetworkIdentity", "NetworkIdentity");
    /// <summary>The game's Laser weapon component (CenturionAuthor.Cram.cs serializes Laser.power).</summary>
    public static readonly Type Laser = Resolve("Laser");
    /// <summary>The game's PowerSupply component (Laser.vehicularPowerSupply draws from it).</summary>
    public static readonly Type PowerSupply = Resolve("PowerSupply");

    static Type Resolve(params string[] names)
    {
        foreach (string name in names)
        {
            Type t = AccessTools.TypeByName(name);
            if (t != null) return t;
        }
        return null;
    }
}

/// <summary>Reflection access helpers: fields or properties by name (walking base classes), member enumeration, dumps.</summary>
static class GameReflect
{
    public const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    const BindingFlags DeclaredAll = All | BindingFlags.DeclaredOnly;

    /// <summary>
    /// Reads a field or property by name, walking base classes so private-in-base and inherited members are found too
    /// (a plain GetField on the concrete type misses private fields declared on a base class - e.g. a member of Unit
    /// read through a GroundVehicle).
    /// </summary>
    public static object GetMember(Type type, object obj, string name)
    {
        if (type == null || string.IsNullOrEmpty(name)) return null;
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, DeclaredAll);
            if (f != null) { try { return f.GetValue(obj); } catch { return null; } }
        }
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name, DeclaredAll);
            if (p != null && p.CanRead) { try { return p.GetValue(obj, null); } catch { return null; } }
        }
        return null;
    }

    /// <summary>Sets a field or property by name (base-class walk as in <see cref="GetMember"/>). Never throws.</summary>
    public static bool SetMember(Type type, object obj, string name, object value)
    {
        if (type == null || string.IsNullOrEmpty(name)) return false;
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            FieldInfo f = t.GetField(name, DeclaredAll);
            if (f != null && f.FieldType.IsInstanceOfTypeNullable(value))
            {
                try { f.SetValue(obj, value); return true; } catch { return false; }
            }
        }
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            PropertyInfo p = t.GetProperty(name, DeclaredAll);
            if (p != null && p.CanWrite && p.PropertyType.IsInstanceOfTypeNullable(value))
            {
                try { p.SetValue(obj, value, null); return true; } catch { return false; }
            }
        }
        return false;
    }

    /// <summary>True when <paramref name="value"/> can be assigned to this type, treating null as assignable to any reference type.</summary>
    public static bool IsInstanceOfTypeNullable(this Type type, object value)
        => value == null ? !type.IsValueType : type.IsInstanceOfType(value);

    public static MemberInfo FindMember(Type type, object obj, string[] names, out object current)
    {
        current = null;
        if (type == null) return null;
        foreach (string name in names)
        {
            object v = GetMember(type, obj, name);
            if (v != null)
            {
                current = v;
                for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
                {
                    FieldInfo f = t.GetField(name, DeclaredAll);
                    if (f != null) return f;
                    PropertyInfo p = t.GetProperty(name, DeclaredAll);
                    if (p != null) return p;
                }
                return null;
            }
        }
        return null;
    }

    public static void SetThrough(MemberInfo member, object obj, object value)
    {
        switch (member)
        {
            case FieldInfo f: f.SetValue(obj, value); break;
            case PropertyInfo p: p.SetValue(obj, value, null); break;
        }
    }

    /// <summary>
    /// Every instance field and readable property of an object, walking base classes, as (name, value) pairs. Unreadable
    /// members are skipped silently. This is how the mod joins "every catalog list the stock MSV LADS is in" without
    /// hardcoding member names: v2.0.0 scanned fields only and would have missed a property-typed catalog
    /// (PlayableVehicles only proves `Encyclopedia.i.vehicles` is *readable* as a member, not that it is a field).
    /// </summary>
    public static IEnumerable<(string Name, object Value)> InstanceMembers(Type type, object obj)
    {
        for (Type t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(DeclaredAll & ~BindingFlags.Static))
            {
                object v;
                try { v = f.GetValue(obj); } catch { continue; }
                yield return (f.Name, v);
            }
            foreach (PropertyInfo p in t.GetProperties(DeclaredAll & ~BindingFlags.Static))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                object v;
                try { v = p.GetValue(obj, null); } catch { continue; }
                yield return (p.Name, v);
            }
        }
    }

    /// <summary>First string member among <paramref name="names"/> that exists on the type (value returned via out).</summary>
    public static string GetString(Type type, object obj, string[] names, out string foundAs)
    {
        foundAs = null;
        foreach (string name in names)
        {
            object v = GetMember(type, obj, name);
            if (v is string s) { foundAs = name; return s; }
        }
        return null;
    }

    /// <summary>First float member among <paramref name="names"/> that exists (found name and current value via out).</summary>
    public static float FindNumeric(Type type, object obj, string[] names, out string foundAs, out float current)
    {
        foreach (string name in names)
        {
            object v = GetMember(type, obj, name);
            if (v is float f) { foundAs = name; current = f; return f; }
            if (v is int i) { foundAs = name; current = i; return i; }
            if (v is double d) { foundAs = name; current = (float)d; return (float)d; }
        }
        foundAs = null;
        current = float.NaN;
        return float.NaN;
    }

    /// <summary>Compact one-line dump of every serialized-looking instance field, for pinning member names in log reports.</summary>
    public static string DescribeFields(Type type, object obj, int maxEntries = 40)
    {
        if (type == null) return "(type missing)";
        var sb = new StringBuilder();
        int count = 0;
        for (Type t = type; t != null && t != typeof(object) && count < maxEntries; t = t.BaseType)
        {
            foreach (FieldInfo f in t.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (count >= maxEntries) break;
                try
                {
                    object v = f.GetValue(obj);
                    string shown = v is UnityEngine.Object u ? (ReferenceEquals(u, null) ? "null" : u.name)
                        : v == null ? "null"
                        : v is Array a ? a.Length + " items"
                        : v.GetType().IsPrimitive || v is string || v is Enum ? v.ToString()
                        : v.GetType().Name;
                    sb.Append(f.FieldType.Name).Append(' ').Append(f.Name).Append('=').Append(shown).Append("; ");
                    count++;
                }
                catch { /* skip unreadable */ }
            }
        }
        return count == 0 ? "(no instance fields)" : sb.ToString();
    }

    /// <summary>"MSV_LADS" ~ "MSV LADS" ~ "msvlads": case/dash/underscore/space-insensitive comparison.</summary>
    public static bool KeyLike(string a, string b) => string.Equals(Norm(a), Norm(b), StringComparison.OrdinalIgnoreCase);

    public static string Norm(string s)
    {
        if (string.IsNullOrEmpty(s)) return s ?? string.Empty;
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (c != '_' && c != '-' && c != ' ') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Reads an IDictionary without a compile-time reference to its construction type.</summary>
    public static IDictionary AsDict(object o) => o as IDictionary;
}
