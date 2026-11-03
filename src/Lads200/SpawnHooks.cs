using System;
using System.Collections.Generic;
using System.Reflection;

namespace Lads200;

/// <summary>
/// The spawn-hook names pinned from the v1.0.3 diagnostic dump (the game build's Unit declares no method whose name
/// contains "spawn"/"instantiate", so the old substring heuristic found 0 targets and logged the whole method list).
/// Verified against that dump:
///   - Unit.RegisterUnit / RegisterOnGridAsync / InitializeUnit are the methods the game runs over each spawned unit;
///     their parameter types carry the definition (UnitDefinition) or the spawned object itself.
///   - Unit.Awake / OnEnable / OnStartServer fire for every instantiated NetworkBehaviour, our clone included.
///     The v1.0.4 field report showed those three are the ONLY ones this build declares directly on Unit - RegisterUnit,
///     RegisterOnGridAsync and InitializeUnit exist in the dump but do not pass the shape check below (overloaded, or not
///     plain void with a reference parameter), so they are dropped silently. That is fine: Awake/OnEnable/OnStartServer
///     still run once per spawned unit, which is everything AfterUnitSpawn needs.
/// Every entry is validated at runtime before patching: a name must resolve on Unit itself to exactly one non-special,
/// void-returning instance method (spawn-data hooks additionally taking at least one reference-type parameter). Anything
/// else is skipped silently, so a future game update that moves or overloads a hook simply drops it instead of failing
/// the plugin (PatchTarget.cs philosophy).
/// </summary>
static class SpawnHooks
{
    /// <summary>
    /// Names to hook, in preference order. The first three take the spawn data as a parameter (requireRefParam), the rest
    /// are Unity/Mirage lifecycle callbacks with no parameters that still run once per spawned unit.
    /// </summary>
    static readonly (string Name, bool RequireRefParam)[] Hooks =
    {
        ("RegisterUnit",       true),   // seen in the dump; the game's own "a unit exists now" registry call
        ("RegisterOnGridAsync",true),   // seen in the dump; async grid registration of a fresh spawn
        ("InitializeUnit",     true),   // seen in the dump; runs after the prefab + definition are attached
        ("Awake",              false),  // declared on Unit in the dump: fires the moment our clone prefab instantiates
        ("OnEnable",           false),  // declared on Unit in the dump
        ("Start",              false),  // MonoBehaviour start, if this build declares it on Unit
        ("OnStartServer",      false),  // Mirage hook overridden by Unit (present in the dump)
    };

    /// <summary>Fill <paramref name="targets"/> with every hook this game build actually has. Returns how many were kept.</summary>
    internal static int Discover(List<PatchTarget> targets, ModLog log)
    {
        targets.Clear();

        var foundNames = new List<string>();
        var skippedNames = new List<string>();
        foreach ((string name, bool requireRefParam) in Hooks)
        {
            if (Add(targets, typeof(Unit), name, requireRefParam)) foundNames.Add("Unit." + name);
            else skippedNames.Add(name);
        }

        if (targets.Count == 0)
        {
            // Pinned names moved in a game update: dump what Unit really declares so the next release can re-pin them.
            log.Warning("spawn-guard: none of the pinned spawn hooks resolved on Unit. Methods declared on Unit: " + DeclaredMethodsOnUnit());
        }
        else
        {
            log.Info($"spawn-guard: found {targets.Count} spawn hook(s): " + string.Join(", ", foundNames)
                     + $" (skipped, not resolvable as a single void instance method on Unit: {string.Join(", ", skippedNames)})");
        }

        return targets.Count;
    }

    static bool Add(List<PatchTarget> targets, Type type, string name, bool requireRefParam)
    {
        MethodInfo m = Resolve(type, name, requireRefParam);
        if (m == null) return false;
        targets.Add(new PatchTarget(m.DeclaringType ?? type, m.Name, m.GetParameters(),
            postfix: HarmonyLib.AccessTools.Method(typeof(SpawnGuard), nameof(SpawnGuard.AfterUnitSpawn))));
        return true;
    }

    /// <summary>Declared-only lookup (we know which type owns the method) with the sanity checks above.</summary>
    static MethodInfo Resolve(Type type, string name, bool requireRefParam)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        MethodInfo found = null;
        int matches = 0;
        foreach (MethodInfo m in type.GetMethods(flags))
        {
            if (m.Name != name || m.IsSpecialName) continue;
            if (m.ReturnType != typeof(void)) continue;                       // async/coroutine shapes are dead ends for a postfix
            ParameterInfo[] ps = m.GetParameters();
            if (requireRefParam)
            {
                bool anyRef = false;
                foreach (ParameterInfo p in ps)
                {
                    if (!p.ParameterType.IsValueType && p.ParameterType != typeof(string)) { anyRef = true; break; }
                }

                if (!anyRef) continue;                                        // a pure-value/no-arg method cannot be the spawn entry point
            }

            found = m;
            matches++;
        }

        return matches == 1 ? found : null; // ambiguous overloads: skip rather than patch the wrong one
    }

    static string DeclaredMethodsOnUnit()
    {
        var names = new List<string>();
        foreach (MethodInfo m in typeof(Unit).GetMethods(BindingFlags.Public | BindingFlags.NonPublic
                     | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (!m.IsSpecialName) names.Add(m.Name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }
}
