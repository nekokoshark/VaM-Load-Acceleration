// Cold backend receipts: one journal per actual wrapper, not one dependency lease per Person.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AssetBundles;
using HarmonyLib;
using UnityEngine;

namespace VaM.PersonPrepared.Runtime
{
    internal enum EdgeState { Pending, Claimed, Confirmed, Unknown }
    internal sealed class BundleEdge
    {
        internal readonly string Name;
        internal LoadedAssetBundle Wrapper;
        internal AssetBundleCreateRequest Create;
        internal WWW Download;
        internal bool CompletionBasis;
        internal bool DownloadDone;
        internal int ReturnWrites;
        internal EdgeState State;
        internal BundleEdge(string name) { Name = name; }
        internal bool Terminal()
        { return CompletionBasis && (Create == null || Create.isDone) && (Download == null || DownloadDone); }
    }
    internal sealed class BundleReceipt
    {
        internal readonly string Name;
        internal readonly BundleEdge[] Edges;
        internal LoadedAssetBundle Root;
        internal string[] Table;
        internal AssetBundleCreateRequest Create;
        internal WWW Download;
        internal int Filled;
        internal bool DownloadDone;
        internal bool NewRoot, Committed, Detached, Closed, Faulted;
        internal BundleReceipt(string name, string[] edges)
        { Name = name; Edges = new BundleEdge[edges.Length]; for (int i = 0; i < edges.Length; i++) Edges[i] = new BundleEdge(edges[i]); }
        internal bool Terminal()
        {
            if ((Create != null && !Create.isDone) || (Download != null && !DownloadDone)) return false;
            foreach (BundleEdge edge in Edges) if (!edge.Terminal()) return false;
            return true;
        }
    }
    internal static class BundleReceipts
    {
        private static readonly List<BundleReceipt> live = new List<BundleReceipt>();
        internal static int Count { get { return live.Count; } }
        internal static Dictionary<string, T> Table<T>(string name)
        { return (Dictionary<string, T>)typeof(AssetBundleManager).GetField(name, OriginalBackend.All).GetValue(null); }
        private static BundleReceipt Find(LoadedAssetBundle wrapper)
        { foreach (BundleReceipt r in live) if (ReferenceEquals(r.Root, wrapper)) return r; return null; }
        internal static BundleReceipt Reserve(OwnedPrefab unit)
        {
            LoadedAssetBundle existing;
            OriginalBackend.Tracked().TryGetValue(unit.Bundle, out existing);
            BundleReceipt shared = existing == null ? null : Find(existing);
            if (shared != null) return shared;
            string[] names, table = null;
            if (existing != null)
            {
                Table<string[]>("m_Dependencies").TryGetValue(unit.Bundle, out table);
                names = table == null ? new string[0] : (string[])table.Clone();
            }
            else
            {
                var manifest = (AssetBundleManifest)typeof(AssetBundleManager).GetField("m_AssetBundleManifest", OriginalBackend.All).GetValue(null);
                if (manifest == null) throw new InvalidOperationException("Manifest missing before acquisition");
                names = (string[])manifest.GetAllDependencies(unit.Bundle).Clone();
                for (int i = 0; i < names.Length; i++) names[i] = (string)OriginalBackend.Remap.Invoke(null, new object[] { names[i] });
            }
            var receipt = new BundleReceipt(unit.Bundle, names) { Root = existing, Table = table };
            if (existing != null)
            {
                foreach (BundleEdge edge in receipt.Edges)
                {
                    LoadedAssetBundle wrapper;
                    if (!OriginalBackend.Tracked().TryGetValue(edge.Name, out wrapper)) throw new InvalidOperationException("Existing dependency identity missing");
                    Capture(edge, wrapper); receipt.Filled++;
                }
                receipt.Committed = true;
            }
            // Allocate the live slot before GetCachedPrefab/LoadAssetAsync performs a backend write.
            live.Add(receipt); return receipt;
        }
        private static void Capture(BundleEdge edge, LoadedAssetBundle wrapper)
        {
            edge.Wrapper = wrapper;
            Table<AssetBundleCreateRequest>("m_LoadingBundles").TryGetValue(edge.Name, out edge.Create);
            Table<WWW>("m_DownloadingWWWs").TryGetValue(edge.Name, out edge.Download);
            edge.DownloadDone = edge.Download != null && edge.Download.isDone;
            edge.CompletionBasis = wrapper.m_AssetBundle != null || edge.Create != null || edge.Download != null
                || Table<string>("m_DownloadingErrors").ContainsKey(edge.Name);
        }
        internal static void NewRoot(OwnedPrefab unit, LoadedAssetBundle wrapper)
        {
            BundleReceipt r = unit.Receipt;
            if (r.Root != null) throw new InvalidOperationException("Repeated new root receipt");
            r.Root = wrapper; r.NewRoot = true;
            Table<AssetBundleCreateRequest>("m_LoadingBundles").TryGetValue(r.Name, out r.Create);
            Table<WWW>("m_DownloadingWWWs").TryGetValue(r.Name, out r.Download);
            r.DownloadDone = r.Download != null && r.Download.isDone;
        }
        internal static void EdgeWrite(OwnedPrefab unit, string name, LoadedAssetBundle wrapper)
        {
            BundleReceipt r = unit.Receipt;
            if (!r.NewRoot || r.Filled >= r.Edges.Length || r.Edges[r.Filled].Name != name)
                throw new InvalidOperationException("Unmatched original dependency acquisition");
            Capture(r.Edges[r.Filled++], wrapper);
        }
        internal static void Commit(OwnedPrefab unit)
        {
            BundleReceipt r = unit.Receipt;
            if (!ReferenceEquals(r.Root, unit.Wrapper) || r.Filled != r.Edges.Length) throw new InvalidOperationException("Incomplete bundle receipt");
            if (r.NewRoot && r.Edges.Length > 0)
            {
                if (!Table<string[]>("m_Dependencies").TryGetValue(r.Name, out r.Table) || r.Table.Length != r.Edges.Length)
                    throw new InvalidOperationException("Missing original dependency table");
                for (int i = 0; i < r.Table.Length; i++) if (r.Table[i] != r.Edges[i].Name) throw new InvalidOperationException("Dependency table changed");
            }
            r.Committed = true;
        }
        internal static void NoAcquisition(OwnedPrefab unit)
        { if (unit.Receipt != null && unit.Receipt.Root == null) live.Remove(unit.Receipt); }
        internal static bool Ready(OwnedPrefab unit)
        { return unit.Receipt == null || unit.Wrapper == null || unit.Wrapper.m_ReferencedCount != 1 || unit.Receipt.Terminal(); }

