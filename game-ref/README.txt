Optional build-time reference assemblies (the v2 build no longer needs Assembly-CSharp.dll):

  BepInEx/core/BepInEx.dll        (from the BepInEx 5 install in your game folder)
  BepInEx/core/0Harmony.dll       (same)
  NuclearOption_Data/Managed/UnityEngine.dll
  NuclearOption_Data/Managed/UnityEngine.CoreModule.dll

Copy them here from a Nuclear Option install with BepInEx 5, then build from src/Lads200 with
NUCLEAR_OPTION_DIR pointed here (or pass -p:GameDir=<this folder>). Any Unity 2022.3-era
UnityEngine.CoreModule.dll works too: the plugin only uses stable Unity APIs, and all game
types are resolved by name at runtime (GameReflect.cs), never at compile time.
