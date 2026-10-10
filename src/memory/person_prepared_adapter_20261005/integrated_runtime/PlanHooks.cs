// Production Person-only call-site bridge; original non-Person and public loader paths remain legacy.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace VaM.PersonPrepared.Runtime
{
internal static class PlanHooks
{
    internal static readonly BindingFlags All = OriginalBackend.All;
    internal static int Anchors;
    internal static readonly MethodInfo Parallel = typeof(SuperController).GetMethod("_ParallelAtomCreate", All);
    internal static readonly MethodInfo Preload = typeof(SuperController).GetMethod("_PreloadAtomPrefabAsync", All);
    internal static readonly MethodInfo Loader = typeof(SuperController).GetMethod("LoadAtomFromBundleAsync", All);
    internal static readonly MethodInfo AddBy = typeof(SuperController).GetMethod("AddAtomByType", new[] { typeof(string), typeof(string), typeof(bool), typeof(bool), typeof(bool) });
    internal static readonly MethodInfo Add = typeof(SuperController).GetMethod("AddAtom", All);
    internal static readonly MethodInfo Load = typeof(SuperController).GetMethod("LoadCo", All);
    internal static Type Iterator(MethodInfo factory)
    {
        Type result = null;
        foreach (CodeInstruction instruction in PatchProcessor.GetOriginalInstructions(factory))
            if (instruction.opcode == OpCodes.Newobj)
            {
                Type type = ((ConstructorInfo)instruction.operand).DeclaringType;
                if (!typeof(IEnumerator).IsAssignableFrom(type)) continue;
                if (result != null) throw new InvalidOperationException("Ambiguous iterator factory");
                result = type;
            }
        if (result == null) throw new InvalidOperationException("No iterator factory");
        return result;
    }
    internal static FieldInfo Field(Type type, string stem, Type expected)
    {
        FieldInfo result = null;
        foreach (FieldInfo field in type.GetFields(All))
            if ((field.Name == stem || field.Name.StartsWith("<" + stem + ">", StringComparison.Ordinal)) && field.FieldType == expected)
            { if (result != null) throw new InvalidOperationException("Ambiguous field " + stem); result = field; }
        if (result == null) throw new MissingFieldException(type.FullName, stem);
        return result;
    }
    private static CodeInstruction Replace(CodeInstruction old, string name)
    {
        var result = new CodeInstruction(OpCodes.Call, typeof(PlanHooks).GetMethod(name, All));
        result.blocks.AddRange(old.blocks); return result;
    }
    private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> source, MethodBase __originalMethod)
    {
        var list = new List<CodeInstruction>(source);
        Type loadType = Iterator(Load), parallelType = Iterator(Parallel), addType = Iterator(AddBy), loaderType = Iterator(Loader);
        string parent = __originalMethod.DeclaringType.Name;
        int preloads = 0, parallels = 0, addBy = 0, loaders = 0, caches = 0, adds = 0, done = 0, flags = 0;
        foreach (CodeInstruction instruction in list)
        {
            var target = instruction.operand as MethodInfo;
            string name = null;
            if (__originalMethod.DeclaringType == loadType)
            {
                if (target == Preload) { preloads++; name = "PreloadSite"; }
                if (target == Parallel) { parallels++; if (parallels == 2) name = "CreateSite"; }
                if (instruction.opcode == OpCodes.Stfld && Equals(instruction.operand, typeof(SuperController).GetField("loadFlag", All)))
                { flags++; name = "FlagSite"; }
            }
            else if (__originalMethod.DeclaringType == parallelType && target != null && target.Name == "AddAtomByType")
            { addBy++; name = "AddBySite"; }
            else if (__originalMethod.DeclaringType == parallelType && target == typeof(Action).GetMethod("Invoke")) { done++; name = "DoneSite"; }
            else if (__originalMethod.DeclaringType == addType)
            {
                if (target == Loader) { loaders++; name = "LoaderSite"; }
                if (target == Add) { adds++; name = "AddSite"; }
            }
            else if (__originalMethod.DeclaringType == loaderType)
            {
                if (target != null && target.Name == "GetCachedPrefab") { caches++; name = "CacheSite"; }
                if (target == Add) { adds++; name = "AddSite"; }
            }
            if (name == null) { yield return instruction; continue; }
            var self = new CodeInstruction(OpCodes.Ldarg_0);
            self.labels.AddRange(instruction.labels); instruction.labels.Clear(); yield return self;
            yield return Replace(instruction, name);
        }
        int count = preloads + (parallels == 2 ? 1 : 0) + addBy + loaders + caches + adds + done + flags;
        bool valid = (__originalMethod.DeclaringType == loadType && preloads == 1 && parallels == 2 && flags == 1 && count == 3)
            || (__originalMethod.DeclaringType == parallelType && addBy == 1 && done == 1 && count == 2)
            || (__originalMethod.DeclaringType == addType && loaders == 1 && adds == 2 && count == 3)
            || (__originalMethod.DeclaringType == loaderType && caches == 1 && adds == 1 && count == 2);
        if (!valid) throw new InvalidOperationException("Plan bridge anchors changed " + parent);
    }
    internal static void DoneSite(Action callback, IEnumerator iterator) { PlanRegistry.Done(callback, iterator); }
    internal static void FlagSite(SuperController controller, AsyncFlag flag, IEnumerator iterator) { SceneHooks.SetFlag(controller, flag, iterator); }
    internal static IEnumerator PreloadSite(SuperController controller, SuperController.AtomAsset asset, Action<GameObject> callback, IEnumerator load)
    {
        return PlanRegistry.IsManaged(load) ? PlanRegistry.Preload(controller, asset, callback, load) : (IEnumerator)Preload.Invoke(controller, new object[] { asset, callback });
    }
    internal static IEnumerator CreateSite(SuperController controller, string uid, string type, AsyncFlag flag, Action done, IEnumerator load)
    {
        IEnumerator result = (IEnumerator)Parallel.Invoke(controller, new object[] { uid, type, flag, done });
        return PlanRegistry.IsManaged(load) ? PlanRegistry.StartCreate(load, uid, type, result, done) : result;
    }
    internal static IEnumerator AddBySite(SuperController controller, string type, string uid, IEnumerator parent)
    {
        IEnumerator result = controller.AddAtomByType(type, uid);
        return PlanRegistry.Child(parent, result);
    }
    internal static IEnumerator LoaderSite(SuperController controller, SuperController.AtomAsset asset, string uid, bool user, bool select, bool focus, IEnumerator parent)
    {
        IEnumerator result = (IEnumerator)Loader.Invoke(controller, new object[] { asset, uid, user, select, focus });
        return PlanRegistry.ReadyLoader(parent, result, asset);
    }
    internal static GameObject CacheSite(SuperController controller, string bundle, string asset, IEnumerator iterator)
    {
        return PlanRegistry.Cache(controller, bundle, asset, iterator);
    }
    internal static Transform AddSite(SuperController controller, Atom atom, string uid, bool user, bool select, bool focus, bool instantiate, IEnumerator iterator)
    {
        return PlanRegistry.Add(controller, atom, uid, user, select, focus, instantiate, iterator);
    }
    internal static void Install()
    {
        var harmony = new Harmony("vam.personprepared.plan.cold");
        // Validate the entire Person group before adding the first hook.
        foreach (MethodInfo factory in new[] { Load, Parallel, AddBy, Loader })
        {
            MethodInfo method = Iterator(factory).GetMethod("MoveNext", All);
            foreach (var ignored in Rewrite(PatchProcessor.GetOriginalInstructions(method), method)) { }
        }
        foreach (MethodInfo factory in new[] { Load, Parallel, AddBy, Loader })
        {
            MethodInfo method = Iterator(factory).GetMethod("MoveNext", All);
            foreach (var ignored in Rewrite(PatchProcessor.GetOriginalInstructions(method), method)) { }
            harmony.Patch(method, transpiler: new HarmonyMethod(typeof(PlanHooks), "Rewrite"));
        }
        Anchors = 10;
    }
}
}
