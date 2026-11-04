using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;

namespace Lads200;

/// <summary>
/// MSV LADS 200: a $50,000,000 ground unit cloned at runtime from the stock MSV LADS, with its 80 kW laser upgraded to
/// a 200 kW blue one. Everything else - model, colliders, sounds, turret, vehicle type - is the stock unit, copied to
/// the tee by cloning the game's own definition and prefab.
///
/// Structure follows the MRB-100 Centurion plugin (github.com/baanish/MRB-100-Centurion): a BepInEx BaseUnityPlugin,
/// a "[Lads200]" logger, and features that stand down with one clear log line instead of throwing. The registration
/// recipe is the Blueprinter loader's (github.com/nikkorap/NOBlueprinter-Releases): a postfix on
/// Encyclopedia.AfterLoad() registers the unit inside the game's own load flow, into Encyclopedia.Lookup, the typed
/// catalog lists, IndexLookup (the network definition index) and with CacheMass(). See <see cref="LadsCatalog"/> for
/// the full walkthrough.
/// </summary>
[BepInPlugin(Guid, "MSV LADS 200", Version)]
public sealed class Lads200Plugin : BaseUnityPlugin
{
    public const string Guid = "razor.lads200";
    public const string Version = "2.1.0";

    internal static ModLog Log { get; private set; }
    internal static bool FeatureOff;

    ConfigEntry<bool> modEnabled; // not "enabled": that name hides MonoBehaviour.enabled (CS0108)
    Harmony harmony;

    // Fallback path when the AfterLoad hook is missing (a game update renamed it): retry from Update for a while.
    static readonly Stopwatch RetryClock = new();
    static bool retryClockRunning;
    const float RetryIntervalS = 2f;
    const float GiveUpS = 300f;
    static float lastAttemptS;

    void Awake()
    {
        Log = new ModLog(Logger);
        LadsCatalog.Log = Log;               // static wiring (CS0649): without these every line below would hit a null logger
        LadsWeapon.Log = Log;

        modEnabled = Config.Bind("General", "Enabled", true, "Adds the 'MSV LADS 200' ground unit (a clone of the stock MSV LADS)");
        LadsCatalog.NewUnitName = Config.Bind("General", "UnitName", "MSV LADS 200",
            "Display name of the cloned unit in build/encyclopedia menus").Value;
        LadsCatalog.NewKey = Config.Bind("General", "UnitKey", "MSV_LADS_200",
            "Encyclopedia json key of the cloned unit; must differ from any stock or other mod key").Value;
        string sourceOverride = Config.Bind("General", "SourceKeyOverride", "",
            "Encyclopedia key of the stock MSV LADS to clone; leave empty for the built-in 'MSV_LADS' (tolerant match)").Value;
        if (!string.IsNullOrWhiteSpace(sourceOverride)) LadsCatalog.SourceKey = sourceOverride.Trim();

        LadsCatalog.Cost = BindCost();

        LadsWeapon.PowerKw = Config.Bind("Laser", "PowerKw", 200f,
            "The clone's laser power in kW (the stock MSV LADS fires 80 kW)").Value;
        LadsWeapon.FireDamageScale = Config.Bind("Laser", "FireDamageScale", -1f,
            "Fire damage multiplier: -1 = automatic (PowerKw / stock power, 2.5x for 200 kW), 0 = keep stock, >0 = that factor").Value;
        LadsWeapon.BeamColor = ParseColor(Config.Bind("Laser", "BeamColor", "0.25, 0.60, 1.00",
            "Beam color as R,G,B floats in 0..1 (bright electric blue by default)").Value);
        LadsWeapon.WeaponDisplayName = Config.Bind("Laser", "WeaponName", "200 kW Laser",
            "Display name of the upgraded weapon in menus and the kill feed").Value;
        LadsWeapon.BoostPowerSupply = Config.Bind("Power", "BoostPowerSupply", true,
            "Raise the cloned unit's PowerSupply so it can feed the 200 kW laser").Value;
        LadsNet.RootHash = Config.Bind("Network", "PrefabHash", 987654321,
            "Mirage prefab hash for the cloned unit's root NetworkIdentity. Must be unique among stock units and other mods, and identical on every peer in multiplayer").Value;
        LadsWeapon.DumpMembers = Config.Bind("Debug", "DumpMembers", true,
            "Log every field of the stock Laser/PowerSupply once at registration, so a game update that renames a member can be pinned from the log").Value;

        // One-time cfg hygiene: the clone key must never collide with the source's.
        if (LadsCatalog.NewKey == LadsCatalog.SourceKey || LadsCatalog.NewKey.Length == 0)
        {
            DisableFeature($"UnitKey '{LadsCatalog.NewKey}' would collide with the stock MSV LADS");
            return;
        }

        harmony = new Harmony(Guid);
        InstallAfterLoadHook();

        Log.Info($"MSV LADS 200 {Version} ({Guid}); Enabled={modEnabled.Value} Cost=${LadsCatalog.Cost:N0} " +
                 $"Laser={LadsWeapon.PowerKw:F0} kW blue; hook={(afterLoadHookInstalled ? "Encyclopedia.AfterLoad" : "fallback timer")}");
    }

