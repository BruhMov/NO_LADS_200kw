using System;
using System.Linq;
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
    public const string Version = "1.0.0";

    internal static ModLog Log;
    internal static bool FeatureOff;

    ConfigEntry<bool> enabled;
    Harmony harmony;

    void Awake()
    {
        Log = new ModLog(Logger);

        enabled = Config.Bind("General", "Enabled", true, "Adds the 'MSV LADS 200' ground unit (a clone of the stock MSV LADS)");
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
        }

        harmony = new Harmony(Guid);
        var off = PatchTarget.InstallAll(harmony, SpawnGuard.PatchTargets.ToList(), Log);
        if (off.Contains("spawn-guard"))
        {
            Log.Warning("spawn-guard patch target missing: remote-peer spawn safety off; the unit still registers locally");
        }

        Log.Info(Invariant($"MSV LADS 200 {Version} ({Guid}); Enabled={enabled.Value} Cost=${LadsClone.Cost:N0} Laser={LadsClone.LaserPowerKw:F0} kW blue"));
    }

    void Update()
    {
        if (FeatureOff || !enabled.Value)
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
