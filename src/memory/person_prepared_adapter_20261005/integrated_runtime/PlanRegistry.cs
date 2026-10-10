using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Vam.PersonPrepared.SpecV2;

namespace VaM.PersonPrepared.Runtime
{
    internal sealed class PersonPlan
    {
        internal readonly long Id;
        internal readonly int Index;
        internal readonly string Uid, Type;
        internal SuperController Controller;
        internal OwnedPrefab Unit;
        internal DrainHandle Request;
        internal Action Done;
        internal bool Publish = true, Creating, Completed, Failed, Notified;
        internal PersonPlan(long id, int index, string uid, string type, SuperController sc)
        { Id = id; Index = index; Uid = uid; Type = type; Controller = sc; }
    }
    internal static class PlanRegistry
    {
        private sealed class Transaction
        {
            internal SuperController Controller;
            internal readonly Dictionary<int, PersonPlan> Plans = new Dictionary<int, PersonPlan>();
            internal bool Publish = true;
        }
        private static readonly Dictionary<IEnumerator, Transaction> loads = new Dictionary<IEnumerator, Transaction>(new ReferenceIdentity<IEnumerator>());
        private static readonly Dictionary<IEnumerator, PersonPlan> tags = new Dictionary<IEnumerator, PersonPlan>(new ReferenceIdentity<IEnumerator>());
        private static long nextPlan, admission;
        internal static int LoadCount { get { return loads.Count; } }
        internal static int TagCount { get { return tags.Count; } }
        internal static bool IsManaged(IEnumerator load) { return loads.ContainsKey(load); }
        internal static int PendingPlans(IEnumerator load)
        { Transaction t; if (!loads.TryGetValue(load, out t) || !t.Publish) return 0; int n = 0; foreach (PersonPlan p in t.Plans.Values) if (p.Creating && !p.Completed) n++; return n; }
        internal static void Open()
        { admission = StableDrain.Shared.OpenAdmission(); OwnerHost.Accepting = true; }
        internal static void Close() { OwnerHost.Accepting = false; StableDrain.Shared.CloseAdmission(admission); }
        internal static void Begin(IEnumerator load, SuperController controller)
        {
            if (loads.ContainsKey(load)) throw new InvalidOperationException("Load already admitted");
            loads.Add(load, new Transaction { Controller = controller });
        }
        private static PersonPlan Plan(IEnumerator load, int index, string uid, string type)
        {
            Transaction t;
            if (!loads.TryGetValue(load, out t) || !t.Publish) throw new InvalidOperationException("Load not admitted");
            PersonPlan plan;
            if (t.Plans.TryGetValue(index, out plan))
            { if (plan.Uid != uid || plan.Type != type) throw new InvalidOperationException("Plan input changed"); return plan; }
            plan = new PersonPlan(checked(++nextPlan), index, uid, type, t.Controller); t.Plans.Add(index, plan); return plan;
        }
        private static List<string> Strings(IEnumerator load, string name)
        { return (List<string>)PlanHooks.Field(load.GetType(), name, typeof(List<string>)).GetValue(load); }
        internal static IEnumerator Preload(SuperController sc, SuperController.AtomAsset asset, Action<GameObject> callback, IEnumerator load)
        {
            int index = (int)PlanHooks.Field(callback.Target.GetType(), "capturedIdx", typeof(int)).GetValue(callback.Target);
            PersonPlan plan = Plan(load, index, Strings(load, "_newAtomUids")[index], Strings(load, "_newAtomTypes")[index]);
            if (!ReferenceEquals(plan.Controller, sc) || plan.Unit != null || plan.Request != null) throw new InvalidOperationException("Duplicate preload plan");
            return Prepare(plan, asset, callback);
        }
        private static IEnumerator Prepare(PersonPlan plan, SuperController.AtomAsset asset, Action<GameObject> callback)
        {
            try
            {
                // Preserve the original manager-ready yield before the first backend access.
                yield return (IEnumerator)typeof(SuperController).GetMethod("AssetManagerReady", OriginalBackend.All).Invoke(plan.Controller, null);
                yield return Acquire(plan, asset);
                if (plan.Publish)
                {
                    Action<GameObject> notify = callback; callback = null;
                    if (plan.Unit == null && !plan.Failed) plan.Controller.Error("Failed to prepare Atom " + asset.assetName, true, true);
                    // Commit prepared ownership before reentrant notification. Failed current plans still become ready/null.
                    if (notify != null) notify(plan.Unit == null || plan.Failed ? null : plan.Unit.Prefab);
                }
            }
            finally { callback = null; if (!plan.Publish) ReleaseUnpublished(plan); }
        }
        private static IEnumerator Acquire(PersonPlan plan, SuperController.AtomAsset asset)
        {
            if (!plan.Publish || plan.Failed) yield break;
            if (!OwnerHost.Accepting || StableDrain.Shared.OwnershipAdmissionStopped) { plan.Failed = true; yield break; }
            var work = new AsyncWork(plan.Controller, asset);
            DrainHandle handle = StableDrain.Shared.Reserve(admission, work); plan.Request = handle;
            bool transferred = false;
            try
            {
                StableDrain.Shared.Start(handle);
                while (plan.Publish && handle.Phase == DrainPhase.NativeInFlight) yield return null;
                if (!plan.Publish) yield break;
                if (handle.Phase == DrainPhase.Prepared)
                { var received = (AsyncWork)StableDrain.Shared.TakePrepared(handle); plan.Unit = received.Unit; transferred = true; }
                else if (handle.Phase == DrainPhase.Quarantined) plan.Failed = true;
            }
            finally { if (!transferred) StableDrain.Shared.Cancel(handle); plan.Request = null; }
        }
        internal static IEnumerator StartCreate(IEnumerator load, string uid, string type, IEnumerator iterator, Action done)
        {
            int ordinal = (int)PlanHooks.Field(load.GetType(), "_pp2", typeof(int)).GetValue(load);
            var indices = (List<int>)PlanHooks.Field(load.GetType(), "_personIndices", typeof(List<int>)).GetValue(load);
            PersonPlan plan = Plan(load, indices[ordinal], uid, type);
            if (plan.Creating || plan.Completed) throw new InvalidOperationException("Plan consumed twice");
            plan.Creating = true; plan.Done = done; return Tag(iterator, plan, true);
        }
        internal static IEnumerator Child(IEnumerator parent, IEnumerator child)
        { PersonPlan plan; return tags.TryGetValue(parent, out plan) ? Tag(child, plan, false) : child; }
        internal static IEnumerator ReadyLoader(IEnumerator parent, IEnumerator loader, SuperController.AtomAsset asset)
        { PersonPlan plan; return tags.TryGetValue(parent, out plan) ? Demand(plan, loader, asset) : loader; }
        private static IEnumerator Demand(PersonPlan plan, IEnumerator loader, SuperController.AtomAsset asset)
        {
            bool started = false;
            try
            {
                if (plan.Unit == null && !plan.Failed)
                {
                    yield return (IEnumerator)typeof(SuperController).GetMethod("AssetManagerReady", OriginalBackend.All).Invoke(plan.Controller, null);
                    yield return Acquire(plan, asset);
                }
                if (plan.Publish && !plan.Failed && plan.Unit != null) { started = true; yield return Tag(loader, plan, false); }
            }
            finally { if (!started && loader is IDisposable) ((IDisposable)loader).Dispose(); }
        }
        private static IEnumerator Tag(IEnumerator iterator, PersonPlan plan, bool terminal)
        { tags.Add(iterator, plan); return new Tagged(iterator, plan, terminal); }
        private sealed class Tagged : IEnumerator, IDisposable
        {
            private IEnumerator iterator;
            private PersonPlan plan;
            private readonly bool terminal;
            internal Tagged(IEnumerator i, PersonPlan p, bool t) { iterator = i; plan = p; terminal = t; }
            public object Current { get { return iterator.Current; } }
            public void Reset() { throw new NotSupportedException(); }
            public bool MoveNext()
            {
                if (iterator == null) return false;
                try { if (plan.Publish && iterator.MoveNext()) return true; }
                catch (Exception error)
                {
                    plan.Failed = true; StableDrain.Shared.StopForOwnershipFault(error);
                    Debug.LogError("PersonPrepared plan " + plan.Id + ": " + error.GetType().FullName + ": " + error.Message);
                }
                Dispose(); return false;
            }
            public void Dispose()
            {
                IEnumerator old = iterator; if (old == null) return; iterator = null;
                try { if (old is IDisposable) ((IDisposable)old).Dispose(); }
                finally
                {
                    tags.Remove(old);
                    FieldInfo flag = null;
                    // Exact stem lookup also supports generated numeric suffix changes.
                    foreach (FieldInfo field in old.GetType().GetFields(OriginalBackend.All))
                        if (field.FieldType == typeof(AsyncFlag) && field.Name.StartsWith("<loadIconFlag>", StringComparison.Ordinal)) flag = field;
                    if (flag != null) { var f = (AsyncFlag)flag.GetValue(old); if (f != null) f.Raise(); }
                    if (terminal)
                    {
                        plan.Completed = true; ReleaseUnpublished(plan); Notify(plan);
                        FieldInfo callback = old.GetType().GetField("onDone", OriginalBackend.All); if (callback != null) callback.SetValue(old, null);
                    }
                    plan = null;
                }
            }
        }
        internal static GameObject Cache(SuperController sc, string bundle, string asset, IEnumerator iterator)
        {
            PersonPlan plan;
            if (!tags.TryGetValue(iterator, out plan)) return sc.GetCachedPrefab(bundle, asset);
            if (!plan.Publish || plan.Unit == null || plan.Unit.Bundle != bundle || plan.Unit.Asset != asset || plan.Unit.Protocol.Read().Phase != UnitPhase.Ready)
                throw new InvalidOperationException("Wrong prepared receipt");
            return plan.Unit.Prefab; // No second GetCachedPrefab/acquire.
        }
        internal static Transform Add(SuperController sc, Atom atom, string uid, bool user, bool select, bool focus, bool instantiate, IEnumerator iterator)
        {
            PersonPlan plan;
            if (!tags.TryGetValue(iterator, out plan)) return sc.AddAtom(atom, uid, user, select, focus, instantiate);
            if (!instantiate) { ReleaseUnpublished(plan); return sc.AddAtom(atom, uid, user, select, focus, false); }
            if (plan.Unit == null) return sc.AddAtom(atom, uid, user, select, focus, instantiate);
            return OwnerHost.AddOwned(plan.Unit, uid, user, select, focus);
        }
        private static void ReleaseUnpublished(PersonPlan plan)
        {
            if (plan.Unit != null && plan.Unit.Protocol.Read().Phase == UnitPhase.Ready) OwnerHost.Discard(plan.Unit);
            plan.Unit = null; // A bound unit belongs exclusively to the Atom/Pool sidecar.
        }
        private static void Notify(PersonPlan plan)
        {
            if (plan.Notified) return; plan.Notified = true;
            Action done = plan.Done; plan.Done = null; if (plan.Publish && done != null) done();
        }
        internal static void Done(Action callback, IEnumerator iterator)
        { PersonPlan plan; if (tags.TryGetValue(iterator, out plan)) Notify(plan); else if (callback != null) callback(); }
        internal static void Cancel(IEnumerator load)
        {
            Transaction t; if (!loads.TryGetValue(load, out t)) return; t.Publish = false;
            foreach (PersonPlan plan in t.Plans.Values)
            {
                plan.Publish = false; plan.Done = null;
                if (plan.Request != null) StableDrain.Shared.Cancel(plan.Request);
                ReleaseUnpublished(plan);
            }
        }
        internal static void CancelController(SuperController sc)
        {
            var snapshot = new List<IEnumerator>(); foreach (var pair in loads) if (ReferenceEquals(pair.Value.Controller, sc)) snapshot.Add(pair.Key);
            foreach (IEnumerator load in snapshot) { Cancel(load); End(load); }
        }
        internal static void End(IEnumerator load)
        {
            Transaction t; if (!loads.TryGetValue(load, out t)) return;
            foreach (PersonPlan plan in t.Plans.Values)
            { if (plan.Request != null) StableDrain.Shared.Cancel(plan.Request); ReleaseUnpublished(plan); plan.Done = null; plan.Controller = null; }
            foreach (FieldInfo field in load.GetType().GetFields(OriginalBackend.All))
            {
                if (!field.Name.StartsWith("<>8__", StringComparison.Ordinal)) continue;
                object closure = field.GetValue(load); if (closure == null) continue;
                FieldInfo prepared = closure.GetType().GetField("_personPrefabs", OriginalBackend.All);
                if (prepared != null) { var dictionary = prepared.GetValue(closure) as Dictionary<int, GameObject>; if (dictionary != null) dictionary.Clear(); }
            }
            t.Plans.Clear(); t.Controller = null; loads.Remove(load);
        }
    }
}
