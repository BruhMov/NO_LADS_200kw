using System;
using System.Collections.Generic;
using System.Reflection;
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
    /// Populated at plugin Awake by <see cref="SpawnHooks.Discover"/>: one entry per spawn/lifecycle hook the game build
    /// actually declares on Unit (v1.0.0 hardcoded Unit.InstantiateSpawn and hit MissingMethodException; v1.0.3's substring
    /// probe found 0 hooks - this build names none of them spawn/instantiate; the pinned list in SpawnHooks.cs resolves the
    /// real ones, e.g. RegisterUnit / InitializeUnit / Awake / OnEnable / OnStartServer).
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
    /// Postfix on every spawn hook SpawnHooks found. Runs after the game instantiated/started a Unit; if that unit's prefab
    /// is ours (or named like our key) and registration has not finished yet, finish it right now so the encyclopedia entry
    /// exists before anything else can ask for it. Idempotent: once registered, this is a single cheap boolean test.
    /// </summary>
    // internal: SpawnHooks takes its MethodInfo with AccessTools.Method(typeof(SpawnGuard), nameof(AfterUnitSpawn)).
    internal static void AfterUnitSpawn(object __instance)
    {
        // Parameter typed as `object` on purpose: Mirage (NetworkBehaviour, the base of Unit) lives in Managed\Mirage.dll
        // which we deliberately do not reference - a strongly-typed postfix would force every user to add that dll.
        if (__instance == null || Lads200Plugin.FeatureOff || LadsClone.IsRegistered) return;

        string goName = GetGameObjectName(__instance);
        string defKey = GetDefinitionKey(__instance);
        if (!IsOurs(goName) && !IsOurs(defKey)) return;

        // v1.0.5: the patched hooks are lifecycle callbacks (Awake/OnEnable/OnStartServer), which also fire for an
        // unspawned instance of the stock LADS prefab sitting in the encyclopedia-preview / object-pool scene - its GO
        // name matches ours before any definition is attached. Registering from that would let TryRegister's unitName
        // fallback stamp jsonKey/unitName onto the SHARED stock definition ("the stock MSV LADS turned into the 200").
        // Only a Unit that already carries a definition key may register here; the legitimate early-spawn race (a real
        // networked spawn beating the Update retry) still has defKey by then, and everything else waits for the retry loop.
        if (defKey == null)
        {
            if (goName != null && goName.IndexOf("200", StringComparison.Ordinal) >= 0)
            {
                Lads200Plugin.Log.StageOnce("guard-unspawned", "spawn-guard: our prefab instantiated without a definition (menu preview/pool); ignoring, waiting for the Update retry");
            }

            return;
        }

        // A spawn (or Awake/OnEnable of our spawned Unit) beat registration (only possible before the encyclopedia
        // filled in): finish now, on the spot.
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

    /// <summary>
    /// ((Component)__instance).gameObject.name - reached by reflection so the Mirage assembly (which defines the
    /// NetworkBehaviour base class Unity's Component lookup passes through) never needs referencing at compile time.
    /// </summary>
    static string GetGameObjectName(object unit)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object go = null;
            PropertyInfo p = unit.GetType().GetProperty("gameObject", flags);
            if (p != null)
            {
                go = p.GetValue(unit, null); // UnityEngine.Object's op_Equality is unavailable here; catch destroyed refs instead
            }
            else
            {
                FieldInfo f = unit.GetType().GetField("gameObject", flags); // some builds name the field this way
                go = f?.GetValue(unit);
            }

            if (go == null) return null;
            return go.GetType().GetProperty("name")?.GetValue(go, null) as string;
        }
        catch
        {
            return null; // destroyed object / missing member; the definition-key check below still covers it
        }
    }

    /// <summary>unit.definition.jsonKey via plain reflection; definition may be null very early.</summary>
    static string GetDefinitionKey(object unit)
    {
        try
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            object def = null;
            PropertyInfo defProp = unit.GetType().GetProperty("definition", flags);
            if (defProp != null)
            {
                def = defProp.GetValue(unit, null);
            }
            else
            {
                def = unit.GetType().GetField("definition", flags)?.GetValue(unit);
            }

            if (def == null) return null;
            if (def.GetType().GetField("jsonKey", flags) is FieldInfo f) return f.GetValue(def) as string;
            if (def.GetType().GetProperty("jsonKey", flags)?.GetGetMethod() is MethodInfo g) return g.Invoke(def, null) as string;
            return null;
        }
        catch
        {
            return null; // best-effort only
        }
    }
}
