using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;

namespace VaM.Memory
{
    // Remove descriptor -> destroyed instance edges, never mutate borrowed arrays/skins.
    public static class InstanceRootRetirement
    {
        private const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Instance = typeof(JSONStorableDynamic).GetField("instance", Fields);
        private static readonly FieldInfo Clothes = typeof(DAZClothingItem).GetField("clothingItemControls", Fields);
        private static readonly FieldInfo Hair = typeof(DAZHairGroup).GetField("hairGroupControls", Fields);
        private static readonly FieldInfo Skin = typeof(DAZCharacter).GetField("_skin", Fields);
        private static readonly FieldInfo ClothesSkin = typeof(DAZCharacter).GetField("_skinForClothes", Fields);
        private static readonly List<Row> Pending = new List<Row>();
        private static Harmony harmony;
        private static int thread, cursor;
        private static long retiredFields, retirementEvents;
        private sealed class Slot
        {
            internal FieldInfo Field;
            internal WeakReference Value;
        }
        private sealed class Row
        {
            internal WeakReference Owner;
            internal readonly List<Slot> Slots = new List<Slot>(2);
        }
        private static void Capture(Row row, object owner, FieldInfo field)
        {
            object value = field.GetValue(owner);
            if (value != null) row.Slots.Add(new Slot { Field = field, Value = new WeakReference(value) });
        }
        private static void BeforeUnload(JSONStorableDynamic __instance, out Row __state)
        {
            __state = null;
            if (Thread.CurrentThread.ManagedThreadId != thread || __instance == null ||
                !(__instance is DAZClothingItem || __instance is DAZHairGroup || __instance is DAZCharacter) ||
                (Transform)Instance.GetValue(__instance) == null) return;
            var row = new Row { Owner = new WeakReference(__instance) };
            if (__instance is DAZClothingItem) Capture(row, __instance, Clothes);
            else if (__instance is DAZHairGroup) Capture(row, __instance, Hair);
            else if (__instance is DAZCharacter)
            { Capture(row, __instance, Skin); Capture(row, __instance, ClothesSkin); }
            if (row.Slots.Count != 0) __state = row;
        }
        private static void AfterUnload(JSONStorableDynamic __instance, Row __state)
        {
            // A successful original unload must have relinquished its instance.
            if (__state == null || __instance.ready || !ReferenceEquals(Instance.GetValue(__instance), null)) return;
            Pending.Add(__state);
        }
        private static bool Destroyed(object value)
        {
            var array = value as Array;
            if (array != null)
            {
                foreach (object element in array)
                {
                    if (ReferenceEquals(element, null)) continue;
                    var native = element as UnityEngine.Object;
                    if (ReferenceEquals(native, null) || native != null) return false;
                }
                return true;
            }
            var obj = value as UnityEngine.Object;
            return !ReferenceEquals(obj, null) && obj == null;
        }
        private static bool Drain(Row row)
        {
            var owner = row.Owner.Target as JSONStorableDynamic;
            if (ReferenceEquals(owner, null)) return true;
            // Reload/reuse cancels the old observation, not the new instance.
            if (owner.ready || !ReferenceEquals(Instance.GetValue(owner), null)) return true;
            bool changed = false;
            for (int i = row.Slots.Count - 1; i >= 0; i--)
            {
                Slot slot = row.Slots[i]; object previous = slot.Value.Target;
                if (previous == null || !ReferenceEquals(slot.Field.GetValue(owner), previous))
                { row.Slots.RemoveAt(i); continue; }
                if (!Destroyed(previous)) continue;
                // Main-thread check/write, with no native calls/callbacks between them.
                slot.Field.SetValue(owner, null);
                retiredFields++; changed = true; row.Slots.RemoveAt(i);
            }
            if (changed)
            {
                retirementEvents++;
                if (retirementEvents <= 4 || retirementEvents % 64 == 0)
                    Log("retirementEvents=" + retirementEvents + " fields=" + retiredFields +
                        " pending=" + Pending.Count + " arrays=not-mutated nativeDestroy=unchanged countsNotBytes=True");
            }
            return row.Slots.Count == 0;
        }
        public static int PendingCount { get { return Pending.Count; } }
        public static long RetiredFields { get { return retiredFields; } }
        public static void Tick()
        {
            if (harmony == null || Thread.CurrentThread.ManagedThreadId != thread) return;
            // Work scales with recent unloads, not the full character/clothing catalogue.
            int steps = Math.Min(Pending.Count, 32);
            while (steps-- > 0 && Pending.Count != 0)
            {
                if (cursor >= Pending.Count) cursor = 0;
                if (Drain(Pending[cursor])) Pending.RemoveAt(cursor);
                else cursor++;
            }
        }
        private static void Check(FieldInfo field, Type expected)
        {
            if (field == null || field.IsStatic || field.FieldType != expected)
                throw new MissingFieldException("Instance retirement field changed: " + expected);
        }
        public static void Install()
        {
            if (harmony != null) return;
            Check(Instance, typeof(Transform)); Check(Clothes, typeof(DAZClothingItemControl[]));
            Check(Hair, typeof(DAZHairGroupControl[])); Check(Skin, typeof(DAZSkinV2)); Check(ClothesSkin, typeof(DAZSkinV2));
            MethodInfo unload = typeof(JSONStorableDynamic).GetMethod("UnloadInstance", Fields);
            if (unload == null || unload.ReturnType != typeof(void) || unload.GetParameters().Length != 0)
                throw new MissingMethodException("JSONStorableDynamic.UnloadInstance");
            thread = Thread.CurrentThread.ManagedThreadId;
            harmony = new Harmony("vam.memory.instance-roots");
            try
            {
                harmony.Patch(unload, prefix: new HarmonyMethod(typeof(InstanceRootRetirement), "BeforeUnload"),
                    postfix: new HarmonyMethod(typeof(InstanceRootRetirement), "AfterUnload"));
                Log("installed unload=event nativeDeath=required receiptIdentity=exact pendingRoots=weak gcPolicy=unchanged");
            }
            catch { Shutdown(); throw; }
        }
        public static void Shutdown()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null; Pending.Clear(); cursor = 0;
        }
        private static void Log(string message)
        {
            if (Quest3TriggerUI.Quest3TriggerUIPlugin.Log != null)
                Quest3TriggerUI.Quest3TriggerUIPlugin.Log.LogInfo("[instance-roots] " + message);
        }
    }
}
