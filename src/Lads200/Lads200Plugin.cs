using System;

using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using static System.FormattableString;

namespace Lads200;

/// <summary>
/// MSV LADS 200: a $10,000,000 ground unit cloned at runtime from the stock MSV LADS, with its 80 kW laser upgraded to a
/// 200 kW blue one. Structure follows the MRB-100 Centurion plugin (github.com/baanish/MRB-100-Centurion): a BepInEx
/// BaseUnityPlugin, the same named PatchTarget table so a game update that moves a method switches the feature off instead
/// of failing the DLL, and a "[Lads200]" logger. Unlike the Centurion there is no Blueprinter .nobp bundle: the unit is a
/// live Object.Instantiate clone of the encyclopedia's MSV LADS definition + prefab (<see cref="LadsClone"/>), retried
/// until the game has finished loading its own units (<see cref="SpawnGuard.Tick"/>).
/// </summary>
[BepInPlugin(Guid, "MSV LADS 200", Version)]
public sealed class Lads200Plugin : BaseUnityPlugin
{
    public const string Guid = "razor.lads200";
    public const string Version = "1.0.4";

    internal static ModLog Log;
    internal static bool FeatureOff;

    ConfigEntry<bool> modEnabled; // not "enabled": that name hides MonoBehaviour.enabled (CS0108)
    Harmony harmony;

    void Awake()
    {
        Log = new ModLog(Logger);
        LadsClone.Log = Log; // static wiring (CS0649): without this every registration log line would hit a null logger

        modEnabled = Config.Bind("General", "Enabled", true, "Adds the 'MSV LADS 200' ground unit (a clone of the stock MSV LADS)");
        LadsClone.NewUnitName = Config.Bind("General", "UnitName", "MSV LADS 200",
            "Display name of the cloned unit in build/encyclopedia menus").Value;
        LadsClone.NewKey = Config.Bind("General", "UnitKey", "MSV_LADS_200",
            "Encyclopedia json key of the cloned unit; must differ from any stock or other mod key").Value;
        LadsClone.Cost = Config.Bind("General", "CostDollars", 10_000_000f,
            "Purchase price of the unit, in dollars").Value;
        LadsClone.LaserPowerKw = Config.Bind("Laser", "PowerKw", 200f,
            "The clone's laser power in kW (the stock MSV LADS fires 80 kW)").Value;

        // One-time cfg hygiene: the source unit must never be edited, only cloned from.
        if (LadsClone.NewKey == LadsClone.SourceKey || LadsClone.NewKey.Length == 0)
        {
            DisableFeature($"UnitKey '{LadsClone.NewKey}' would collide with the stock MSV LADS");
            return;
        }

        harmony = new Harmony(Guid);
        // Build the patch table from the spawn-hook names pinned against this game build (SpawnHooks.cs). The v1.0.3
        // substring probe found 0 targets - Unit declares no method containing "spawn"/"instantiate" - so the guard ran
        // without any hook; the pinned list resolves RegisterUnit/InitializeUnit/Awake/OnEnable/... at runtime instead.
        SpawnHooks.Discover(SpawnGuard.PatchTargets, Log);
        var off = PatchTarget.InstallAll(harmony, SpawnGuard.PatchTargets, Log);
        if (off.Count >= SpawnGuard.PatchTargets.Count)
        {
            Log.Warning("spawn-guard: no usable spawn hook was patched in this game build; remote-peer spawn safety off - " +
                        "the unit still registers locally from the Update retry loop");
        }

        Log.Info(Invariant($"MSV LADS 200 {Version} ({Guid}); Enabled={modEnabled.Value} Cost=${LadsClone.Cost:N0} Laser={LadsClone.LaserPowerKw:F0} kW blue"));
    }

    void Update()
    {
        if (FeatureOff || !modEnabled.Value)
        {
            if (LadsClone.IsRegistered) LadsClone.Disable();
            return;
        }

        SpawnGuard.Tick();
    }

    void OnDestroy() => harmony?.UnpatchSelf();

    /// <summary>Stands every feature down once, with one clear log line (CenturionPlugin.FeaturesOff, simplified).</summary>
    internal static void DisableFeature(string reason)
    {
        if (FeatureOff) return;
        FeatureOff = true;
        if (LadsClone.IsRegistered) LadsClone.Disable();
        Log.Error($"feature off: {reason}");
    }
}
