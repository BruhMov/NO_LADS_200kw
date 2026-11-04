# MSV LADS 200 — a Nuclear Option BepInEx mod

A **$50,000,000** copy of the stock **MSV LADS** ground unit with a **200 kW blue laser** (the stock unit fires 80 kW).
Everything else — model, colliders, turret, sounds, vehicle type — is the stock unit, copied to the tee by cloning the
game's own `UnitDefinition` and prefab at runtime. No Unity project, no Blueprinter `.nobp` bundle: just one DLL.

## Why v2.1 works where v2.0.0 / v1.0.x never showed the unit

**v1.0.x** registered the clone from an `Update()` retry loop, *after* the game had already finished loading and
consumed its catalogs. **v2.0.0** had the right entry point but three defects that could abort or hide the
registration: it re-pointed the clone's `Unit.definition` by looking for a component *literally named* `Unit` (the LADS
root is a `GroundVehicle`, a `Unit` subclass → `MissingFieldException` → the whole feature stood down), it scanned only
*fields* for the catalog lists (a property-typed `vehicles` would have been missed), and one missing weapon member
aborted everything. v2.1 fixes all of that and registers through the exact entry point and recipe the
[Blueprinter loader](https://github.com/nikkorap/NOBlueprinter-Releases) uses for `.nobp` units
(`EncyclopediaLoader.cs` / `EncyclopediaAfterLoad.cs`), so the game treats the clone like any stock unit:

| Registration step | Where it comes from |
|---|---|
| Harmony postfix on **`Encyclopedia.AfterLoad()`** | Blueprinter's load entry point: registration lands inside the game's load flow, before menus/network read the catalog |
| Clone the definition (`Object.Instantiate`, keeps every serialized field) | restamped `jsonKey`/`unitName`/`value` (+`emptyCost`/`cost` where present) |
| Clone the prefab **under an inactive `DontDestroyOnLoad` holder** | no `Awake` runs, nothing renders in the menu, survives every scene load (v1.0.x kept a live object in the menu scene) |
| Re-point every root member that referenced the stock **definition** at ours (reference-matched, not name-matched) | v2.0.0 looked for a component literally named `Unit` and aborted on the `GroundVehicle` root; matching by *what points at the stock definition* works for `Unit.definition`, `Unit.Type`, any subclass, and never kills the registration |
| **Fresh `NetworkIdentity.PrefabHash`** for the clone (root = `Network.PrefabHash` in the config) | Mirage spawns by hash; v1.0.x's clone shared the stock prefab's hash |
| `Encyclopedia.Lookup[key]` + every typed catalog list carrying the stock LADS (`vehicles`, … — **fields and properties**) | the mission editor, encyclopedia, AI depots and vehicle shops all read these |
| **`IndexLookup` + `definition.LookupIndex`** | the network definition index — v1.0.x never set it, and the game could not spawn the unit |
| **`definition.CacheMass()`** | Blueprinter's final registration step |

The weapon upgrade is applied once, **on the cloned prefab**, so every spawned instance inherits it: `Laser.power`
80 → 200 kW, `Laser.fireDamage` scaled with the power (2.5× by default), the beam tinted bright electric blue
(`Laser.beamColor`, falling back to the beam renderers' instanced materials), the `WeaponInfo` cloned and renamed to
"200 kW Laser", and every `PowerSupply` max-power member raised to cover the draw. Field names verified against the
[MRB-100 Centurion](https://github.com/baanish/MRB-100-Centurion)'s authoring code (`CenturionAuthor.Cram.cs`
serializes the game's real `Laser` component). The stock MSV LADS is never modified.

Built in the Centurion's plugin shape (netstandard2.1 BepInEx plugin, `[Lads200]` logger, features that stand down with
one clear log line). The plugin compiles **without referencing `Assembly-CSharp.dll`** — every game type is resolved by
name at runtime — so the build needs nothing but BepInEx core and Unity modules, and game patches that only move
namespaces cannot break the build.

## Files (`src/Lads200/`)

| File | Role |
|---|---|
| `Lads200Plugin.cs` | `[BepInPlugin("razor.lads200", ...)]` entry point; all config entries; installs the `Encyclopedia.AfterLoad` postfix; fallback Update retry (2 s interval, 300 s give-up) if a game update renames the hook. |
| `LadsCatalog.cs` | The registration heart: finds the stock MSV LADS (tolerant key/name match), clones definition + prefab, re-points the clone's definition references (reference-matched), registers `Lookup` / `IndexLookup` + `LookupIndex` / typed catalog lists (fields **and** properties) / `CacheMass()` exactly like Blueprinter; one-shot `menu-check:` diagnostics; clean rollback via `Disable()`. |
| `LadsWeapon.cs` | The 200 kW blue laser on the cloned prefab: power, scaled fire damage, beam color (color field → LineRenderers → instanced materials), cloned+renamed WeaponInfo, PowerSupply boost, and a one-shot member dump (`Debug.DumpMembers`) so any renamed field can be pinned from the log. Non-fatal: a missing weapon class logs an error with the prefab's component list and still registers the unit. |
| `LadsNet.cs` | Fresh `PrefabHash` assignment for the clone's NetworkIdentities (Blueprinter's `PrefabHashAssigner`, condensed), with collision check against every loaded prefab. |
| `GameReflect.cs` | Runtime type resolution (`Encyclopedia`, `UnitDefinition`, `Mirage.NetworkIdentity`) and field/property helpers. |
| `ModLog.cs` | Centurion-style `[Lads200]` logger with once-per-stage lines. |

## Install

1. Nuclear Option with [BepInEx 5](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.4) installed
   (Blueprinter is **not** required — this mod has no `.nobp`).
2. Copy `Lads200.dll` into `<game folder>\BepInEx\plugins\`.
3. Launch. `BepInEx\LogOutput.log` should show:

   ```
   [Info :MSV LADS 200] hook: Encyclopedia.AfterLoad postfix installed
   [Info :MSV LADS 200] MSV LADS 200 2.1.0 (razor.lads200); Enabled=True Cost=$50,000,000 Laser=200 kW blue; hook=Encyclopedia.AfterLoad
   [Info :MSV LADS 200] encyclopedia load observed (Encyclopedia.AfterLoad fired): registering
   [Info :MSV LADS 200] definition references repointed on the clone's root: GroundVehicle.definition
   [Info :MSV LADS 200] registered 'MSV LADS 200' (key MSV_LADS_200) via Encyclopedia.AfterLoad: cost $50,000,000, laser 200 kW blue, prefab 'MSV_LADS_200'
   [Info :MSV LADS 200] menu-check: Lookup=True, lists=[vehicles], GroundVehicle=yes, key='MSV_LADS_200', name='MSV LADS 200', cost=$50,000,000
   ```

4. The unit appears next to the MSV LADS in the mission editor / sandbox build menus, in the encyclopedia, in AI
   garrisons, and in the [PlayableVehicles](https://github.com/BruhMov/NO-PlayableVec-personal-mods) shop (AAA tab).
   Missions and loadouts reference it by the key `MSV_LADS_200`.

**Multiplayer:** every player in the lobby needs this DLL (like any unit-adding mod), and everyone should run the same
`Network.PrefabHash` value (the default is fine unless another mod complains about a collision).

**Upgrading from v1.0.x / v2.0.0:** nothing to do. The price moved to the `General.UnitCostDollars` key because the old
`CostDollars` key shipped with a wrong $10,000,000 default in v1.x, and a value already saved in
`razor.lads200.cfg` silently overrides any new default bound under the same key — that is why v2.0.0 still logged
`Cost=$10,000,000`. v2.1.0 starts the new key at the $50,000,000 spec, carries over a price you deliberately customized,
and removes the stale entry automatically. A hard reset is always `delete BepInEx\config\razor.lads200.cfg`.

## Configuration (`BepInEx/config/razor.lads200.cfg`)

| Section.Entry | Default | Meaning |
|---|---|---|
| `General.Enabled` | `true` | Adds the unit |
| `General.UnitName` | `MSV LADS 200` | Display name |
| `General.UnitKey` | `MSV_LADS_200` | Encyclopedia json key (must be unique) |
| `General.SourceKeyOverride` | *(empty)* | Force a different stock unit to clone from |
| `General.UnitCostDollars` | `50000000` | Purchase price (v2.1.0 key; the old `CostDollars` entry is migrated/removed automatically) |
| `Laser.PowerKw` | `200` | Laser power in kW |
| `Laser.FireDamageScale` | `-1` | `-1` = scale with power (2.5×), `0` = stock damage, `>0` = fixed factor |
| `Laser.BeamColor` | `0.25, 0.60, 1.00` | Beam color, R,G,B in 0..1 (or 0..255) |
| `Laser.WeaponName` | `200 kW Laser` | Weapon display name |
| `Power.BoostPowerSupply` | `true` | Raise the PowerSupply so it can feed the 200 kW draw |
| `Network.PrefabHash` | `987654321` | Mirage prefab hash for the clone's root (same on every peer) |
| `Debug.DumpMembers` | `true` | Log every Laser/PowerSupply field once at registration, for pinning renamed members |

## Build

The project no longer needs the game's assemblies. From `src/Lads200`:

```powershell
dotnet build Lads200.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option"
```

Any `GameDir` with `BepInEx\core\{BepInEx,0Harmony}.dll` and `NuclearOption_Data\Managed\UnityEngine*.dll` works —
your game install, or the `game-ref` folder (see `game-ref/README.txt`). Output:
`bin/Release/netstandard2.1/Lads200.dll`. Requires the .NET SDK 6+; the DLL runs on Mono inside the game.

## Troubleshooting

| Log line | Meaning | Fix |
|---|---|---|
| `hook: Encyclopedia.AfterLoad() not found … falling back` | a game update renamed the load hook | the fallback timer still registers; report the game version |
| `still waiting for the stock MSV LADS …` | the stock unit's key differs | set `SourceKeyOverride` to a key from the same log's `source '…' not found … keys containing 'LADS'` line |
| `feature off: <Exception>` | a member moved in a game update | the line names the member; with `Debug.DumpMembers` the log also carries the full field dump to pin the new name |
| `no laser-type component on the cloned prefab …` | the weapon class moved in a game update | the unit registered with its stock weapon; the same line lists every component on the clone — report it |
| `no member on the cloned prefab's root referenced the stock definition` | the identity member moved | the unit still spawns with the 200 kW laser; it only *identifies* as the stock LADS — report the line |
| `weapons: … power 80 -> 200 kW on 1 …` | the upgrade landed on N lasers | if `on 0`, the `Laser.power` field name moved — report the `laser dump` line |
| `no Laser color field and no tintable beam renderer found` | beam color member unknown in this build | the beam stays stock-colored (still 200 kW); report the `laser dump` line so the color member can be pinned |

## Notes / honest limits

- The stock MSV LADS keeps its 80 kW laser, price and catalog rows untouched.
- Icons/thumbnails show the stock MSV LADS art (the model is identical; only the beam differs, when firing).
- The mission editor preview ghost shows the cloned prefab, so it already fires blue in previews once spawned.
