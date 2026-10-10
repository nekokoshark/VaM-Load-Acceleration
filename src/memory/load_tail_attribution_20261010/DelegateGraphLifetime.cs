using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using HarmonyLib;

namespace VaM.Memory
{
    // Legacy Mono's prev chain is the invocation list. kpm_next is search
    // scratch, not subscription ownership: never publish it through a clone.
    public static class DelegateGraphLifetime
    {
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static readonly FieldInfo Previous = typeof(MulticastDelegate).GetField("prev", All);
        private static readonly FieldInfo Next = typeof(MulticastDelegate).GetField("kpm_next", All);
        private static Func<MulticastDelegate, MulticastDelegate> previous;
        private static Action<MulticastDelegate> clearNext;
        private static Action<MulticastDelegate> clearPrevious;
        private static Action<MulticastDelegate, MulticastDelegate> setPrevious;
        private static Func<MulticastDelegate, MulticastDelegate> next;
        private static Action<MulticastDelegate> setMulticastInvoke;
        public static long SharedLeftNodes, ClonedRightNodes, SharedCombines, DirtyFallbacks;
        public static Action<string> ShareLog;
        private static int sampledSites;

        // Only source chains free of legacy search-history roots may be shared.
        // Existing snapshots/search are immutable; original RemoveImpl clones
        // its output and never writes the borrowed source's prev links.
        private static bool CombineShared(MulticastDelegate __instance, Delegate __0, ref Delegate __result)
        {
            if (!enabled || __0 == null || __instance.GetType() != __0.GetType()) return true;
            long count = 0;
            for (var node = __instance; node != null; node = previous(node))
            {
                if (next(node) != null) { System.Threading.Interlocked.Increment(ref DirtyFallbacks); return true; }
                count++;
            }
            var right = (MulticastDelegate)__0;
            var head = (MulticastDelegate)CleanClone(right);
            var tail = head;
            long cloned = 1;
            for (var node = previous(right); node != null; node = previous(node))
            {
                var copy = (MulticastDelegate)CleanClone(node);
                setPrevious(tail, copy);
                tail = copy;
                cloned++;
            }
            // Only the new right-side clones are written. The published left
            // chain keeps invocation order/identity and has no retired roots.
            setPrevious(tail, __instance);
            // A right-hand singleton may already have a single-cast native
            // invoke_impl. Reset the new boundary after attaching its prefix;
            // only cloned nodes are touched, never the borrowed left chain.
            setMulticastInvoke(tail);
            if (!ReferenceEquals(tail, head)) setMulticastInvoke(head);
            __result = head;
            System.Threading.Interlocked.Add(ref SharedLeftNodes, count);
            System.Threading.Interlocked.Add(ref ClonedRightNodes, cloned);
            System.Threading.Interlocked.Increment(ref SharedCombines);
            if (count >= 128 && ShareLog != null && System.Threading.Interlocked.Increment(ref sampledSites) <= 16)
                ShareLog("[delegate-share-site] leftNodes=" + count + " caller=" +
                    new System.Diagnostics.StackTrace(1, false).ToString().Replace("\r", "").Replace("\n", " | "));
            return false;
        }

        public static string ShareStats()
        {
            return "combines=" + System.Threading.Interlocked.Read(ref SharedCombines) +
                " leftClonesAvoided=" + System.Threading.Interlocked.Read(ref SharedLeftNodes) +
                " rightClones=" + System.Threading.Interlocked.Read(ref ClonedRightNodes) +
                " dirtyFallbacks=" + System.Threading.Interlocked.Read(ref DirtyFallbacks);
        }
        private static Func<MulticastDelegate, MulticastDelegate, bool> equal;
        private static Harmony harmony;
        private static volatile bool enabled;

        private static T Accessor<T>(string name, Type result, Type[] arguments, Action<ILGenerator> emit)
        {
            var method = new DynamicMethod(name, result, arguments, typeof(DelegateGraphLifetime), true);
            emit(method.GetILGenerator());
            return (T)(object)method.CreateDelegate(typeof(T));
        }

