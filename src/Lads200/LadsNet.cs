using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Lads200;

/// <summary>
/// Gives the cloned prefab's NetworkIdentities fresh PrefabHashes, the way Blueprinter's PrefabHashAssigner does for
/// .nobp prefabs. Mirage spawns by hash: an Object.Instantiate copy of the stock LADS prefab would carry the stock
/// prefab's hash, and the network would resolve our spawn messages back to the stock 80 kW unit. The root hash is a
/// config value (Network.PrefabHash) so every peer in a multiplayer lobby picks the same one; children get root+i.
/// Stock hashes are collected first (Resources.FindObjectsOfTypeAll, exactly Blueprinter's CollectExistingHashes) so a
/// collision with a stock or other mod's prefab is detected and logged instead of silently corrupting spawns.
/// </summary>
static class LadsNet
{
    internal static int RootHash = 987654321;

    /// <summary>Assigns fresh, collision-checked hashes to every NetworkIdentity on the cloned prefab.</summary>
    public static void AssignFreshPrefabHashes(GameObject prefab, ModLog log)
    {
        if (GameTypes.NetworkIdentity == null)
        {
            log.Warning("Mirage.NetworkIdentity not found: prefab hashes not reassigned - multiplayer spawns may resolve to the stock MSV LADS");
            return;
        }

        var taken = new HashSet<int>();
        foreach (Object ni in Resources.FindObjectsOfTypeAll(GameTypes.NetworkIdentity))
        {
            if (ni == null) continue;
            if (GameReflect.GetMember(GameTypes.NetworkIdentity, ni, "PrefabHash") is int hash && hash != 0) taken.Add(hash);
        }

        // Root first (the spawn message carries the root identity's hash), then children top-down for determinism.
        var identities = new List<Component>();
        Collect(prefab.transform, GameTypes.NetworkIdentity, identities);

        int next = RootHash;
        int assigned = 0;
        foreach (Component identity in identities)
        {
            if (identity == null) continue;
            if (taken.Contains(next))
            {
                // Deterministic drift is still better than a collision, but peers must agree: tell the user to change
                // Network.PrefabHash in the config instead of relying on this.
                int free = next;
                while (taken.Contains(free)) free++;
                log.Warning($"prefab hash {next} already taken (another mod?); using {free}. Set Network.PrefabHash in " +
                            "razor.lads200.cfg to an unused value on every peer, or multiplayer spawns will disagree");
                next = free;
            }

            if (GameReflect.SetMember(GameTypes.NetworkIdentity, identity, "PrefabHash", next))
            {
                taken.Add(next);
                assigned++;
            }
            next++;
        }

        log.Info($"prefab hashes: {assigned} NetworkIdentity(ies) on the clone reassigned from {RootHash} " +
                 $"({taken.Count - assigned} existing hashes avoided)");
    }

    static void Collect(Transform t, Type type, List<Component> into)
    {
        Component c = t.GetComponent(type);
        if (c != null) into.Add(c);
        for (int i = 0; i < t.childCount; i++) Collect(t.GetChild(i), type, into);
    }
}
