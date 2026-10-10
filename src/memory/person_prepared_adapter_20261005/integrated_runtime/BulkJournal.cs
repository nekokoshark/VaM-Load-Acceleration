// ORIGINAL per-key detach/per-loop-step adapter, including direct UnregisterAll callers.
using System;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using UnityEngine;
using Vam.PersonPrepared.SpecV2;

namespace VaM.PersonPrepared.Runtime
{
    internal sealed class BulkKey
    {
        internal string Key, Bundle;
        internal int Count, Step;
        internal OwnedPrefab[] Owned;
        internal bool CountRemoved, CacheRemoved;
    }
    internal sealed class BulkCall
    {
        internal SuperController Controller;
        internal readonly Dictionary<string, BulkKey> Keys = new Dictionary<string, BulkKey>();
        internal BulkKey Current;
        internal int OwnedSteps, LegacySteps, UnknownSteps;
    }
    internal static class BulkJournal
    {
        internal static BulkCall Active;
        internal static int LastOwnedSteps, LastLegacySteps, LastUnknownSteps;
        private static SuperController pendingReset;
        private static bool executingReset;
        internal static long ResetRequests, ResetExecutions;
        internal static bool BeforeReset(SuperController __instance)
        {
            if (executingReset) return true;
            SceneHooks.CancelController(__instance);
            if (OwnerHost.Units.Count == 0 && OwnerHost.Creators == 0 && StableDrain.Shared.Pending == 0) return true;
            OwnerHost.Accepting = false;
            if (!ReferenceEquals(pendingReset, null) && !ReferenceEquals(pendingReset, __instance)) throw new InvalidOperationException("Different controller reset pending");
            if (ReferenceEquals(pendingReset, null)) { pendingReset = __instance; ResetRequests++; }
            // All old nonpublished requests must be cancelled by the transaction adapter before this gate opens.
            return false;
        }
        internal static void DrainReset()
        {
            if (ReferenceEquals(pendingReset, null) || executingReset || Active != null || OwnerHost.Creators != 0
                || StableDrain.Shared.Pending != 0 || StableDrain.Shared.OwnershipAdmissionStopped) return;
            SuperController sc = pendingReset;
            foreach (OwnedPrefab unit in OwnerHost.Units.Values)
                if (!ReferenceEquals(unit.Atom, null) && ReferenceEquals(unit.Controller, sc)) OwnerHost.CaptureBarrier(unit);
            pendingReset = null; executingReset = true;
            try { ResetExecutions++; sc.HardReset(); }
            finally { executingReset = false; }
        }
        internal static void Begin(SuperController __instance, out BulkCall __state)
        {
            __state = null;
            if (OwnerHost.Creators != 0 || StableDrain.Shared.Pending != 0)
                throw new InvalidOperationException("Direct bulk called with owned producer/operation still active");
            bool owns = false;
            foreach (OwnedPrefab unit in OwnerHost.Units.Values) if (ReferenceEquals(unit.Controller, __instance)) owns = true;
            if (!owns) return;
            if (Active != null || OwnerHost.Creators != 0 || StableDrain.Shared.Pending != 0)
                throw new InvalidOperationException("Bulk ownership barrier not closed");
            OwnerHost.Accepting = false;
            var call = new BulkCall { Controller = __instance };
            var counts = OriginalBackend.Counts(__instance);
            var assets = (Dictionary<string, SuperController.AtomAsset>)OriginalBackend.AssetsField.GetValue(__instance);
            foreach (SuperController.AtomAsset asset in assets.Values)
            {
                string key = asset.assetBundleName + ":" + asset.assetName; if (call.Keys.ContainsKey(key)) continue;
                int count; if (counts == null || !counts.TryGetValue(key, out count)) continue;
                var units = new List<OwnedPrefab>();
                foreach (OwnedPrefab unit in OwnerHost.Units.Values)
                {
                    Observer status = unit.Protocol.Read();
                    if (!ReferenceEquals(unit.Controller, __instance) || unit.Key != key || status.Stage != ReceiptStage.RegisteredPrefab) continue;
                    if (status.Phase != UnitPhase.Ready && status.Phase != UnitPhase.Bound && status.Phase != UnitPhase.Retiring)
                        throw new InvalidOperationException("Unsettled bulk receipt");
                    LoadedAssetBundle actual;
                    if (!OriginalBackend.Tracked().TryGetValue(unit.Bundle, out actual) || !ReferenceEquals(actual, unit.Wrapper))
                        throw new InvalidOperationException("Bulk incarnation changed");
                    if (!ReferenceEquals(unit.Atom, null)) OwnerHost.CaptureBarrier(unit);
                    units.Add(unit);
                }
                if (units.Count > count || count <= 0) throw new InvalidOperationException("Invalid bulk N/K");
                call.Keys.Add(key, new BulkKey { Key = key, Bundle = asset.assetBundleName, Count = count, Owned = units.ToArray() });
            }
            foreach (OwnedPrefab unit in OwnerHost.Units.Values)
                if (ReferenceEquals(unit.Controller, __instance) && unit.Protocol.Read().Stage == ReceiptStage.RegisteredPrefab && !call.Keys.ContainsKey(unit.Key))
                    throw new InvalidOperationException("Owned key missing from bulk routing");
            __state = call; Active = call;
        }
        internal static bool ReadCount(Dictionary<string, int> table, string key, out int value)
        {
            bool found = table.TryGetValue(key, out value);
            if (Active != null)
            {
                BulkKey snapshot;
                if (found && (!Active.Keys.TryGetValue(key, out snapshot) || snapshot.Count != value))
                    throw new InvalidOperationException("Bulk input changed before detach");
            }
            return found;
        }
        internal static bool RemoveCount(Dictionary<string, int> table, string key)
        {
            bool removed = table.Remove(key);
            if (Active != null && removed) { Active.Current = Active.Keys[key]; Active.Current.CountRemoved = true; }
            return removed;
        }
        internal static bool RemoveCache(Dictionary<string, GameObject> table, string key)
        {
            bool removed = table.Remove(key);
            if (Active != null && Active.Current != null && Active.Current.Key == key) Active.Current.CacheRemoved = removed;
            return removed;
        }
        internal static void UnloadStep(string bundle)
        {
            if (Active == null) { AssetBundleManager.UnloadAssetBundle(bundle); return; }
            BulkKey key = Active.Current;
            if (key == null || !key.CountRemoved || !key.CacheRemoved || key.Bundle != bundle || key.Step >= key.Count)
                throw new InvalidOperationException("Bulk step lacks detach witness");
            int step = key.Step++; // Claim BEFORE original native release can reenter.
            try
            {
                if (step < key.Owned.Length)
                {
                    OwnedPrefab unit = key.Owned[step]; unit.Protocol.WitnessBulkDetach(); unit.OwnerVersion = unit.Protocol.Read().Version;
                    unit.Owner = AtomOwner.Retiring; unit.Prefab = null; unit.CacheIdentity = null; OwnerHost.Queue(unit); Active.OwnedSteps++;
                }
                else { AssetBundleManager.UnloadAssetBundle(bundle); Active.LegacySteps++; }
            }
            catch { Active.UnknownSteps++; throw; }
        }
        internal static Exception End(Exception __exception, BulkCall __state)
        {
            if (__state == null) return __exception;
            try
            {
                Exception error = __exception;
                if (error == null)
                {
                    foreach (BulkKey key in __state.Keys.Values)
                        if (key.CountRemoved && key.Step != key.Count) { error = new InvalidOperationException("Bulk loop incomplete"); break; }
                }
                if (error != null)
                {
                    foreach (BulkKey key in __state.Keys.Values) foreach (OwnedPrefab unit in key.Owned)
                        OwnerHost.Quarantine(unit, error);
                }
                LastOwnedSteps = __state.OwnedSteps; LastLegacySteps = __state.LegacySteps; LastUnknownSteps = __state.UnknownSteps;
                return error;
            }
            finally { Active = null; }
        }
    }
}
