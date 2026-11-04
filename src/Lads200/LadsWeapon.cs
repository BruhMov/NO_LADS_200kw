using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lads200;

/// <summary>
/// The weapon half of the MSV LADS 200, applied once to the cloned prefab so every spawned instance inherits it. Field
/// names are pinned against the MRB-100 Centurion's authoring code (unity/Assets/Centurion/Editor/CenturionAuthor.Cram.cs),
/// which serializes the game's real Laser component:
///   Laser.power (float, kW - the C-RAM sets 120, "the same as the Medusa"), Laser.fireDamage ("less power, less damage"),
///   Laser.blastDamage, Laser.maxAngle, Laser.vehicularPowerSupply, Laser.damageAtRange (AnimationCurve), Laser.info
///   (WeaponInfo asset) and WeaponInfo.weaponName / WeaponInfo.shortName.
/// The stock MSV LADS prefab itself is never touched: it keeps its 80 kW laser.
///
/// v2.1.0: nothing in here is fatal anymore. A missing Laser class or member logs the dump lines needed to pin the new
/// name and the unit still registers - v2.0.0 aborted the whole registration on the first surprise, which is one more
/// reason the unit never appeared. Components are matched subclass-tolerantly (exact name, then the resolved game type,
/// then the name suffix) so a LaserTurret-style subclass is still upgraded.
/// </summary>
static class LadsWeapon
{
    // Config, wired from the plugin.
    internal static ModLog Log;
    internal static float PowerKw = 200f;
    /// <summary>Fire-damage multiplier: -1 = automatic (PowerKw / stock power), 0 = keep stock, &gt;0 = that factor.</summary>
    internal static float FireDamageScale = -1f;
    internal static Color BeamColor = new Color(0.25f, 0.60f, 1.00f);
    internal static string WeaponDisplayName = "200 kW Laser";
    internal static bool BoostPowerSupply = true;
    internal static bool DumpMembers = true;

    static readonly string[] PowerNames = { "power", "powerKW", "powerKw" };
    static readonly string[] FireDamageNames = { "fireDamage", "damage" };
    static readonly string[] ColorNames = { "beamColor", "laserColor", "colour", "color" };
    static readonly string[] InfoNames = { "info", "weaponInfo" };
    static readonly string[] MaxPowerNames = { "maxPower", "maximumPower", "maxPowerKW", "maxPowerKw", "maxCharge", "maxChargeKJ", "capacity" };

    static Object clonedInfo; // one cloned WeaponInfo per session, shared by every LADS 200 laser

    /// <summary>
    /// Upgrades every Laser on the cloned prefab: 200 kW, fire damage scaled with the power, blue beam, renamed weapon
    /// info, and the carrier's PowerSupply raised so it can feed the bigger draw. Every failure is logged and skipped;
    /// only thePrefab itself is required.
    /// </summary>
    public static void UpgradeClonePrefab(GameObject prefab, ModLog log)
    {
        int lasers = 0, powered = 0, damaged = 0, tinted = 0, renamed = 0;
        float stockPower = float.NaN;

        foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
        {
            if (c == null || !IsLaser(c)) continue;
            Type t = c.GetType();
            lasers++;

            if (DumpMembers) log.Info($"laser dump ({c.name}, {t.Name}): {GameReflect.DescribeFields(t, c)}");

            // --- power: 80 -> 200 kW. Reading the stock value back also proves the field name on this game build.
            if (GameReflect.FindMember(t, c, PowerNames, out object powerBoxed) is { } powerMember)
            {
                stockPower = powerBoxed is float sp ? sp : float.NaN;
                GameReflect.SetThrough(powerMember, c, PowerKw);
                powered++;
            }
            else log.Warning($"Laser.power not found on {t.FullName} (candidates {string.Join("/", PowerNames)}): beam stays {PowerKw} kW short - report the laser dump line above");

            // --- fire damage scales with the power unless configured otherwise ("less power, less damage").
            if (GameReflect.FindMember(t, c, FireDamageNames, out object damageBoxed) is { } damageMember && damageBoxed is float stockDamage)
            {
                float scale = FireDamageScale < 0f
                    ? (float.IsNaN(stockPower) || stockPower <= 0f ? 1f : PowerKw / stockPower) // auto: 200/80 = 2.5
                    : FireDamageScale;
                if (scale > 0f)
                {
                    GameReflect.SetThrough(damageMember, c, stockDamage * scale);
                    damaged++;
                }
            }

            // --- beam color: serialized color field first, then the beam renderers.
            if (TintLaser(t, c, log)) tinted++;

            // --- weapon display name: one cloned WeaponInfo ("200 kW Laser"), shared by all our lasers.
            if (RenameWeaponInfo(t, c, log)) renamed++;
        }

        if (lasers == 0)
        {
            // Not fatal: the unit registers with its stock weapon so it is at least visible and reportable.
            var components = new HashSet<string>();
            foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
            {
                if (c != null) components.Add(c.GetType().Name);
            }
            log.Error($"no laser-type component on the cloned prefab (looked for 'Laser' and subclasses): the MSV LADS 200 " +
                      $"registered with its STOCK weapon. Components on the clone: {string.Join(", ", components)} - report this line");
            return;
        }

        log.Info($"weapons: {lasers} laser(s): power {stockPower:0} -> {PowerKw:0} kW on {powered}, fire damage scaled on {damaged}, " +
                 $"beam tinted blue on {tinted}, weapon info renamed on {renamed}");

        if (BoostPowerSupply) EnsurePowerSupply(prefab, log);
    }