    /// <summary>
    /// The price lives under General.UnitCostDollars. v1.0.x-v2.0.0 bound General.CostDollars with a $10,000,000
    /// default, and a value saved in razor.lads200.cfg silently overrides any new default bound under the SAME key -
    /// which is why a fresh v2.0.0 still logged Cost=$10,000,000. The new key starts clean at the $50,000,000 spec; a
    /// price the user deliberately customized (anything other than 10M/50M) carries over, and the stale entry is
    /// removed from the file when this BepInEx supports ConfigFile.Remove.
    /// </summary>
    float BindCost()
    {
        const float spec = 50_000_000f, staleDefault = 10_000_000f;
        ConfigEntry<float> cost = Config.Bind("General", "UnitCostDollars", spec, "Purchase price of the unit, in dollars");
        ConfigEntry<float> stale = Config.Bind("General", "CostDollars", spec, "(superseded by UnitCostDollars; left over from v1.x-v2.0.0)");

        bool fileCarriedStaleKey = Math.Abs(stale.Value - spec) > 0.01f;
        if (fileCarriedStaleKey)
        {
            bool userPrice = Math.Abs(stale.Value - staleDefault) > 0.01f;      // not the wrong v1 default = deliberate
            bool newKeyUntouched = Math.Abs(cost.Value - spec) < 0.01f;         // don't clobber a newer setting
            if (userPrice && newKeyUntouched)
            {
                cost.Value = stale.Value;
                Log.Info($"price: carried your customized ${stale.Value:N0} over from the old CostDollars key to UnitCostDollars");
            }
            else
            {
                Log.Info($"price: the old CostDollars key carried ${stale.Value:N0} (the wrong v1 default); " +
                         $"the new UnitCostDollars key holds the ${spec:N0} spec - delete BepInEx\\config\\razor.lads200.cfg to reset everything");
            }
        }

        try
        {
            MethodInfo remove = typeof(ConfigFile).GetMethod("Remove",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(ConfigEntryBase) }, null);
            if (remove != null)
            {
                remove.Invoke(Config, new object[] { stale });
                Config.Save();
            }
        }
        catch { /* older BepInEx without ConfigFile.Remove: the orphaned CostDollars line is harmless */ }

