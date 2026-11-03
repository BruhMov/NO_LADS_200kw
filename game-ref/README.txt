Place the following reference assemblies here (from a Nuclear Option 0.34.x install):
  Assembly-CSharp.dll   (NuclearOption_Data/Managed/Assembly-CSharp.dll)
  UnityEngine.CoreModule.dll
  UnityEngine.dll
and BepInEx core:
  BepInEx.dll  0Harmony.dll  (BepInEx/core/)
The build script uses them as Private=false references, exactly like the Centurion repo's csproj uses GameDir paths.