        private sealed class PublicFrame
        { internal string Name; internal BundleReceipt Receipt; internal PublicFrame Parent; }
        private sealed class ReturnFrame
        { internal string Name; internal LoadedAssetBundle Wrapper; internal BundleEdge Edge; internal bool Exact; internal ReturnFrame Parent; }
        [ThreadStatic] private static PublicFrame publicFrame;
        [ThreadStatic] private static ReturnFrame frame;
        [ThreadStatic] private static LoadedAssetBundle expected;
        [ThreadStatic] private static BundleEdge expectedEdge;
        private static void EnterPublic(string assetBundleName, out PublicFrame __state)
        { __state = new PublicFrame { Name = assetBundleName, Parent = publicFrame }; publicFrame = __state; }
        private static Exception ExitPublic(Exception __exception, PublicFrame __state)
        { publicFrame = __state.Parent; return __exception; }
        private static void EnterInternal(string assetBundleName, out ReturnFrame __state)
        {
            LoadedAssetBundle wrapper = expected;
            if (wrapper == null) wrapper = ReturnWitness.ExpectedReturn(assetBundleName);
            bool exact = wrapper != null;
            if (wrapper == null) OriginalBackend.Tracked().TryGetValue(assetBundleName, out wrapper);
            __state = new ReturnFrame { Name = assetBundleName, Wrapper = wrapper, Edge = expectedEdge, Exact = exact || (wrapper != null && Find(wrapper) != null), Parent = frame };
            expected = null; expectedEdge = null; frame = __state;
            if (publicFrame != null && publicFrame.Name == assetBundleName && publicFrame.Receipt == null && wrapper != null)
                publicFrame.Receipt = Find(wrapper);
        }
        internal static void WitnessDecrement(LoadedAssetBundle wrapper)
        {
            if (frame == null || (frame.Exact && !ReferenceEquals(frame.Wrapper, wrapper))) throw new InvalidOperationException("Return wrapper frame changed");
            frame.Wrapper = wrapper;
            if (frame.Edge != null) frame.Edge.ReturnWrites++;
        }
        private static Exception ExitInternal(Exception __exception, ReturnFrame __state)
        {
            try
            {
                BundleReceipt r = __state.Wrapper == null ? null : Find(__state.Wrapper);
                if (__exception == null && r != null && r.Root.m_ReferencedCount == 0 && !r.Closed)
                    Close(r, null); // Also closes native-null failed roots and a final legacy consumer.
                if (__exception != null && r != null) { r.Faulted = true; StableDrain.Shared.StopForOwnershipFault(__exception); }
                return __exception;
            }
            catch (Exception error) { StableDrain.Shared.StopForOwnershipFault(error); throw; }
            finally { frame = __state.Parent; }
        }
        private static bool Lookup(Dictionary<string, LoadedAssetBundle> table, string name, out LoadedAssetBundle wrapper)
        {
            if (frame != null && frame.Exact && frame.Name == name && frame.Wrapper != null) { wrapper = frame.Wrapper; return true; }
            return table.TryGetValue(name, out wrapper);
        }
        private static bool Remove(Dictionary<string, LoadedAssetBundle> table, string name)
        {
            LoadedAssetBundle actual;
            if (frame != null && frame.Exact && frame.Name == name && table.TryGetValue(name, out actual) && !ReferenceEquals(actual, frame.Wrapper)) return false;
            return table.Remove(name);
        }
        private static void NativeUnload(AssetBundle bundle, bool all)
        {
            BundleReceipt r = frame == null || frame.Wrapper == null ? null : Find(frame.Wrapper);
            if (r == null) { bundle.Unload(all); return; }
            if (!ReferenceEquals(r.Root.m_AssetBundle, bundle) || !all) throw new InvalidOperationException("Native release identity changed");
            Close(r, bundle);
        }
        private static void Detach(BundleReceipt r)
        {
            LoadedAssetBundle wrapper; string[] table;
            if (!OriginalBackend.Tracked().TryGetValue(r.Name, out wrapper) || !ReferenceEquals(wrapper, r.Root)) throw new InvalidOperationException("Old root routing changed");
            bool has = Table<string[]>("m_Dependencies").TryGetValue(r.Name, out table);
            if (has != (r.Table != null) || (has && !ReferenceEquals(table, r.Table))) throw new InvalidOperationException("Dependency routing incarnation changed");
            if (has)
            {
                for (int i = 0; i < table.Length; i++) if (table[i] != r.Edges[i].Name) throw new InvalidOperationException("Dependency routing mutated");
                Table<string[]>("m_Dependencies").Remove(r.Name);
            }
            OriginalBackend.Tracked().Remove(r.Name);
            var loaded = Table<LoadedAssetBundle>("m_LoadedAssetBundles");
            if (loaded.TryGetValue(r.Name, out wrapper) && ReferenceEquals(wrapper, r.Root)) loaded.Remove(r.Name);
            Table<string>("m_DownloadingErrors").Remove(r.Name); Table<float>("m_BundleLoadStartTimes").Remove(r.Name);
            r.Detached = true;
        }
        private static void Close(BundleReceipt r, AssetBundle native)
        {
            if (!r.Committed || r.Faulted || r.Closed || r.Detached || r.Root.m_ReferencedCount != 0 || !r.Terminal())
                throw new InvalidOperationException("Final backend retirement not proved");
            try
            {
                Detach(r); // Crucially BEFORE native callbacks can acquire a new same-name root.
                if (native != null) native.Unload(true);
                foreach (BundleEdge edge in r.Edges)
                {
                    if (edge.State != EdgeState.Pending || edge.Wrapper == null || edge.Wrapper.m_ReferencedCount <= 0 || !edge.Terminal())
                        throw new InvalidOperationException("Dependency return boundary not proved");
                    edge.State = EdgeState.Claimed;
                    try { ReturnExact(edge.Name, edge.Wrapper, edge); if (edge.ReturnWrites != 1) throw new InvalidOperationException("Dependency decrement unproved"); edge.State = EdgeState.Confirmed; }
                    catch { edge.State = EdgeState.Unknown; throw; }
                }
                r.Closed = true; live.Remove(r);
            }
            catch { r.Faulted = true; throw; }
        }
        internal static void ReturnExact(string name, LoadedAssetBundle wrapper, BundleEdge edge)
        {
            if (edge != null && wrapper.m_ReferencedCount == 1 && Find(wrapper) == null)
                PrepareFinalDependency(name, wrapper);
            LoadedAssetBundle previous = expected; BundleEdge priorEdge = expectedEdge;
            expected = wrapper; expectedEdge = edge;
            try { AssetBundleManager.UnloadAssetBundle(name); }
            finally { expected = previous; expectedEdge = priorEdge; }
        }
        private static void PrepareFinalDependency(string name, LoadedAssetBundle wrapper)
        {
            LoadedAssetBundle current; string[] table;
            if (!OriginalBackend.Tracked().TryGetValue(name, out current) || !ReferenceEquals(current, wrapper))
                throw new InvalidOperationException("Final dependency incarnation changed");
            Table<string[]>("m_Dependencies").TryGetValue(name, out table);
            var r = new BundleReceipt(name, table == null ? new string[0] : (string[])table.Clone()) { Root = wrapper, Table = table };
            Table<AssetBundleCreateRequest>("m_LoadingBundles").TryGetValue(name, out r.Create);
            Table<WWW>("m_DownloadingWWWs").TryGetValue(name, out r.Download);
            r.DownloadDone = r.Download != null && r.Download.isDone;
            foreach (BundleEdge child in r.Edges)
            {
                if (!OriginalBackend.Tracked().TryGetValue(child.Name, out current)) throw new InvalidOperationException("Final dependency child identity missing");
                Capture(child, current); r.Filled++;
            }
            if (!r.Terminal()) throw new InvalidOperationException("Final dependency operation still active");
            r.Committed = true; live.Add(r); // Preallocate its return journal before decrement/native side effects.
        }
        private static void Dependencies(string name)
        {
            if (publicFrame != null && publicFrame.Name == name && publicFrame.Receipt != null && publicFrame.Receipt.Detached) return;
            typeof(AssetBundleManager).GetMethod("UnloadDependencies", OriginalBackend.All).Invoke(null, new object[] { name });
        }
        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            int count = 0; bool isPublic = __originalMethod.Name == "UnloadAssetBundle";
            foreach (CodeInstruction old in input)
            {
                string target = null;
                if (isPublic && old.Calls(typeof(AssetBundleManager).GetMethod("UnloadDependencies", OriginalBackend.All))) target = "Dependencies";
                if (!isPublic)
                {
                    if (old.Calls(typeof(Dictionary<string, LoadedAssetBundle>).GetMethod("TryGetValue"))) target = "Lookup";
                    if (old.Calls(typeof(Dictionary<string, LoadedAssetBundle>).GetMethod("Remove"))) target = "Remove";
                    if (old.Calls(typeof(AssetBundle).GetMethod("Unload", new[] { typeof(bool) }))) target = "NativeUnload";
                }
                if (target == null) { yield return old; continue; }
                var next = new CodeInstruction(OpCodes.Call, typeof(BundleReceipts).GetMethod(target, OriginalBackend.All));
                next.labels.AddRange(old.labels); next.blocks.AddRange(old.blocks); count++; yield return next;
            }
            if (count != (isPublic ? 1 : 4)) throw new InvalidOperationException("Backend retirement anchors changed");
        }
        internal static void Install(Harmony h)
        {
            h.Patch(typeof(AssetBundleManager).GetMethod("UnloadAssetBundle"), prefix: new HarmonyMethod(typeof(BundleReceipts), "EnterPublic"),
                transpiler: new HarmonyMethod(typeof(BundleReceipts), "Rewrite"), finalizer: new HarmonyMethod(typeof(BundleReceipts), "ExitPublic"));
            h.Patch(typeof(AssetBundleManager).GetMethod("UnloadAssetBundleInternal", OriginalBackend.All), prefix: new HarmonyMethod(typeof(BundleReceipts), "EnterInternal"),
                transpiler: new HarmonyMethod(typeof(BundleReceipts), "Rewrite"), finalizer: new HarmonyMethod(typeof(BundleReceipts), "ExitInternal"));
            h.Patch(typeof(WWW).GetMethod("Dispose"), prefix: new HarmonyMethod(typeof(BundleReceipts), "BeforeDownloadDispose"));
        }
        private static void BeforeDownloadDispose(WWW __instance)
        {
            foreach (BundleReceipt r in live)
            {
                if (ReferenceEquals(r.Download, __instance)) r.DownloadDone = __instance.isDone;
                foreach (BundleEdge edge in r.Edges) if (ReferenceEquals(edge.Download, __instance)) edge.DownloadDone = __instance.isDone;
            }
        }
        internal static void Validate()
        {
            Check<LoadedAssetBundle>("m_LoadedAssetBundles"); Check<string[]>("m_Dependencies"); Check<AssetBundleCreateRequest>("m_LoadingBundles");
            Check<WWW>("m_DownloadingWWWs"); Check<string>("m_DownloadingErrors"); Check<float>("m_BundleLoadStartTimes");
        }
        private static void Check<T>(string name)
        { FieldInfo f = typeof(AssetBundleManager).GetField(name, OriginalBackend.All); if (f == null || f.FieldType != typeof(Dictionary<string, T>)) throw new MissingFieldException(name); }
    }
}
