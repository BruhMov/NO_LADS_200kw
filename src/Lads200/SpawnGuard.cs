using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Lads200;

/// <summary>
/// The one Harmony patch of this mod: the networked spawn guard. Nuclear Option is Mirage-networked and every automatic
/// start (host, client, dedicated server) waits on the encyclopedia being complete before missions run (the Centurion repo's
/// StartupHold.cs documents the failure: "Unit Type '&lt;key&gt;' not found in Encyclopedia"). Our clone registers itself
/// into Encyclopedia.Lookup from the plugin's Update loop once the game has loaded its own units, so instead of holding the
/// menu we hold the *spawn* of our key only until registration finished - a missing entry can then never be observed by the
/// game, exactly like Blueprinter guarantees for its .nobp assets but without shipping an asset bundle.
/// </summary>
static class SpawnGuard
{
    const float RetryIntervalS = 0.5f;   // definitions land over several seconds after the menu loads (StartupHold.cs)
    const float GiveUpS = 120f;          // past this, stop retrying and stand the feature down (Centurion's MaxHoldS pattern)

    /// <summary>
    /// Populated at plugin Awake by <see cref="SpawnProbe.Discover"/>: one entry per spawn/instantiate hook the game build
    /// actually declares on Unit (the first release hardcoded Unit.InstantiateSpawn and hit MissingMethodException - the
    /// probe finds the real name(s) at runtime instead of guessing them from a decompile).
    /// </summary>
    internal static readonly List<PatchTarget> PatchTargets = new();

    static float firstAttemptAt = float.NaN, lastRetryAt = float.NegativeInfinity;

    /// <summary>Called from Lads200Plugin.Update every frame; retries at most twice a second (CenturionLog's rate-limit idea).</summary>
    internal static void Tick()
    {
        if (LadsClone.IsRegistered || Lads200Plugin.FeatureOff) return;

        float now = Time.unscaledTime;
        if (float.IsNaN(firstAttemptAt)) firstAttemptAt = now;
        if (now - lastRetryAt < RetryIntervalS) return;
        lastRetryAt = now;

        if (now - firstAttemptAt > GiveUpS)
        {
            Lads200Plugin.DisableFeature("no MSV LADS in the encyclopedia within " + GiveUpS + " s of load");
            return;
        }

        try
        {
            LadsClone.TryRegister();
        }
        catch (Exception e)
        {
            // A member moved in a game update: do not spam retries, stand down with a clear line (PatchTarget.cs style).
            Lads200Plugin.DisableFeature($"{e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// Postfix on every spawn hook SpawnProbe found. Runs after the game instantiated/started a Unit; if that unit's prefab
    /// is ours (or named like our key) and registration has not finished yet, finish it right now so the encyclopedia entry
    /// exists before anything else can ask for it. Idempotent: once registered, this is a single cheap boolean test.
    /// </summary>
    static void AfterUnitSpawn(Unit __instance)
    {
        if (__instance == null || Lads200Plugin.FeatureOff || LadsClone.IsRegistered) return;

        string goName = __instance.gameObject != null ? __instance.gameObject.name : null;
        string defKey = GetDefinitionKey(__instance);
        if (!IsOurs(goName) && !IsOurs(defKey)) return;

        // A spawn of our prefab beat registration (only possible before the encyclopedia filled in): finish now, on the spot.
        try
        {
            if (LadsClone.TryRegister())
            {
                Lads200Plugin.Log.Info("spawn-guard: registered during a spawn request");
            }
            else
            {
                Lads200Plugin.Log.Warning("spawn-guard: our unit spawned before the stock MSV LADS was in the encyclopedia; retrying from Update");
            }
        }
        catch (Exception e)
        {
            Lads200Plugin.DisableFeature($"spawn-time registration failed: {e.GetType().Name}: {e.Message}");
        }
    }

    static bool IsOurs(string s)
        => s != null && (s.StartsWith(LadsClone.NewKey, StringComparison.Ordinal)
                         || s.StartsWith("MSV_LADS_200", StringComparison.Ordinal));

    /// <summary>unit.definition.jsonKey via reflection-free access where possible; definition may be null very early.</summary>
    static string GetDefinitionKey(Unit unit)
    {
        try
        {
            var defProp = AccessTools.Property(unit.GetType(), "definition") ?? AccessTools.Property(typeof(Unit), "definition");
            object def = defProp?.GetGetMethod()?.Invoke(unit, null);
            if (def == null) return null;
            var jsonKey = AccessTools.Field(def.GetType(), "jsonKey") ?? AccessTools.Field(typeof(UnitDefinition), "jsonKey");
            return jsonKey?.GetValue(def) as string;
        }
        catch
        {
            return null; // best-effort only; the gameObject-name check above still covers the common case
        }
    }
}
