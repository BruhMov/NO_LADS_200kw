using System.Collections.Generic;
using BepInEx.Logging;

namespace Lads200;

/// <summary>
/// One "[Lads200]" prefixed line per key per second at most (the Centurion repo's CenturionLog, trimmed to what this mod needs):
/// registration is retry-driven, so without rate limiting the log would fill with identical lines every frame.
/// </summary>
public sealed class ModLog
{
    readonly ManualLogSource source;
    readonly HashSet<string> stageSeen = new();

    public ModLog(ManualLogSource source)
    {
        this.source = source;
    }

    public void Info(string message) => source.LogInfo(message);
    public void Warning(string message) => source.LogWarning(message);
    public void Error(string message) => source.LogError(message);

    /// <summary>Logs <paramref name="message"/> once per <paramref name="key"/> until <see cref="ResetStage"/> clears it.</summary>
    public void StageOnce(string key, string message)
    {
        if (stageSeen.Add(key))
        {
            source.LogInfo(message);
        }
    }

    public void ResetStage() => stageSeen.Clear();
}
