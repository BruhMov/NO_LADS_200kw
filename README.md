# MSV LADS 200 — a Nuclear Option BepInEx mod

A $10,000,000 copy of the stock **MSV LADS** ground unit with a **200 kW blue laser**, added entirely at runtime —
no Unity project, no Blueprinter `.nobp` bundle. The DLL clones the game's own `UnitDefinition` + prefab out of
`Encyclopedia.Lookup`, restamps name/key/cost, and re-tunes every `Laser` on the cloned prefab.

Built in the style of the reference mod [MRB-100 Centurion](https://github.com/baanish/MRB-100-Centurion):
same netstandard2.1 BepInEx plugin shape, same named `PatchTarget` table (a game update that moves a method turns the
feature off instead of crashing the plugin), same `[Mod]`-prefixed logger discipline.

## Files (`src/Lads200/`)

| File | Role |
|---|---|
| `Lads200.csproj` | Mirrors Centurion's csproj: `Private=false` references into your game's `Assembly-CSharp.dll`, `UnityEngine*.dll` and BepInEx's `BepInEx.dll` / `0Harmony.dll`. |
| `Lads200Plugin.cs` | `[BepInPlugin("razor.lads200", ...)]` entry point; config entries (`Enabled`, `UnitName`, `UnitKey`, `CostDollars=10000000`, `PowerKw=200`); installs Harmony patches; drives registration from `Update`. |
| `LadsClone.cs` | The heart: finds the stock MSV LADS in `Encyclopedia.Lookup`, `Object.Instantiate`-clones its definition *and* its prefab, sets `jsonKey="MSV_LADS_200"`, `unitName="MSV LADS 200"`, cost field (`emptyCost`/`cost`) = 10,000,000, sets each `Laser.power` 80→200 kW, tints the beam blue (serialized color field, else beam material `_BaseColor`/`_Color`), raises `PowerSupply.maxPower` to ≥200 kW, and adds the clone to the lookup + every typed Encyclopedia list its source is in. Clean rollback via `Disable()`. |
| `SpawnGuard.cs` | Retry loop (twice a second until the encyclopedia loads, gives up after 120 s) plus the postfix every spawn hook gets: if a networked spawn request for our key ever lands before registration finished, register on the spot (the Centurion repo's "Unit Type not found in Encyclopedia" failure mode, handled without holding the main menu). |
| `SpawnHooks.cs` | Patch discovery (v1.0.4): the v1.0.3 substring probe (formerly `SpawnProbe.cs`) found **0** spawn hooks because this game build's `Unit` declares no method containing "spawn"/"instantiate" — it dumped every Unit method into LogOutput.txt as a diagnostic. This file pins the real hooks read off that dump (`RegisterUnit`, `RegisterOnGridAsync`, `InitializeUnit`, plus the lifecycle callbacks `Awake`/`OnEnable`/`Start`/`OnStartServer`), validates each at runtime (single, non-special, void-returning; parameter-carrying ones must take a reference type) and patches only what resolves; if nothing resolves it dumps the method list again so the next build can re-pin. Field report v1.0.4: this build resolves exactly `Unit.Awake`, `Unit.OnEnable`, `Unit.OnStartServer` (the three lifecycle callbacks — the spawn-data candidates are overloaded/non-void and are skipped); that is sufficient since all three run once per spawned unit, and the log line now also names which candidates were skipped. |
| `ModLog.cs`, `PatchTarget.cs` | Trimmed copies of Centurion's logging + patch-installation helpers. |

## Build on your machine

1. Install [BepInEx 5](https://bepinex.io) into Nuclear Option if you haven't.
2. Point the build at your install (either works):
   ```powershell
   dotnet build src/Lads200/Lads200.csproj -c Release -p:GameDir="D:\SteamLibrary\steamapps\common\Nuclear Option"
   # or:  $env:NUCLEAR_OPTION_DIR="D:\...\Nuclear Option"; dotnet build src/Lads200/Lads200.csproj -c Release
   ```
3. Copy `src/Lads200/bin/Release/netstandard2.1/Lads200.dll` into `<GameDir>\BepInEx\plugins\`.
4. Launch, start any mission/sandbox: check `BepInEx\LogOutput.log` for
   `[Lads200] registered 'MSV LADS 200' (key MSV_LADS_200) ... cost $10,000,000, laser 200 kW blue`.
   The unit then appears next to the MSV LADS in the build/encyclopedia menus.

Requires .NET SDK 6+ (`dotnet build` only needs it; the DLL runs on Mono inside the game).

## If compilation complains about a member name

The code uses reflection with candidate lists (`emptyCost|cost`, `power|powerKW`, `maxPower`, `beamColor|laserColor|color`)
because exact private names can move between updates. Verified against the Centurion repo: `UnitDefinition.jsonKey`,
`.unitName`, `WeaponMount.emptyCost`, `Laser.power`, `PowerSupply` fields, `Encyclopedia.Lookup`, `Encyclopedia.i.aircraft`.
To confirm the rest on your machine:

```powershell
dotnet tool install -g ilspycmd
ilspycmd "<GameDir>\NuclearOption_Data\Managed\Assembly-CSharp.dll" -t Laser
ilspycmd "<GameDir>\NuclearOption_Data\Managed\Assembly-CSharp.dll" -t UnitDefinition
```

Add any renamed field to the matching candidate array in `LadsClone.cs` and rebuild.

## Notes / honest limits

- The stock MSV LADS keeps its 80 kW green laser untouched — we clone, never edit, the original prefab.
- Multiplayer: both peers need the DLL (like any NO mod). Net IDs are assigned by Mirage at spawn, so a runtime-registered
  prefab behaves like a Blueprinter asset as long as registration precedes spawning — which the retry loop + SpawnGuard guarantee.
- Icons/portraits still show the stock MSV LADS art (they ride along in the cloned definition).
