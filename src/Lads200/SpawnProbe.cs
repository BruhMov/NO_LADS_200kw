using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Lads200;

/// <summary>
/// The networked spawn guard, self-healing edition. The first release hardcoded "Unit.InstantiateSpawn" and the game said
/// MissingMethodException - the method is not declared on Unit under that name in this build. Rather than guess again, we
/// enumerate at runtime every candidate method the game uses to instantiate a unit's prefab:
///   - declared or inherited (public/non-public, instance/static) on Unit whose name contains "spawn" or "instantiate",
///   - plus Mirage's NetworkBehaviour.OnStartServer / OnStartNetwork / OnSpawned hooks when Unit implements them.
/// Each candidate gets a postfix that calls SpawnGuard.AfterUnitSpawn(__instance); whichever hook fires first finishes the
/// registration during a spawn request, the others are harmless duplicates (the handler is idempotent). If NO candidate
/// exists at all we log one diagnostic line listing every method declared on Unit, so the correct name can be read off the
/// log and pinned here for the next game build.
/// </summary>
static class SpawnProbe
{
    /// <summary>Fill <paramref name="targets"/> with every spawn-hook candidate found on Unit. Returns how many were kept.</summary>
    internal static int Discover(List<PatchTarget> targets, ModLog log)
    {
        targets.Clear();
        foreach (MethodInfo m in CandidateMethods())
        {
            targets.Add(new PatchTarget(m.DeclaringType ?? typeof(Unit), m.Name, m.GetParameters(),
                postfix: AccessTools.Method(typeof(SpawnGuard), nameof(SpawnGuard.AfterUnitSpawn))));
        }

        if (targets.Count == 0)
        {
            log.Warning("spawn-guard: no spawn/instantiate hook found on Unit. Methods declared on Unit: " + DeclaredMethodsOnUnit());
        }
        else
        {
            var names = new string[targets.Count];
            for (int i = 0; i < targets.Count; i++) names[i] = targets[i].Type.Name + "." + targets[i].Method;
            log.Info($"spawn-guard: found {targets.Count} spawn hook candidate(s) on Unit: " + string.Join(", ", names));
        }

        return targets.Count;
    }

    static IEnumerable<MethodInfo> CandidateMethods()
    {
        var seen = new HashSet<(string, string)>();
        for (Type t = typeof(Unit); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (MethodInfo m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                                                  BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue;                       // skip property getters/setters
                string n = m.Name;
                if (!(n.IndexOf("spawn", StringComparison.OrdinalIgnoreCase) >= 0
                      || n.IndexOf("instantiate", StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                // Pure queries like GetSpawnTransform / CanSpawn are dead ends; only act-on-spawn shapes remain plausible.
                if (n.StartsWith("get_", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Can", StringComparison.Ordinal)) continue;
                if (!IsVoidOrGameObject(m)) continue;
                if (!seen.Add((t.FullName ?? "", n))) continue;
                yield return m;
            }
        }
    }

    static bool IsVoidOrGameObject(MethodInfo m)
        => m.ReturnType == typeof(void) || m.ReturnType == typeof(GameObject);

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
