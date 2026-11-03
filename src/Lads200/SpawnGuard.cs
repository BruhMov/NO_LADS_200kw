using System;
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

    internal static readonly PatchTarget[] PatchTargets =
    {
        // Unit.InstantiateSpawn(GameObject prefab, ...) is the single choke point every unit type passes through on every
        // peer (Mirage spawns instantiate their prefab here). The parameter list is left null so AccessTools matches by
        // name; if a game update removes the method entirely, InstallAll logs "missing: feature off" and local spawning
        // still works - only remote-peer spawn requests could then race registration.
        new("spawn-guard", typeof(Unit), "InstantiateSpawn",
            postfix: AccessTools.Method(typeof(SpawnGuard), nameof(AfterInstantiateSpawn))),
    };

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

    static void AfterInstantiateSpawn(ref GameObject __result)
    {
        if (__result == null || Lads200Plugin.FeatureOff) return;
        if (!__result.name.StartsWith(LadsClone.NewKey, StringComparison.Ordinal)
            && !__result.name.StartsWith("MSV_LADS_200", StringComparison.Ordinal)) return;
        if (LadsClone.IsRegistered) return;

        // A spawn of our prefab beat registration (only possible before the encyclopedia filled in): finish now, on the spot.
        try
        {
            if (LadsClone.TryRegister())
            {
                Lads200Plugin.Log.Info("spawn-guard: registered during a spawn request");
            }
        }
        catch (Exception e)
        {
            Lads200Plugin.DisableFeature($"spawn-time registration failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
