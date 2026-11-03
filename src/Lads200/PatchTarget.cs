using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Lads200;

/// <summary>
/// One Harmony patch the mod wants. The feature name is the declaring type + method, so every target for one game method
/// shares its on/off state (Centurion repo's PatchTarget.cs). The game method is looked up by name AND parameter list
/// (inherited methods included), so a game update that renames or re-signatures it switches that target off instead of
/// failing the plugin.
/// </summary>
public sealed class PatchTarget
{
    public PatchTarget(Type type, string method, ParameterInfo[]? parameters = null, MethodInfo? prefix = null, MethodInfo? postfix = null, MethodInfo? transpiler = null)
    {
        Type = type;
        Method = method;
        Parameters = parameters;
        Prefix = prefix;
        Postfix = postfix;
        Transpiler = transpiler;
    }

    public Type Type { get; }
    public string Method { get; }
    public ParameterInfo[]? Parameters { get; }
    public MethodInfo? Prefix { get; }
    public MethodInfo? Postfix { get; }
    public MethodInfo? Transpiler { get; }

    /// <summary>Feature key: "Unit.InstantiateSpawn"-style, derived from the target itself.</summary>
    public string Feature => Type.Name + "." + Method;

    string Name => Type.Name + "." + Method;

    /// <summary>
    /// Patches each target and logs one line per target: "found/patched" or "missing: feature off". Returns the features
    /// switched off, so their code can stand down.
    /// </summary>
    public static HashSet<string> InstallAll(Harmony harmony, IReadOnlyList<PatchTarget> targets, ModLog log)
    {
        var off = new HashSet<string>();
        var installed = new List<(PatchTarget Target, MethodInfo Original)>();
        log.Info($"Harmony: {targets.Count} patch targets");
        foreach (PatchTarget target in targets)
        {
            if (off.Contains(target.Feature)) continue;
            try
            {
                Type[]? types = target.Parameters == null ? null : Array.ConvertAll(target.Parameters, p => p.ParameterType);
                // AccessTools.Method walks base classes too - the 1.0.0 failure came from DeclaredMethod only seeing Unit itself.
                MethodInfo? original = AccessTools.Method(target.Type, target.Method, types);
                if (original == null) throw new MissingMethodException(target.Type.FullName, target.Method);
                installed.Add((target, original));
                harmony.Patch(
                    original,
                    target.Prefix == null ? null : new HarmonyMethod(target.Prefix),
                    target.Postfix == null ? null : new HarmonyMethod(target.Postfix),
                    target.Transpiler == null ? null : new HarmonyMethod(target.Transpiler));
                log.Info($"patch {target.Feature}: found/patched");
            }
            catch (Exception e)
            {
                off.Add(target.Feature);
                // Include the failed target: Harmony may have registered a prefix before a transpiler threw.
                for (int i = installed.Count - 1; i >= 0; i--)
                {
                    var entry = installed[i];
                    if (entry.Target.Feature != target.Feature) continue;
                    foreach (MethodInfo? patch in new[] { entry.Target.Transpiler, entry.Target.Prefix, entry.Target.Postfix })
                    {
                        if (patch != null) harmony.Unpatch(entry.Original, patch);
                    }
                }
                log.Error($"patch {target.Feature}: {target.Name} failed to patch: feature off ({e.GetType().Name}: {e.Message})");
            }
        }

        return off;
    }
}
