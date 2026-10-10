// Candidate production hooks: real Unity leaf calls; no SliceBoundary/NativeBoundary dependency.
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VaM.PersonPrepared.Runtime
{
    internal static class LifecycleHooks
    {
        private static Harmony harmony;
        internal static readonly MethodInfo Add = typeof(SuperController).GetMethod("AddAtom");
        internal static readonly MethodInfo Remove = typeof(SuperController).GetMethod("RemoveAtom", new[] { typeof(Atom), typeof(bool) });
        internal static readonly MethodInfo Clear = typeof(SuperController).GetMethod("ClearAtomPool");
        internal static readonly MethodInfo Put = typeof(SuperController).GetMethod("PutAtomBackInPool", OriginalBackend.All);
        internal static readonly MethodInfo Get = typeof(SuperController).GetMethod("GetAtomOfTypeFromPool", OriginalBackend.All);
        internal static readonly MethodInfo Bulk = typeof(SuperController).GetMethod("UnregisterAllPrefabsFromAtoms", OriginalBackend.All);
        private static MethodInfo Own(Type type, string name) { return type.GetMethod(name, OriginalBackend.All); }
        private static CodeInstruction Call(CodeInstruction old, Type type, string name)
        {
            var next = new CodeInstruction(OpCodes.Call, Own(type, name)); next.labels.AddRange(old.labels); next.blocks.AddRange(old.blocks); return next;
        }
        internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, MethodBase __originalMethod, ILGenerator generator)
        {
            int destroy = 0, unregister = 0, parent = 0, poolFlag = 0, read = 0, count = 0, cache = 0, unload = 0;
            LocalBuilder exitAtom = (__originalMethod == Remove || __originalMethod == Clear) ? generator.DeclareLocal(typeof(Atom)) : null;
            foreach (CodeInstruction old in input)
            {
                if (__originalMethod == Add && old.Calls(typeof(Transform).GetMethod("SetParent", new[] { typeof(Transform), typeof(bool) })))
                { parent++; yield return Call(old, typeof(OwnerHost), "ParentClone"); }
                else if (exitAtom != null && old.Calls(typeof(UnityEngine.Object).GetMethod("Destroy", new[] { typeof(UnityEngine.Object) })))
                {
                    destroy++; var duplicate = new CodeInstruction(OpCodes.Dup); duplicate.labels.AddRange(old.labels); duplicate.blocks.AddRange(old.blocks);
                    yield return duplicate; yield return new CodeInstruction(OpCodes.Call, Own(typeof(OwnerHost), "AtomForDestroy"));
                    yield return new CodeInstruction(OpCodes.Stloc, exitAtom); yield return new CodeInstruction(OpCodes.Call, Own(typeof(OwnerHost), "DestroyObject"));
                }
                else if (exitAtom != null && old.Calls(typeof(SuperController).GetMethod("UnregisterPrefab")))
                {
                    unregister++; var load = new CodeInstruction(OpCodes.Ldloc, exitAtom); load.labels.AddRange(old.labels); load.blocks.AddRange(old.blocks);
                    yield return load; yield return new CodeInstruction(OpCodes.Call, Own(typeof(OwnerHost), "UnregisterAtExit"));
                }
                else if (__originalMethod == Get && old.opcode == OpCodes.Stfld && Equals(old.operand, typeof(Atom).GetField("inPool")))
                { poolFlag++; yield return Call(old, typeof(OwnerHost), "PoolFlag"); }
                else if (__originalMethod == Bulk)
                {
                    string target = null;
                    if (old.Calls(typeof(Dictionary<string, int>).GetMethod("TryGetValue"))) { read++; target = "ReadCount"; }
                    if (old.Calls(typeof(Dictionary<string, int>).GetMethod("Remove"))) { count++; target = "RemoveCount"; }
                    if (old.Calls(typeof(Dictionary<string, GameObject>).GetMethod("Remove"))) { cache++; target = "RemoveCache"; }
                    if (old.Calls(typeof(AssetBundles.AssetBundleManager).GetMethod("UnloadAssetBundle"))) { unload++; target = "UnloadStep"; }
                    yield return target == null ? old : Call(old, typeof(BulkJournal), target);
                }
                else yield return old;
            }
            if ((__originalMethod == Add && parent != 1) || (exitAtom != null && (destroy != 1 || unregister != 1))
                || (__originalMethod == Get && poolFlag != 1) || (__originalMethod == Bulk && (read != 1 || count != 1 || cache != 1 || unload != 1)))
                throw new InvalidOperationException("Ownership lifecycle call-site anchors changed: " + __originalMethod.Name);
        }
        private static void BeforePut(Atom a) { OwnerHost.ToPool(a); }
        private static Exception AfterPool(Exception __exception, Atom a)
        { OwnedPrefab unit = OwnerHost.Find(a); if (__exception != null && unit != null) OwnerHost.Quarantine(unit, __exception); return __exception; }
        private static Exception AtomDestroyed(Exception __exception, Atom __instance)
        { OwnerHost.DestroyedAtom(__instance, __exception); return __exception; }
        private static Exception RunDestroyed(Exception __exception, DAZCharacterRun __instance)
        {
            Exception error = __exception;
            if (error == null)
                for (int i = 1; i <= 5; i++)
                    if (typeof(DAZCharacterRun).GetField("characterRunTask" + (i == 1 ? "" : i.ToString()), OriginalBackend.All).GetValue(__instance) != null)
                        error = new InvalidOperationException("DAZ worker shutdown incomplete");
            OwnerHost.DestroyedRun(__instance, error); return __exception;
        }
        private static void Pump() { OwnerHost.Pump(); }
        internal static void Install()
        {
            StableDrain.Shared.Main(); if (harmony != null) return;
            OwnerHost.Accepting = false; OriginalBackend.Validate(); RegistrationProbe.Validate();
            BundleReceipts.Validate();
            ValidateWorkerFields();
            PatchGraph.RequireUniqueService();
            PatchGraph.RequireNoForeignAddPrefix(Add);
            var candidate = new Harmony("vam.personprepared.lifecycle.cold");
            try
            {
                ReturnWitness.Install(candidate);
                BundleReceipts.Install(candidate);
                candidate.Patch(Add, prefix: new HarmonyMethod(typeof(OwnerHost), "EnterAdd"), transpiler: new HarmonyMethod(typeof(LifecycleHooks), "Rewrite"), finalizer: new HarmonyMethod(typeof(OwnerHost), "ExitAdd"));
                foreach (MethodInfo method in new[] { Remove, Clear, Get }) candidate.Patch(method, transpiler: new HarmonyMethod(typeof(LifecycleHooks), "Rewrite"));
                candidate.Patch(Put, prefix: new HarmonyMethod(typeof(LifecycleHooks), "BeforePut"), finalizer: new HarmonyMethod(typeof(LifecycleHooks), "AfterPool"));
                candidate.Patch(typeof(Atom).GetMethod("OnDestroy", OriginalBackend.All), finalizer: new HarmonyMethod(typeof(LifecycleHooks), "AtomDestroyed"));
                candidate.Patch(typeof(DAZCharacterRun).GetMethod("OnDestroy", OriginalBackend.All), finalizer: new HarmonyMethod(typeof(LifecycleHooks), "RunDestroyed"));
                candidate.Patch(Bulk, prefix: new HarmonyMethod(typeof(BulkJournal), "Begin"), transpiler: new HarmonyMethod(typeof(LifecycleHooks), "Rewrite"), finalizer: new HarmonyMethod(typeof(BulkJournal), "End"));
                candidate.Patch(typeof(SuperController).GetMethod("HardReset"), prefix: new HarmonyMethod(typeof(BulkJournal), "BeforeReset"));
                candidate.Patch(typeof(AssetBundles.AssetBundleManager).GetMethod("Update", OriginalBackend.All), postfix: new HarmonyMethod(typeof(LifecycleHooks), "Pump"));
                harmony = candidate;
            }
            catch { candidate.UnpatchAll(candidate.Id); throw; }
            // Installation does not open acquisition. The full cold-plan/native integration gate owns admission.
        }
        internal static void RemoveUnusedHooks()
        {
            OwnerHost.Accepting = false;
            if (OwnerHost.Units.Count != 0 || OwnerHost.Creators != 0 || StableDrain.Shared.Pending != 0 || BundleReceipts.Count != 0
                || PlanRegistry.LoadCount != 0 || PlanRegistry.TagCount != 0 || SceneHooks.Count != 0) throw new InvalidOperationException("Live ownership obligations remain");
            if (harmony != null) { harmony.UnpatchAll(harmony.Id); harmony = null; }
        }
        private static void ValidateWorkerFields()
        {
            Type taskType = null;
            for (int i = 1; i <= 5; i++)
            {
                FieldInfo field = typeof(DAZCharacterRun).GetField("characterRunTask" + (i == 1 ? "" : i.ToString()), OriginalBackend.All);
                if (field == null || field.IsStatic || (taskType != null && field.FieldType != taskType)) throw new MissingFieldException("DAZ worker mapping changed");
                taskType = field.FieldType;
            }
            FieldInfo thread = taskType.GetField("thread", OriginalBackend.All);
            if (thread == null || thread.FieldType != typeof(System.Threading.Thread)) throw new MissingFieldException("DAZ worker thread mapping changed");
        }
    }
    internal static class PatchGraph
    {
        internal static void RequireUniqueService()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                if (assembly != typeof(StableDrain).Assembly && assembly.GetType(typeof(StableDrain).FullName, false) != null)
                    throw new InvalidOperationException("A second cold ownership service assembly is loaded");
        }
        internal static void RequireNoForeignAddPrefix(MethodBase method)
        {
            Patches patches = Harmony.GetPatchInfo(method);
            if (patches == null) return;
            foreach (Patch patch in patches.Prefixes)
                if (patch.owner != "vam.personprepared.lifecycle.cold") throw new InvalidOperationException("Unverified pre-body AddAtom prefix: " + patch.owner);
        }
    }
}
