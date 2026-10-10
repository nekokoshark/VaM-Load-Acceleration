// Cold-assembly Atom ownership/retirement. No scene callback or hot-payload delegate is retained.
using System;
using System.Collections.Generic;
using System.Reflection;
using AssetBundles;
using UnityEngine;
using Vam.PersonPrepared.SpecV2;

namespace VaM.PersonPrepared.Runtime
{
    internal enum AtomOwner { Scene, Pool, Retiring, Quarantined }
    internal sealed class OwnedPrefab
    {
        internal readonly long Id;
        internal SuperController Controller;
        internal readonly string Bundle, Asset, Key;
        internal readonly PreparedUnitProtocol Protocol;
        internal LoadedAssetBundle Wrapper;
        internal GameObject Prefab;
        internal GameObject CacheIdentity;
        internal Atom Atom;
        internal long OwnerVersion;
        internal int PrefabWrites, BundleWrites, ReturnWrites;
        internal int OperationWrites;
        internal AssetBundleLoadAssetOperation Operation;
        internal BundleReceipt Receipt;
        internal AtomOwner Owner;
        internal bool Queued, DestroyRequested, AtomDestroyed, BarrierCaptured;
        internal DAZCharacterRun[] Runs;
        internal bool[] RunsDestroyed;
        internal RegistrationProbe Registrations;
        internal OwnedPrefab(long id, SuperController sc, string bundle, string asset)
        {
            Id = id; Controller = sc; Bundle = bundle; Asset = asset; Key = bundle + ":" + asset;
            Protocol = new PreparedUnitProtocol(id); Protocol.BeginAcquisition();
        }
    }
    internal sealed class ReferenceIdentity<T> : IEqualityComparer<T> where T : class
    {
        public bool Equals(T a, T b) { return ReferenceEquals(a, b); }
        public int GetHashCode(T value) { return System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value); }
    }
    internal static class OwnerHost
    {
        internal static readonly Dictionary<long, OwnedPrefab> Units = new Dictionary<long, OwnedPrefab>();
        private static readonly Dictionary<Atom, OwnedPrefab> instances = new Dictionary<Atom, OwnedPrefab>(new ReferenceIdentity<Atom>());
        private static readonly List<OwnedPrefab> retiring = new List<OwnedPrefab>();
        private static long nextId;
        internal static bool Accepting;
        internal static int Creators;
        private static bool pumping;
        internal static int InstanceCount { get { return instances.Count; } }
        internal static int RetirementCount { get { return retiring.Count; } }
        private sealed class AddCall
        {
            internal OwnedPrefab Unit;
            internal Atom Prefab;
            internal bool Claimed;
            internal AddCall Parent;
        }
        [ThreadStatic] private static AddCall pending;
        [ThreadStatic] private static AddCall active;

        internal static OwnedPrefab AcquireCached(SuperController sc, string bundle, string asset)
        {
            OwnedPrefab unit = Reserve(sc, bundle, asset);
            try { unit.Receipt = BundleReceipts.Reserve(unit); }
            catch { unit.Protocol.WitnessNoUnitAcquired(); Drop(unit); throw; }
            try
            {
                ReadCached(unit);
                if (unit.Prefab == null) { unit.Protocol.WitnessNoUnitAcquired(); BundleReceipts.NoAcquisition(unit); Drop(unit); return null; }
                CommitCached(unit); return unit;
            }
            catch (Exception error) { Quarantine(unit, error); throw; }
        }
        private static OwnedPrefab Reserve(SuperController sc, string bundle, string asset)
        {
            StableDrain.Shared.Main();
            if (!Accepting || StableDrain.Shared.OwnershipAdmissionStopped) throw new InvalidOperationException("Ownership admission closed");
            OriginalBackend.RequireCanonical(bundle);
            var unit = new OwnedPrefab(checked(nextId + 1), sc, bundle, asset);
            Units.Add(unit.Id, unit); nextId = unit.Id;
            return unit;
        }
        private static void ReadCached(OwnedPrefab unit)
        {
            using (new ReturnWitness.Scope(unit, "GetCachedPrefab")) unit.Prefab = unit.Controller.GetCachedPrefab(unit.Bundle, unit.Asset);
            if (unit.Prefab == null && unit.PrefabWrites == 0 && unit.BundleWrites == 0) return;
            if (unit.Prefab == null || unit.PrefabWrites != 1 || unit.BundleWrites != 1 || unit.Wrapper == null || unit.Wrapper.m_AssetBundle == null)
                throw new InvalidOperationException("Cached acquisition receipt incomplete");
        }
        private static void CommitCached(OwnedPrefab unit)
        {
            BundleReceipts.Commit(unit);
            unit.CacheIdentity = unit.Prefab; unit.Protocol.WitnessCacheAcquisitionCommitted();
        }
        internal static void BeginAsync(SuperController sc, SuperController.AtomAsset asset, out OwnedPrefab unit)
        {
            unit = Reserve(sc, asset.assetBundleName, asset.assetName);
            try { unit.Receipt = BundleReceipts.Reserve(unit); }
            catch { unit.Protocol.WitnessNoUnitAcquired(); Drop(unit); throw; }
            try
            {
                // Reserve dependency receipt and drain slot BEFORE the first backend acquire.
                ReadCached(unit);
                if (unit.Prefab != null)
                { BundleReceipts.Commit(unit); unit.CacheIdentity = unit.Prefab; unit.Protocol.WitnessCacheAcquisitionCommitted(); return; }
                using (new ReturnWitness.Scope(unit, "LoadAssetAsync"))
                    unit.Operation = AssetBundleManager.LoadAssetAsync(unit.Bundle, unit.Asset, typeof(GameObject));
                if (unit.BundleWrites != 1 || unit.OperationWrites != 1 || unit.Wrapper == null || unit.Operation == null)
                    throw new InvalidOperationException("Cold acquisition receipt incomplete");
                BundleReceipts.Commit(unit); unit.Protocol.WitnessColdAcquisitionCommitted();
            }
            catch (Exception error) { Quarantine(unit, error); throw; }
        }
        internal static void CompleteCold(OwnedPrefab unit)
        {
            if (unit.Protocol.Read().Phase != UnitPhase.NativeInFlight || !unit.Operation.IsDone()) return;
            BundleReceipt r = unit.Receipt;
            if ((r.Create != null && !r.Create.isDone) || (r.Download != null && !r.DownloadDone)) return;
            LoadedAssetBundle actual;
            if (!OriginalBackend.Tracked().TryGetValue(unit.Bundle, out actual) || !ReferenceEquals(actual, unit.Wrapper))
                throw new InvalidOperationException("Operation root incarnation changed");
            unit.Prefab = unit.Operation.GetAsset<GameObject>();
            bool usable = unit.Prefab != null && unit.Prefab.GetComponent(typeof(Atom)) != null;
            unit.Protocol.WitnessNativeTerminal(usable);
            if (unit.Protocol.Read().Phase == UnitPhase.NeedsRegistration)
            {
                using (new ReturnWitness.Scope(unit, "RegisterPrefab")) unit.Controller.RegisterPrefab(unit.Bundle, unit.Asset, unit.Prefab);
                if (unit.PrefabWrites != 1 || unit.BundleWrites != 1) throw new InvalidOperationException("Registration promotion unproved");
                unit.CacheIdentity = OriginalBackend.Cache(unit.Controller)[unit.Key];
                if (!ReferenceEquals(unit.CacheIdentity, unit.Prefab)) throw new InvalidOperationException("Registered prefab identity changed");
                unit.Protocol.WitnessRegistrationCommitted();
            }
            unit.Operation = null;
            if (unit.Protocol.Read().Phase == UnitPhase.Retiring) Queue(unit);
        }
        internal static Transform AddOwned(OwnedPrefab unit, string uid, bool user, bool select, bool focus)
        {
            StableDrain.Shared.Main();
            if (!Accepting || StableDrain.Shared.OwnershipAdmissionStopped || unit.Protocol.Read().Phase != UnitPhase.Ready)
                throw new InvalidOperationException("Prepared creation not admitted: accepting=" + Accepting + ", stopped="
                    + StableDrain.Shared.OwnershipAdmissionStopped + ", phase=" + unit.Protocol.Read().Phase + ", prior=" + StableDrain.Shared.LastFaultMessage);
            Atom prefab = (Atom)unit.Prefab.GetComponent(typeof(Atom));
            if (prefab == null) { Discard(unit); throw new InvalidOperationException("Prefab missing Atom"); }
            var call = new AddCall { Unit = unit, Prefab = prefab, Parent = pending };
            unit.Protocol.BeginCreation(unit.Id); Creators++;
            pending = call; Transform result = null;
            try
            {
                result = unit.Controller.AddAtom(prefab, uid, user, select, focus, true);
                if (unit.Protocol.Read().Phase == UnitPhase.Creating)
                {
                    if (result != null) throw new InvalidOperationException("Clone binding not witnessed");
                    unit.Protocol.AbandonCreation(unit.Id, 0, true); Queue(unit);
                }
                return result;
            }
            catch (Exception error) { Quarantine(unit, error); throw; }
            finally
            {
                pending = call.Parent;
                unit.Protocol.EndProducer(unit.Id); Creators--;
            }
        }
        internal static void EnterAdd(Atom atom, bool instantiate, out object __state)
        {
            StableDrain.Shared.Main();
            AddCall previous = active; __state = previous; active = null;
            if (pending != null && !pending.Claimed && instantiate && ReferenceEquals(atom, pending.Prefab))
            { pending.Claimed = true; active = pending; }
        }
        internal static Exception ExitAdd(Exception __exception, object __state)
        { active = (AddCall)__state; return __exception; }
        internal static void ParentClone(Transform clone, Transform parent, bool stays)
        {
            if (active != null)
            {
                OwnedPrefab unit = active.Unit; Atom atom = (Atom)clone.GetComponent(typeof(Atom));
                if (atom == null || instances.ContainsKey(atom)) throw new InvalidOperationException("Invalid owned clone");
                // Allocate sidecar before publication. Unity Awake/OnEnable may already have run.
                instances.Add(atom, unit); unit.Atom = atom;
                unit.OwnerVersion = unit.Protocol.BindInstance(unit.Id, unit.Id); unit.Owner = AtomOwner.Scene;
                unit.Prefab = null;
            }
            clone.SetParent(parent, stays);
        }
        internal static OwnedPrefab Find(Atom atom)
        { OwnedPrefab unit; return !ReferenceEquals(atom, null) && instances.TryGetValue(atom, out unit) ? unit : null; }
        internal static void ToPool(Atom atom)
        {
            OwnedPrefab unit = Find(atom); if (unit == null) return;
            unit.OwnerVersion = unit.Protocol.TransferBoundOwner(unit.OwnerVersion); unit.Owner = AtomOwner.Pool;
        }
        internal static void PoolFlag(Atom atom, bool value)
        {
            OwnedPrefab unit = Find(atom);
            if (!value && unit != null)
            { unit.OwnerVersion = unit.Protocol.TransferBoundOwner(unit.OwnerVersion); unit.Owner = AtomOwner.Scene; }
            atom.inPool = value; // Preserve the original write and its position before SetActive(true).
        }
        internal static void CaptureBarrier(OwnedPrefab unit)
        {
            if (unit.BarrierCaptured) return;
            Component[] components = unit.Atom.GetComponentsInChildren(typeof(DAZCharacterRun), true);
            var runs = new DAZCharacterRun[components.Length];
            for (int i = 0; i < components.Length; i++) runs[i] = (DAZCharacterRun)components[i];
            var completed = new bool[runs.Length];
            RegistrationProbe registrations = new RegistrationProbe(unit.Controller, unit.Atom);
            unit.Runs = runs; unit.RunsDestroyed = completed; unit.Registrations = registrations; unit.BarrierCaptured = true;
        }
        internal static void DestroyObject(UnityEngine.Object target)
        {
            GameObject go = target as GameObject; Atom atom = go == null ? null : (Atom)go.GetComponent(typeof(Atom));
            OwnedPrefab unit = Find(atom);
            if (unit != null)
            {
                try
                {
                    CaptureBarrier(unit);
                    if (unit.Protocol.Read().Phase == UnitPhase.Bound && !unit.Protocol.RequestAtomRetirement(unit.OwnerVersion))
                        throw new InvalidOperationException("Retirement owner changed");
                    unit.Owner = AtomOwner.Retiring; Queue(unit);
                    UnityEngine.Object.Destroy(target); unit.DestroyRequested = true;
                }
                catch (Exception error) { Quarantine(unit, error); throw; }
            }
            else UnityEngine.Object.Destroy(target);
        }
        internal static Atom AtomForDestroy(UnityEngine.Object target)
        { GameObject go = target as GameObject; return go == null ? null : (Atom)go.GetComponent(typeof(Atom)); }
        internal static void UnregisterAtExit(SuperController sc, string bundle, string asset, Atom atom)
        {
            OwnedPrefab unit = Find(atom);
            if (unit == null) { sc.UnregisterPrefab(bundle, asset); return; }
            if (!ReferenceEquals(sc, unit.Controller) || bundle != unit.Bundle || asset != unit.Asset || !unit.DestroyRequested)
            { Quarantine(unit, new InvalidOperationException("Destructive release mapping changed")); return; }
            // The private sidecar owns this return even when loadedFromBundle was still false.
        }
        internal static void DestroyedAtom(Atom atom, Exception error)
        {
            OwnedPrefab unit = Find(atom); if (unit == null) return;
            if (error != null) { Quarantine(unit, error); return; }
            if (!unit.BarrierCaptured)
            { Quarantine(unit, new InvalidOperationException("Unobserved external Atom destruction")); return; }
            unit.AtomDestroyed = true;
        }
        internal static void DestroyedRun(DAZCharacterRun run, Exception error)
        {
            foreach (OwnedPrefab unit in Units.Values)
            {
                if (unit.Runs == null) continue;
                for (int i = 0; i < unit.Runs.Length; i++)
                    if (ReferenceEquals(unit.Runs[i], run))
                    {
                        if (error != null) Quarantine(unit, error);
                        else unit.RunsDestroyed[i] = true;
                    }
            }
        }
        internal static void Discard(OwnedPrefab unit)
        { if (unit.Protocol.RetireUnused(unit.Protocol.Read().Version)) { Queue(unit); OriginalBackend.Return(unit); } }
        internal static void Queue(OwnedPrefab unit)
        { if (!unit.Queued) { retiring.Add(unit); unit.Queued = true; } }
        internal static void Pump()
        {
            StableDrain.Shared.Main(); if (pumping) return; pumping = true;
            try
            {
                StableDrain.Shared.Pump();
                for (int i = retiring.Count - 1; i >= 0; i--)
                {
                    if (i >= retiring.Count) continue; OwnedPrefab unit = retiring[i];
                    if (unit.Protocol.Read().Phase != UnitPhase.Retiring) continue;
                    if (unit.Atom != null || (!ReferenceEquals(unit.Atom, null) && !unit.AtomDestroyed)) continue;
                    if (!ReferenceEquals(unit.Atom, null))
                    {
                        bool complete = true;
                        for (int j = 0; j < unit.Runs.Length; j++) if (!unit.RunsDestroyed[j] || unit.Runs[j] != null) complete = false;
                        if (!complete || !unit.Registrations.Cleared()) continue;
                        unit.Protocol.WitnessDestructionAndConsumersComplete();
                    }
                    OriginalBackend.Return(unit);
                }
                BulkJournal.DrainReset();
            }
            finally { pumping = false; }
        }
        internal static void Quarantine(OwnedPrefab unit, Exception error)
        {
            if (unit.Protocol.Read().Phase == UnitPhase.Quarantined) return;
            unit.Protocol.Quarantine(); unit.Owner = AtomOwner.Quarantined; Accepting = false;
            if (!StableDrain.Shared.OwnershipAdmissionStopped) StableDrain.Shared.StopForOwnershipFault(error);
        }
        internal static void Drop(OwnedPrefab unit)
        {
            if (!ReferenceEquals(unit.Atom, null)) instances.Remove(unit.Atom);
            Units.Remove(unit.Id); if (unit.Queued) retiring.Remove(unit);
            unit.Controller = null; unit.Prefab = null; unit.CacheIdentity = null; unit.Atom = null; unit.Wrapper = null;
            unit.Operation = null; unit.Receipt = null;
            unit.Runs = null; unit.RunsDestroyed = null; unit.Registrations = null;
        }
    }
}
