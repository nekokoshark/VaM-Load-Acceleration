// Per-call ORIGINAL write witnesses, shared by the cold authority. No before/after total inference.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AssetBundles;
using HarmonyLib;

namespace VaM.PersonPrepared.Runtime
{
    internal static class ReturnWitness
    {
        private sealed class Frame { internal string Method; internal bool Owned, Dependency; internal string DependencyName; internal Frame Parent; }
        [ThreadStatic] private static Frame frame;
        [ThreadStatic] private static OwnedPrefab receipt;
        [ThreadStatic] private static string root;
        [ThreadStatic] private static bool used;
        internal static LoadedAssetBundle ExpectedReturn(string name)
        { return receipt != null && receipt.Bundle == name && (root == "UnregisterPrefab" || root == "UnloadAssetBundleInternal") ? receipt.Wrapper : null; }
        internal sealed class Scope : IDisposable
        {
            private readonly Frame previousFrame;
            private readonly OwnedPrefab previousReceipt;
            private readonly string previousRoot;
            private readonly bool previousUsed;
            internal Scope(OwnedPrefab unit, string method)
            { previousFrame = frame; previousReceipt = receipt; previousRoot = root; previousUsed = used; frame = null; receipt = unit; root = method; used = false; }
            public void Dispose() { frame = previousFrame; receipt = previousReceipt; root = previousRoot; used = previousUsed; }
        }
        private static void Enter(MethodBase __originalMethod, string assetBundleName, out object __state)
        {
            string name = __originalMethod.Name; bool owned = false;
            if (receipt != null)
            {
                if (frame == null) { owned = !used && name == root; if (owned) used = true; }
                else if (frame.Owned) owned = (frame.Method == "GetCachedPrefab" && name == "RegisterAssetBundleAdditionalUse")
                    || (frame.Method == "LoadAssetAsync" && name == "LoadAssetBundleInternal")
                    || (frame.Method == "UnregisterPrefab" && name == "UnloadAssetBundleInternal");
            }
            var next = new Frame { Method = name, Owned = owned, Parent = frame }; frame = next; __state = next;
            if (name == "LoadAssetBundleInternal" && owned && assetBundleName != receipt.Bundle)
            { next.Dependency = true; next.DependencyName = assetBundleName; next.Owned = false; }
        }
        private static Exception Exit(Exception __exception, object __state)
        { var current = (Frame)__state; if (!ReferenceEquals(frame, current)) throw new InvalidOperationException("Backend call-frame mismatch"); frame = current.Parent; return __exception; }
        private static void CountWrite(Dictionary<string, int> table, string key, int value)
        {
            table.Add(key, value);
            if (frame != null && frame.Owned && key == receipt.Key && ReferenceEquals(table, OriginalBackend.Counts(receipt.Controller))) receipt.PrefabWrites++;
        }
        private static void Increment(LoadedAssetBundle wrapper, int value)
        {
            if (value != wrapper.m_ReferencedCount + 1) throw new InvalidOperationException("Acquire IL changed");
            wrapper.m_ReferencedCount = value;
            if (frame != null && frame.Dependency) BundleReceipts.EdgeWrite(receipt, frame.DependencyName, wrapper);
            if (frame != null && frame.Owned) { receipt.BundleWrites++; receipt.Wrapper = wrapper; }
        }
        private static void Decrement(LoadedAssetBundle wrapper, int value)
        {
            if (value != wrapper.m_ReferencedCount - 1) throw new InvalidOperationException("Return IL changed");
            wrapper.m_ReferencedCount = value;
            BundleReceipts.WitnessDecrement(wrapper);
            if (frame != null && frame.Owned && ReferenceEquals(wrapper, receipt.Wrapper)) receipt.ReturnWrites++;
        }
        private static void AddedWrapper(Dictionary<string, LoadedAssetBundle> table, string name, LoadedAssetBundle wrapper)
        {
            table.Add(name, wrapper);
            if (frame != null && frame.Owned && name == receipt.Bundle)
            { receipt.BundleWrites++; receipt.Wrapper = wrapper; BundleReceipts.NewRoot(receipt, wrapper); }
            else if (frame != null && frame.Dependency) BundleReceipts.EdgeWrite(receipt, name, wrapper);
        }
        private static void Enqueued(List<AssetBundleLoadOperation> queue, AssetBundleLoadOperation operation)
        {
            queue.Add(operation);
            if (frame != null && frame.Owned)
            {
                string name = (string)typeof(AssetBundleLoadAssetOperationFull).GetField("m_AssetBundleName", OriginalBackend.All).GetValue(operation);
                if (name != receipt.Bundle) throw new InvalidOperationException("Unmatched operation routing");
                receipt.Operation = (AssetBundleLoadAssetOperation)operation; receipt.OperationWrites++;
            }
        }
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, MethodBase __originalMethod)
        {
            int count = 0; string name = __originalMethod.Name;
            FieldInfo refs = typeof(LoadedAssetBundle).GetField("m_ReferencedCount");
            MethodInfo add = typeof(Dictionary<string, int>).GetMethod("Add");
            foreach (CodeInstruction old in input)
            {
                string replacement = null;
                if ((name == "GetCachedPrefab" || name == "RegisterPrefab" || name == "UnregisterPrefab") && old.Calls(add)) replacement = "CountWrite";
                if (old.opcode == OpCodes.Stfld && Equals(old.operand, refs)) replacement = name == "UnloadAssetBundleInternal" ? "Decrement" : "Increment";
                if (name == "LoadAssetBundleInternal" && old.Calls(typeof(Dictionary<string, LoadedAssetBundle>).GetMethod("Add"))) replacement = "AddedWrapper";
                if (name == "LoadAssetAsync" && old.Calls(typeof(List<AssetBundleLoadOperation>).GetMethod("Add"))) replacement = "Enqueued";
                if (replacement == null) { yield return old; continue; }
                var next = new CodeInstruction(OpCodes.Call, typeof(ReturnWitness).GetMethod(replacement, OriginalBackend.All));
                next.labels.AddRange(old.labels); next.blocks.AddRange(old.blocks); count++; yield return next;
            }
            if (count != (name == "LoadAssetBundleInternal" ? 5 : 1)) throw new InvalidOperationException("Original backend write anchors changed: " + name + "=" + count);
        }
        internal static MethodInfo[] Methods()
        {
            return new[] { typeof(SuperController).GetMethod("GetCachedPrefab"), typeof(SuperController).GetMethod("RegisterPrefab"), typeof(SuperController).GetMethod("UnregisterPrefab"),
                typeof(AssetBundleManager).GetMethod("RegisterAssetBundleAdditionalUse"), typeof(AssetBundleManager).GetMethod("UnloadAssetBundleInternal", OriginalBackend.All),
                typeof(AssetBundleManager).GetMethod("LoadAssetBundleInternal", OriginalBackend.All), typeof(AssetBundleManager).GetMethod("LoadAssetAsync") };
        }
        internal static void Install(Harmony harmony)
        {
            foreach (MethodInfo method in Methods()) harmony.Patch(method, prefix: new HarmonyMethod(typeof(ReturnWitness), "Enter"),
                transpiler: new HarmonyMethod(typeof(ReturnWitness), "Rewrite"), finalizer: new HarmonyMethod(typeof(ReturnWitness), "Exit"));
        }
    }
}
