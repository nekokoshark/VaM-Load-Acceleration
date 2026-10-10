using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VaM.PersonPrepared.Runtime
{
    // Only scene request/root lifetime lives here. Published Atom units stay in OwnerHost.
    internal static class SceneHooks
    {
        private sealed class Entry
        {
            internal SuperController Controller;
            internal Entry Parent;
            internal long Accepted;
            internal bool Attached;
        }
        [ThreadStatic] private static Entry entry;
        private static long nextId;
        private static readonly List<Root> roots = new List<Root>();
        private static readonly Dictionary<SuperController, Root> latest = new Dictionary<SuperController, Root>(new ReferenceIdentity<SuperController>());
        internal static int Count { get { return roots.Count; } }
        internal static long AcceptedCount;
        internal static bool HasAcceptedEntry { get { return entry != null && entry.Accepted != 0; } }
        private static void Enter(SuperController __instance, out Entry __state)
        { __state = new Entry { Controller = __instance, Parent = entry }; entry = __state; }
        private static Exception Exit(Exception __exception, Entry __state)
        { entry = __state.Parent; __state.Controller = null; return __exception; }
        private static void Accept(SuperController sc, bool merge)
        {
            if (!OwnerHost.Accepting || StableDrain.Shared.OwnershipAdmissionStopped) return;
            if (entry == null || !ReferenceEquals(entry.Controller, sc) || entry.Accepted != 0) throw new InvalidOperationException("Scene acceptance call changed");
            entry.Accepted = checked(++nextId); AcceptedCount++;
            if (!merge) CancelController(sc);
        }
        private static void Factory(SuperController __instance, bool loadMerge, ref IEnumerator __result)
        {
            if (!OwnerHost.Accepting || StableDrain.Shared.OwnershipAdmissionStopped) return;
            long id = 0;
            if (entry != null && ReferenceEquals(entry.Controller, __instance) && entry.Accepted != 0 && !entry.Attached)
            { id = entry.Accepted; entry.Attached = true; }
            __result = new Root(__instance, __result, loadMerge, id);
        }
        internal static IEnumerator Underlying(IEnumerator iterator)
        { Root root = iterator as Root; return root == null ? iterator : root.Iterator; }
        internal static void SetFlag(SuperController sc, AsyncFlag flag, IEnumerator iterator)
        {
            typeof(SuperController).GetField("loadFlag", OriginalBackend.All).SetValue(sc, flag);
            foreach (Root root in roots) if (ReferenceEquals(root.Iterator, iterator)) { root.Flag = flag; break; }
        }
        internal static void CancelController(SuperController sc)
        {
            foreach (Root root in new List<Root>(roots)) if (ReferenceEquals(root.Controller, sc)) root.Stop();
            PlanRegistry.CancelController(sc);
        }
        internal static void CancelAll()
        { foreach (Root root in new List<Root>(roots)) root.Stop(); }
        private sealed class Root : IEnumerator, IDisposable
        {
            internal SuperController Controller;
            internal IEnumerator Iterator;
            private readonly bool merge;
            private long id;
            private bool started, busy, stopped, innerDone, legacy;
            private object current;
            internal AsyncFlag Flag;
            internal Root(SuperController sc, IEnumerator i, bool m, long accepted) { Controller = sc; Iterator = i; merge = m; id = accepted; }
            public object Current { get { return current; } }
            public void Reset() { throw new NotSupportedException(); }
            public bool MoveNext()
            {
                if (Iterator == null) return false;
                if (legacy) return LegacyMove();
                if (!started)
                {
                    // A factory created just before shutdown has acquired nothing: preserve the original unmarked path.
                    if (!OwnerHost.Accepting || StableDrain.Shared.OwnershipAdmissionStopped) { legacy = true; return LegacyMove(); }
                    if (id == 0) { id = checked(++nextId); if (!merge) CancelController(Controller); }
                    PlanRegistry.Begin(Iterator, Controller); roots.Add(this); latest[Controller] = this; started = true;
                }
                if (stopped) { Dispose(); return false; }
                busy = true;
                try
                {
                    if (!innerDone)
                    {
                        bool more = Iterator.MoveNext();
                        if (more && !stopped) { current = Iterator.Current; return true; }
                        innerDone = true;
                    }
                    // Normally LoadCo's own parallel barrier already proves this; also handles a terminal child exception.
                    if (!stopped && PlanRegistry.PendingPlans(Iterator) != 0) { current = null; return true; }
                }
                catch { stopped = true; throw; }
                finally { busy = false; if (stopped) Dispose(); }
                Dispose(); return false;
            }
            private bool LegacyMove()
            {
                if (Iterator.MoveNext()) { current = Iterator.Current; return true; }
                innerDone = true; Dispose(); return false;
            }
            internal void Stop()
            {
                stopped = true;
                if (started) PlanRegistry.Cancel(Iterator);
                if (!busy) Dispose();
            }
            public void Dispose()
            {
                if (Iterator == null) return;
                if (busy) { stopped = true; return; }
                IEnumerator old = Iterator; Iterator = null; current = null;
                try
                {
                    if (started)
                    {
                        if (stopped || !innerDone) PlanRegistry.Cancel(old);
                        PlanRegistry.End(old);
                    }
                    if (old is IDisposable) ((IDisposable)old).Dispose();
                }
                finally
                {
                    if ((stopped || !innerDone) && Flag != null) Flag.Raise();
                    Root active;
                    if (started && latest.TryGetValue(Controller, out active) && ReferenceEquals(active, this))
                    {
                        latest.Remove(Controller);
                        if ((stopped || !innerDone) && Controller != null) Cleanup(Controller);
                    }
                    roots.Remove(this); Controller = null; Flag = null;
                }
            }
        }
        private static void Cleanup(SuperController sc)
        {
            typeof(SuperController).GetField("_isLoading", OriginalBackend.All).SetValue(sc, false);
            typeof(SuperController).GetField("hideWaitTransform", OriginalBackend.All).SetValue(sc, false);
            foreach (string name in new[] { "loadingUI", "loadingUIAlt", "loadingGeometry" })
            {
                Transform ui = typeof(SuperController).GetField(name, OriginalBackend.All).GetValue(sc) as Transform;
                if (ui != null) ui.gameObject.SetActive(false);
            }
            if (ReferenceEquals(SuperController.singleton, sc))
            {
                if (UserPreferences.singleton != null) UserPreferences.singleton.pauseGlow = false;
                DAZMorphBank.DisableDirectoryCache();
            }
        }
        private static IEnumerable<CodeInstruction> Acceptance(IEnumerable<CodeInstruction> input)
        {
            var code = new List<CodeInstruction>(input); int exists = 0, admitted = 0;
            for (int i = 0; i < code.Count; i++)
            {
                yield return code[i];
                MethodInfo call = code[i].operand as MethodInfo;
                if (call == null || call.Name != "FileExists" || call.DeclaringType.FullName != "MVR.FileManagement.FileManager") continue;
                exists++;
                if (exists != 1) continue;
                if (i + 1 >= code.Count || (code[i + 1].opcode != OpCodes.Brfalse && code[i + 1].opcode != OpCodes.Brfalse_S))
                    throw new InvalidOperationException("Accepted input branch changed");
                yield return code[++i]; // Busy/invalid paths skip this fallthrough and acquire no transaction.
                yield return new CodeInstruction(OpCodes.Ldarg_0); yield return new CodeInstruction(OpCodes.Ldarg_2);
                yield return new CodeInstruction(OpCodes.Call, typeof(SceneHooks).GetMethod("Accept", OriginalBackend.All)); admitted++;
            }
            if (exists != 2 || admitted != 1) throw new InvalidOperationException("Scene input acceptance anchors changed");
        }
        internal static void Install()
        {
            MethodInfo load = typeof(SuperController).GetMethod("LoadInternal", OriginalBackend.All);
            foreach (var ignored in Acceptance(PatchProcessor.GetOriginalInstructions(load))) { }
            foreach (string name in new[] { "loadFlag", "_isLoading", "hideWaitTransform", "loadingUI", "loadingUIAlt", "loadingGeometry" })
                if (typeof(SuperController).GetField(name, OriginalBackend.All) == null) throw new MissingFieldException(name);
            var h = new Harmony("vam.personprepared.scene.cold");
            try
            {
                h.Patch(load, prefix: new HarmonyMethod(typeof(SceneHooks), "Enter"), transpiler: new HarmonyMethod(typeof(SceneHooks), "Acceptance"), finalizer: new HarmonyMethod(typeof(SceneHooks), "Exit"));
                h.Patch(PlanHooks.Load, postfix: new HarmonyMethod(typeof(SceneHooks), "Factory"));
                h.Patch(typeof(SuperController).GetMethod("OnDestroy", OriginalBackend.All), prefix: new HarmonyMethod(typeof(SceneHooks), "ControllerDestroyed"));
            }
            catch { h.UnpatchAll(h.Id); throw; }
        }
        private static void ControllerDestroyed(SuperController __instance) { CancelController(__instance); }
    }
}