        private static Delegate CleanClone(Delegate value)
        {
            var copy = (MulticastDelegate)value.Clone();
            if (enabled) clearNext(copy);
            // Keep prev and all runtime invocation/target/method fields intact.
            return copy;
        }

        private static bool InvocationList(MulticastDelegate __instance, ref Delegate[] __result)
        {
            if (!enabled) return true;
            int count = 0;
            for (var node = __instance; node != null; node = previous(node)) count++;
            var result = new Delegate[count];
            for (var node = __instance; node != null; node = previous(node))
            {
                var copy = (MulticastDelegate)node.Clone();
                clearNext(copy);
                clearPrevious(copy);
                result[--count] = copy;
            }
            __result = result;
            return false;
        }

        // Same private contract as original KPM: return the newest matched
        // source node and its oldest predecessor. Source and needle are read-only.
        private static bool Search(MulticastDelegate needle, MulticastDelegate haystack,
            ref MulticastDelegate tail, ref MulticastDelegate __result)
        {
            if (!enabled) return true;
            tail = null;
            __result = null;
            var first = previous(needle);
            if (first == null)
            {
                for (var node = haystack; node != null; node = previous(node))
                    if (equal(needle, node))
                    { __result = node; tail = previous(node); break; }
                return false;
            }
            int count = 1;
            for (var node = first; node != null; node = previous(node)) count++;
            var pattern = new MulticastDelegate[count];
            var failure = new int[count];
            var window = new MulticastDelegate[count];
            int index = 0;
            for (var node = needle; node != null; node = previous(node)) pattern[index++] = node;
            // KMP on the reverse invocation order keeps last-occurrence semantics
            // and O(source + pattern) cost, without shared forward-link writes.
            for (int i = 1, match = 0; i < count; i++)
            {
                while (match != 0 && !equal(pattern[i], pattern[match])) match = failure[match - 1];
                if (equal(pattern[i], pattern[match])) match++;
                failure[i] = match;
            }
            int matched = 0, slot = 0;
            for (var node = haystack; node != null; node = previous(node))
            {
                window[slot] = node;
                slot = (slot + 1) % count;
                while (matched != 0 && !equal(node, pattern[matched])) matched = failure[matched - 1];
                if (equal(node, pattern[matched])) matched++;
                if (matched == count)
                { __result = window[slot]; tail = previous(node); break; }
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> CloneCalls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            int changed = 0;
            foreach (var instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                if ((instruction.opcode == OpCodes.Callvirt || instruction.opcode == OpCodes.Call) &&
                    method != null && method.DeclaringType == typeof(Delegate) && method.Name == "Clone")
                {
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = typeof(DelegateGraphLifetime).GetMethod("CleanClone", All);
                    changed++;
                }
                yield return instruction;
            }
            int expected = __originalMethod.Name == "CombineImpl" ? 4 : 2;
            if (changed != expected) throw new InvalidOperationException("Delegate clone anchors changed: " + __originalMethod.Name);
        }

        private static MethodInfo Check(string name, string hash)
        {
            var method = typeof(MulticastDelegate).GetMethod(name, All | BindingFlags.DeclaredOnly);
            if (method == null || method.GetMethodBody() == null) throw new MissingMethodException("MulticastDelegate." + name);
            using (var sha = SHA256.Create())
                if (BitConverter.ToString(sha.ComputeHash(method.GetMethodBody().GetILAsByteArray())).Replace("-", "").ToLowerInvariant() != hash)
                    throw new InvalidOperationException("Legacy delegate IL changed: " + name);
            if (Harmony.GetPatchInfo(method) != null && Harmony.GetPatchInfo(method).Owners.Count != 0)
                throw new InvalidOperationException("Unreviewed core delegate hook: " + name);
            return method;
        }

        public static void Install()
        {
            if (harmony != null) { enabled = true; return; }
            if (Previous == null || Next == null || Previous.FieldType != typeof(MulticastDelegate) || Next.FieldType != typeof(MulticastDelegate))
                throw new MissingFieldException("Legacy delegate links changed");
            var list = Check("GetInvocationList", "5c6fd13c05719b85294cf23dcb6fbf7a0982fecbad5ab3bf675a77b02501e51e");
            var combine = Check("CombineImpl", "39c41a7f0cd0ef8c156f0cb1232f7c241bccc2ac83e051f503cff1976a870e4f");
            var remove = Check("RemoveImpl", "2b0efb615714adba0a003074fddf9b644fc8c971aed3e47c474d92d059c7b0b2");
            var search = Check("KPM", "c48d906d1945b623e7d7ca93b8013d3fa9cbde805c1df4aa8f4e847f72169585");
            var baseEquals = Check("BaseEquals", "a1eea21a1a3c68784885be6b2da25eccd2f757ab63f92b8f6dc3516e0d7e8291");
            previous = Accessor<Func<MulticastDelegate, MulticastDelegate>>("delegate_prev", typeof(MulticastDelegate), new[] { typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, Previous); il.Emit(OpCodes.Ret); });
            clearNext = Accessor<Action<MulticastDelegate>>("delegate_no_search_root", typeof(void), new[] { typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, Next); il.Emit(OpCodes.Ret); });
            setPrevious = Accessor<Action<MulticastDelegate, MulticastDelegate>>("delegate_link_new_clone", typeof(void), new[] { typeof(MulticastDelegate), typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Stfld, Previous); il.Emit(OpCodes.Ret); });
            next = Accessor<Func<MulticastDelegate, MulticastDelegate>>("delegate_search_link", typeof(MulticastDelegate), new[] { typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, Next); il.Emit(OpCodes.Ret); });
            var invoke = typeof(Delegate).GetMethod("SetMulticastInvoke", All);
            if (invoke == null || invoke.ReturnType != typeof(void) || invoke.GetParameters().Length != 0)
                throw new MissingMethodException("Delegate.SetMulticastInvoke");
            setMulticastInvoke = Accessor<Action<MulticastDelegate>>("delegate_native_multicast", typeof(void), new[] { typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, invoke); il.Emit(OpCodes.Ret); });
            clearPrevious = Accessor<Action<MulticastDelegate>>("delegate_single_cast", typeof(void), new[] { typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldnull); il.Emit(OpCodes.Stfld, Previous); il.Emit(OpCodes.Ret); });
            equal = Accessor<Func<MulticastDelegate, MulticastDelegate, bool>>("delegate_base_equals", typeof(bool), new[] { typeof(MulticastDelegate), typeof(MulticastDelegate) }, delegate(ILGenerator il)
            { il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Call, baseEquals); il.Emit(OpCodes.Ret); });
            harmony = new Harmony("vam.memory.delegate-graphs");
            try
            {
                harmony.Patch(list, prefix: new HarmonyMethod(typeof(DelegateGraphLifetime), "InvocationList"));
                // Original parameter names differ by runtime; Harmony numeric
                // binding is explicitly supplied in SearchHook below.
                harmony.Patch(search, prefix: new HarmonyMethod(typeof(DelegateGraphLifetime), "SearchHook"));
                var clones = new HarmonyMethod(typeof(DelegateGraphLifetime), "CloneCalls");
                harmony.Patch(combine, prefix: new HarmonyMethod(typeof(DelegateGraphLifetime), "CombineShared"), transpiler: clones);
                harmony.Patch(remove, transpiler: clones);
                enabled = true;
            }
            catch
            {
                enabled = false;
                harmony.UnpatchAll(harmony.Id);
                harmony = null;
                throw;
            }
        }

        private static bool SearchHook(MulticastDelegate __0, MulticastDelegate __1,
            ref MulticastDelegate __2, ref MulticastDelegate __result)
        { return Search(__0, __1, ref __2, ref __result); }

        public static void Shutdown()
        {
            enabled = false;
            // Process-lifetime adapters stay installed: callers JIT-compiled
            // while enabled may inline them. The gate also covers such callers.
            // Immutable existing delegates remain valid. No stored target graph,
            // stack/root suppression, GC request or resource deallocator exists.
        }
    }
}