    /// <summary>Laser by exact name, by the resolved game type, or by name suffix - catches subclasses like a hypothetical LaserTurret.</summary>
    static bool IsLaser(Component c)
    {
        Type t = c.GetType();
        if (t.Name == "Laser") return true;
        if (GameTypes.Laser != null && GameTypes.Laser.IsInstanceOfType(c)) return true;
        return t.Name.EndsWith("Laser", StringComparison.Ordinal);
    }

    /// <summary>PowerSupply by exact name, by the resolved game type, or by name suffix.</summary>
    static bool IsPowerSupply(Component c)
    {
        Type t = c.GetType();
        if (t.Name == "PowerSupply") return true;
        if (GameTypes.PowerSupply != null && GameTypes.PowerSupply.IsInstanceOfType(c)) return true;
        return t.Name.EndsWith("PowerSupply", StringComparison.Ordinal);
    }

    /// <summary>Sets the Laser's serialized color member if it has one; otherwise tints the beam's renderers (instanced materials, so the stock LADS is untouched).</summary>
    static bool TintLaser(Type t, Component laser, ModLog log)
    {
        if (GameReflect.FindMember(t, laser, ColorNames, out object colorBoxed) is { } member && colorBoxed is Color)
        {
            GameReflect.SetThrough(member, laser, BeamColor);
            log.Info($"beam color set through Laser.{member.Name}");
            return true;
        }

        // Visual fallback: LineRenderers and other renderers under the laser object. renderer.material INSTANCES the
        // shared material on our clone only - the stock MSV LADS keeps its color.
        int tinted = 0;
        foreach (var renderer in RenderersUnder(laser))
        {
            LineRenderer line = renderer as LineRenderer;
            if (line != null)
            {
                line.startColor = BeamColor;
                line.endColor = BeamColor;
                tinted++;
            }

            foreach (Material m in renderer.materials) // .material = per-clone instances, never the shared asset
            {
                if (m == null) continue;
                if (m.HasProperty("_BaseColor")) { m.SetColor("_BaseColor", BeamColor); tinted++; }
                else if (m.HasProperty("_Color")) { m.SetColor("_Color", BeamColor); tinted++; }
            }
        }

        if (tinted > 0)
        {
            log.Info($"beam color set through {tinted} renderer(s)/material instance(s) under '{laser.name}'");
            return true;
        }

        log.Warning("no Laser color field and no tintable beam renderer found: the beam keeps the stock color " +
                    "(still " + PowerKw + " kW) - report the laser dump line so the next build can pin the color member");
        return false;
    }

    static IEnumerable<Renderer> RenderersUnder(Component c)
    {
        foreach (Renderer r in c.GetComponentsInChildren<Renderer>(true)) yield return r;
    }

    /// <summary>Clones the laser's WeaponInfo once and restamps its display names ("200 kW Laser"), so the shop, the encyclopedia and the kill feed name the upgraded weapon.</summary>
    static bool RenameWeaponInfo(Type t, Component laser, ModLog log)
    {
        if (GameReflect.FindMember(t, laser, InfoNames, out object infoBoxed) is not { } infoMember || !(infoBoxed is Object info)) return false;

        if (clonedInfo == null)
        {
            clonedInfo = Object.Instantiate(info);
            clonedInfo.name = "Lads200_WeaponInfo";
            Type infoType = clonedInfo.GetType();
            if (!GameReflect.SetMember(infoType, clonedInfo, "weaponName", WeaponDisplayName))
                log.Warning($"WeaponInfo.weaponName not found on {infoType.FullName}: weapon keeps the stock display name");
            GameReflect.SetMember(infoType, clonedInfo, "shortName", "LADS 200KW");
        }

        GameReflect.SetThrough(infoMember, laser, clonedInfo);
        return true;
    }

    /// <summary>A 200 kW laser needs a supply that can feed it: raise every PowerSupply's max-* member below the draw (the member name is not verified anywhere - candidates, logged).</summary>
    static void EnsurePowerSupply(GameObject prefab, ModLog log)
    {
        int raised = 0;
        foreach (Component c in prefab.GetComponentsInChildren<Component>(true))
        {
            if (c == null || !IsPowerSupply(c)) continue;
            Type t = c.GetType();
            if (DumpMembers) log.Info($"power supply dump ({c.name}, {t.Name}): {GameReflect.DescribeFields(t, c)}");

            foreach (string name in MaxPowerNames)
            {
                if (GameReflect.GetMember(t, c, name) is float max && max < PowerKw && GameReflect.SetMember(t, c, name, PowerKw))
                {
                    raised++;
                    log.Info($"PowerSupply.{name} raised {max:0} -> {PowerKw:0} kW");
                }
            }
        }

        if (raised == 0)
            log.Info("no PowerSupply max-power member below the laser draw was found: supply left stock " +
                     "(if the 200 kW beam sputters, report the power supply dump line above)");
    }
}