        return cost.Value;
    }

    bool afterLoadHookInstalled;

    /// <summary>
    /// The primary registration path, Blueprinter's exact entry point (EncyclopediaAfterLoad.cs patches
    /// Encyclopedia.AfterLoad()). Installing in Awake is early enough: BepInEx chains plugins before the game boots its
    /// encyclopedia, and the postfix runs while the game is still inside its load flow - before menus are built or
    /// network prefabs are registered.
    /// </summary>
    void InstallAfterLoadHook()
    {
        try
        {
            Type enc = GameTypes.Encyclopedia;
            MethodInfo afterLoad = enc == null ? null : AccessTools.Method(enc, "AfterLoad", Type.EmptyTypes);
            if (enc == null || afterLoad == null)
            {
                Log.Error($"hook: Encyclopedia.AfterLoad() not found ({(enc == null ? "type missing" : "method missing/renamed")}); " +
                          "falling back to the Update retry - if the unit does not appear, report the game version");
                return;
            }

            harmony.Patch(afterLoad, postfix: new HarmonyMethod(typeof(LadsPatches), nameof(LadsPatches.AfterEncyclopediaLoad)));
            afterLoadHookInstalled = true;
            Log.Info("hook: Encyclopedia.AfterLoad postfix installed");
        }
        catch (Exception e)
        {
            Log.Error($"hook: AfterLoad patch failed ({e.GetType().Name}: {e.Message}); falling back to the Update retry");
        }
    }

    void Update()
    {
        if (FeatureOff) return;

        if (!modEnabled.Value)
        {
            if (LadsCatalog.IsRegistered) LadsCatalog.Disable();
            return;
        }

        if (LadsCatalog.IsRegistered) return;

        // Fallback timer (a renamed AfterLoad, or an unusual load order): try every 2 s, give up after 300 s.
        if (!retryClockRunning)
        {
            retryClockRunning = true;
            RetryClock.Start();
        }

        float elapsed = (float)RetryClock.Elapsed.TotalSeconds;
        if (elapsed < lastAttemptS + RetryIntervalS) return;
        lastAttemptS = elapsed;

        if (elapsed > GiveUpS)
        {
            DisableFeature("the stock MSV LADS never appeared in the encyclopedia within " + GiveUpS + " s of load");
            return;
        }

        try
        {
            if (LadsCatalog.TryRegister(null, "fallback timer")) return;

            long since = (long)elapsed;
            if (since % 20 < 2) // a heartbeat roughly every 20 s, not every retry
                Log.Warning($"still waiting for the stock MSV LADS ({since} s of {GiveUpS:F0}); see the earlier 'source ... not found' line for the key candidates");
        }
        catch (Exception e)
        {
            DisableFeature($"{e.GetType().Name}: {e.Message}");
        }
    }

    void OnDestroy() => harmony?.UnpatchSelf();

    /// <summary>Stands every feature down once, with one clear log line (CenturionPlugin.FeaturesOff, simplified).</summary>
    internal static void DisableFeature(string reason)
    {
        if (FeatureOff) return;
        FeatureOff = true;
        if (LadsCatalog.IsRegistered) LadsCatalog.Disable();
        Log.Error($"feature off: {reason}");
    }

    static Color ParseColor(string text)
    {
        var parts = (text ?? "").Split(',');
        if (parts.Length >= 3
            && float.TryParse(parts[0].Trim(), out float r) && float.TryParse(parts[1].Trim(), out float g)
            && float.TryParse(parts[2].Trim(), out float b))
        {
            // Accept both 0..1 floats and 0..255 bytes.
            if (r > 1f || g > 1f || b > 1f) return new Color(r / 255f, g / 255f, b / 255f);
            return new Color(r, g, b);
        }

        Log?.Warning($"BeamColor '{text}' is not R,G,B floats; using the default bright electric blue");
        return new Color(0.25f, 0.60f, 1.00f);
    }
}

/// <summary>The one Harmony patch: runs inside the game's encyclopedia load flow, exactly where Blueprinter registers .nobp units.</summary>
static class LadsPatches
{
    /// <summary>Parameter typed as object on purpose: the plugin compiles without referencing Assembly-CSharp.dll.</summary>
    public static void AfterEncyclopediaLoad(object __instance)
    {
        if (Lads200Plugin.FeatureOff || LadsCatalog.IsRegistered) return;
        try
        {
            Lads200Plugin.Log.StageOnce("afterload-fired",
                "encyclopedia load observed (Encyclopedia.AfterLoad fired): registering");
            if (LadsCatalog.TryRegister(__instance, "Encyclopedia.AfterLoad"))
                Lads200Plugin.Log.Info("registered during Encyclopedia.AfterLoad (the Blueprinter entry point)");
        }
        catch (Exception e)
        {
            Lads200Plugin.DisableFeature($"registration during AfterLoad failed: {e.GetType().Name}: {e.Message}");
        }
    }
}
