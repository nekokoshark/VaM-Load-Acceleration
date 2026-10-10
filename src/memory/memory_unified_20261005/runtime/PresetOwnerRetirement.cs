using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using MeshVR;
using UnityEngine;

namespace VaM.Memory
{
    // Preset containers own registrations, not the lifetime of removed instance data.
    public static class PresetOwnerRetirement
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", Fields);
        private static readonly FieldInfo Storables = typeof(Atom).GetField("_storables", Fields);
        private static readonly FieldInfo Manager = typeof(PresetManagerControl).GetField("pm", Fields);
        private static readonly FieldInfo Regular = typeof(PresetManager).GetField("regularStorables", Fields);
        private static readonly FieldInfo[] Lists = {
            typeof(PresetManager).GetField("storables", Fields),
            typeof(PresetManager).GetField("dynamicStorables", Fields),
            typeof(PresetManager).GetField("optionalStorables", Fields),
            typeof(PresetManager).GetField("optionalStorables2", Fields),
            typeof(PresetManager).GetField("optionalStorables3", Fields)
        };
        private static readonly List<Receipt> Pending = new List<Receipt>();
        private static readonly List<JSONStorable> Keys = new List<JSONStorable>();
        private static readonly HashSet<PresetManager> Visited = new HashSet<PresetManager>();
        private static Harmony harmony;
        private static int thread, cursor, useDepth;
        private static bool draining;
        private static long retiredEntries, retiredKeys, completed, faults;
        private sealed class Receipt
        {
            internal WeakReference Atom, Instance;
        }
        private static void BeforeUnload(JSONStorableDynamic __instance, out Receipt __state)
        {
            __state = null;
            if (Thread.CurrentThread.ManagedThreadId != thread || __instance == null) return;
            Transform instance = (Transform)Instance.GetValue(__instance);
            Atom atom = __instance.containingAtom;
            if (instance == null || atom == null) return;
            __state = new Receipt { Atom = new WeakReference(atom), Instance = new WeakReference(instance) };
        }
        private static void AfterUnload(JSONStorableDynamic __instance, Receipt __state)
        {
            if (__state != null && !__instance.ready && ReferenceEquals(Instance.GetValue(__instance), null))
                Pending.Add(__state);
        }
        private static void BeforeUse(out bool __state)
        {
            __state = Thread.CurrentThread.ManagedThreadId == thread;
            if (__state) useDepth++;
        }
        private static Exception AfterUse(bool __state, Exception __exception)
        {
            if (__state) useDepth--;
            return __exception;
        }
        private static bool Dead(JSONStorable value)
        { return !ReferenceEquals(value, null) && value == null; }
        private static void Retire(PresetManager manager)
        {
            if (manager == null || !Visited.Add(manager)) return;
            foreach (FieldInfo field in Lists)
            {
                var list = (List<PresetManager.Storable>)field.GetValue(manager);
                if (list == null) continue;
                // Keep container and surviving entry identities/order; never edit a borrowed entry.
                for (int i = list.Count - 1; i >= 0; i--)
                    if (list[i] != null && Dead(list[i].storable))
                    { list.RemoveAt(i); retiredEntries++; }
            }
            var regular = (Dictionary<JSONStorable, bool>)Regular.GetValue(manager);
            if (regular == null) return;
            try
            {
                foreach (JSONStorable key in regular.Keys)
                    if (Dead(key)) Keys.Add(key);
                foreach (JSONStorable key in Keys)
                    if (regular.Remove(key)) retiredKeys++;
            }
            finally { Keys.Clear(); }
        }
        private static void RetireAtom(Atom atom)
        {
            Visited.Clear();
            try
            {
                var list = (List<JSONStorable>)Storables.GetValue(atom);
                if (list != null)
                    for (int i = 0; i < list.Count; i++)
                    {
                        var control = list[i] as PresetManagerControl;
                        if (control != null) Retire((PresetManager)Manager.GetValue(control));
                    }
                var controls = atom.presetManagerControls;
                if (controls != null)
                    for (int i = 0; i < controls.Count; i++)
                        if (controls[i] != null) Retire((PresetManager)Manager.GetValue(controls[i]));
            }
            finally { Visited.Clear(); }
        }
        public static int PendingCount { get { return Pending.Count; } }
        public static long RetiredEntries { get { return retiredEntries; } }
        public static long RetiredKeys { get { return retiredKeys; } }
        public static void Tick()
        {
            if (harmony == null || Thread.CurrentThread.ManagedThreadId != thread || useDepth != 0 || draining) return;
            draining = true;
            try
            {
                int steps = Math.Min(Pending.Count, 16);
                while (steps-- > 0 && Pending.Count != 0)
                {
                    if (cursor >= Pending.Count) cursor = 0;
                    Receipt row = Pending[cursor];
                    var atom = row.Atom.Target as Atom;
                    var instance = row.Instance.Target as Transform;
                    if (atom == null || ReferenceEquals(instance, null)) { Pending.RemoveAt(cursor); continue; }
                    if (instance != null) { cursor++; continue; }
                    // Claim before inspecting caches. Existing/pool instances remain native-alive.
                    Pending.RemoveAt(cursor);
                    long before = retiredEntries + retiredKeys;
                    try { RetireAtom(atom); completed++; }
                    catch (Exception error)
                    {
                        // This receipt has been claimed; do not repeat a partial container edit.
                        faults++;
                        if (faults <= 4 || faults % 64 == 0)
                            Log("retirementFaults=" + faults + " type=" + error.GetType().Name);
                        continue;
                    }
                    if (retiredEntries + retiredKeys != before && (completed <= 4 || completed % 64 == 0))
                        Log("entries=" + retiredEntries + " keys=" + retiredKeys + " pending=" + Pending.Count +
                            " nativeDeath=required borrowedData=unchanged countsNotBytes=True");
                }
            }
            finally { draining = false; }
        }
        private static void Check(FieldInfo field, Type type)
        {
            if (field == null || field.IsStatic || field.FieldType != type)
                throw new MissingFieldException("Preset owner field changed: " + type);
        }
        public static void Install()
        {
            if (harmony != null) return;
            Check(Instance, typeof(Transform)); Check(Storables, typeof(List<JSONStorable>));
            Check(Manager, typeof(PresetManager));
            Check(Regular, typeof(Dictionary<JSONStorable, bool>));
            foreach (FieldInfo field in Lists) Check(field, typeof(List<PresetManager.Storable>));
            var methods = new List<MethodInfo>();
            foreach (MethodInfo method in typeof(PresetManager).GetMethods(Fields | BindingFlags.DeclaredOnly))
            {
                if (method.GetMethodBody() == null) continue;
                // These protected consumers can also be called directly by subclasses.
                bool readsOwnedContainer = method.Name == "StoreStorablesInList" ||
                    method.Name == "ClearLockStorablesInList" || method.Name == "LockStorablesInList" ||
                    method.Name == "FilterStorables" || method.Name == "LoadDefaultsPreProcessStorables";
                foreach (CodeInstruction code in PatchProcessor.GetOriginalInstructions(method, (ILGenerator)null))
                {
                    var field = code.operand as FieldInfo;
                    if (field != null && (field.Equals(Regular) || Array.IndexOf(Lists, field) >= 0))
                    { readsOwnedContainer = true; break; }
                }
                if (readsOwnedContainer) methods.Add(method);
            }
            if (methods.Count < 10) throw new MissingMethodException("Preset container consumer coverage changed");
            thread = Thread.CurrentThread.ManagedThreadId;
            harmony = new Harmony("vam.memory.preset-owners");
            try
            {
                foreach (MethodInfo method in methods)
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(PresetOwnerRetirement), "BeforeUse"),
                        finalizer: new HarmonyMethod(typeof(PresetOwnerRetirement), "AfterUse"));
                harmony.Patch(typeof(JSONStorableDynamic).GetMethod("UnloadInstance", Fields),
                    prefix: new HarmonyMethod(typeof(PresetOwnerRetirement), "BeforeUnload"),
                    postfix: new HarmonyMethod(typeof(PresetOwnerRetirement), "AfterUnload"));
                Log("installed owner=PresetManager lists=5 indexes=1 guardedConsumers=" + methods.Count +
                    " admission=explicitUnload nativeDeath=required gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }
        public static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null; Pending.Clear(); Keys.Clear(); Visited.Clear(); cursor = 0;
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUI.Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUI.Quest3TriggerUIPlugin.Log.LogInfo("[preset-owners] " + message);
        }
    }
}
