using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MeshVR;
using MVR.FileManagement;
using UnityEngine;

namespace VamBundleDiskCache
{
    // Installed once at startup. Never restore MoveNext while cached iterators exist.
    internal static class Feature
    {
        internal static Action<string> Logger;
        private static Harmony harmony;
        private sealed class State { internal WeakReference Owner; internal long Size; internal byte[] Bytes; internal CacheJob Job; }
        // VaM uses the .NET 3.5 profile, which has no ConditionalWeakTable.
        // No cache job retains its iterator. Dead bindings are pruned at allocation and by the cold host.
        private static readonly List<State> States = new List<State>();
        private static readonly MethodInfo Read = typeof(FileManager).GetMethod("ReadAllBytesCoroutine", new[] { typeof(FileEntry), typeof(byte[]) });
        private static readonly MethodInfo Memory = typeof(AssetBundle).GetMethod("LoadFromMemoryAsync", new[] { typeof(byte[]) });
        private static readonly MethodInfo Size = typeof(FileEntry).GetProperty("Size").GetGetMethod();

        internal static void Sweep()
        {
            for (int index = States.Count - 1; index >= 0; index--)
                if (!States[index].Owner.IsAlive) { States[index].Bytes = null; States.RemoveAt(index); }
        }

        private static State Find(object iterator)
        {
            foreach (var state in States) if (ReferenceEquals(state.Owner.Target, iterator)) return state;
            throw new InvalidOperationException("cache state missing");
        }

        private static void Remove(object iterator)
        {
            for (int index = States.Count - 1; index >= 0; index--)
                if (!States[index].Owner.IsAlive || ReferenceEquals(States[index].Owner.Target, iterator))
                { States[index].Bytes = null; States.RemoveAt(index); }
        }

        internal static MethodInfo Target()
        {
            MethodInfo target = null;
            foreach (var type in typeof(AssetLoader).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                if (type.Name.StartsWith("<LoadBundleFileAsync>", StringComparison.Ordinal))
                {
                    if (target != null) throw new InvalidOperationException("ambiguous bundle iterator");
                    target = AccessTools.Method(type, "MoveNext");
                }
            if (target == null) throw new InvalidOperationException("bundle iterator missing");
            return target;
        }

        private static MethodInfo Helper(string name) { return AccessTools.Method(typeof(Feature), name); }

        private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            int allocation = 0, read = 0, memory = 0;
            for (int index = 0; index < code.Count; index++)
            {
                var current = code[index];
                if (current.opcode == OpCodes.Newarr && Equals(current.operand, typeof(byte)))
                {
                    if (index < 2 || code[index - 1].opcode != OpCodes.Conv_Ovf_I || !code[index - 2].Calls(Size)
                        || current.labels.Count != 0 || current.blocks.Count != 0)
                        throw new InvalidOperationException("unexpected byte-array allocation shape");
                    code[index - 1].opcode = OpCodes.Ldarg_0;
                    code[index - 1].operand = null;
                    current.opcode = OpCodes.Call;
                    current.operand = Helper("Allocate");
                    allocation++;
                }
                else if (current.Calls(Read) || current.Calls(Memory))
                {
                    bool isRead = current.Calls(Read);
                    var owner = new CodeInstruction(OpCodes.Ldarg_0);
                    owner.labels.AddRange(current.labels);
                    owner.blocks.AddRange(current.blocks);
                    current.labels.Clear();
                    current.blocks.Clear();
                    code.Insert(index++, owner);
                    current.opcode = OpCodes.Call;
                    current.operand = Helper(isRead ? "Prepare" : "Load");
                    if (isRead) read++; else memory++;
                }
            }
            if (allocation != 1 || read != 1 || memory != 1)
                throw new InvalidOperationException("bundle cache anchors=" + allocation + "/" + read + "/" + memory);
            return code;
        }

        internal static int CheckAnchors()
        {
            int count = 0;
            foreach (var instruction in Rewrite(PatchProcessor.GetOriginalInstructions(Target(), (ILGenerator)null)))
                if (instruction.Calls(Helper("Allocate")) || instruction.Calls(Helper("Prepare")) || instruction.Calls(Helper("Load"))) count++;
            return count;
        }

        internal static void Install()
        {
            if (harmony != null) return;
            if (CheckAnchors() != 3) throw new InvalidOperationException("incomplete bundle cache anchors");
            var patch = new Harmony("vam.bundle.disk.cache.process.lifetime");
            try
            {
                patch.Patch(Target(), transpiler: new HarmonyMethod(Helper("Rewrite")),
                    postfix: new HarmonyMethod(Helper("AfterMoveNext")), finalizer: new HarmonyMethod(Helper("AfterException")));
                harmony = patch;
            }
            catch { patch.UnpatchAll(patch.Id); throw; }
        }

        private static byte[] Allocate(long size, object iterator)
        {
            if (!Backend.Available) return new byte[checked((int)size)];
            Sweep();
            States.Add(new State { Owner = new WeakReference(iterator), Size = size });
            return null;
        }

        private static IEnumerator Prepare(FileEntry entry, byte[] bytes, object iterator)
        {
            if (bytes != null) return FileManager.ReadAllBytesCoroutine(entry, bytes);
            State state = Find(iterator);
            var file = entry as VarFileEntry;
            if (file == null || file.Simulated) throw new InvalidOperationException("cache branch requires archived VAR entry");
            return PrepareCached(file, iterator, state);
        }

        private static IEnumerator PrepareCached(VarFileEntry file, object iterator, State state)
        {
            var job = Backend.Enqueue(file.Package.Path, file.InternalSlashPath, state.Size);
            state.Job = job;
            while (!job.Complete) yield return null;
            if (job.Error == null)
            {
                if (Logger != null) Logger("[bundle-disk-cache] ready inputBytes=" + state.Size + " converted=" + job.Converted + " hit=" + job.Hit + " managedArchiveArray=False");
                yield break;
            }
            // Preserve the original reader and load behavior when preparation fails.
            if (Logger != null) Logger("[bundle-disk-cache] original reader retained: " + job.Error);
            state.Bytes = new byte[checked((int)state.Size)];
            yield return FileManager.ReadAllBytesCoroutine(file, state.Bytes);
        }

        private static AssetBundleCreateRequest Load(byte[] bytes, object iterator)
        {
            if (bytes != null) return AssetBundle.LoadFromMemoryAsync(bytes);
            State state = Find(iterator);
            CacheJob job = state.Job;
            if (job == null || !job.Complete)
                throw new InvalidOperationException("cache state missing at native load");
            try
            {
                if (state.Bytes != null) return AssetBundle.LoadFromMemoryAsync(state.Bytes);
                if (job.Error != null) throw new InvalidOperationException("cache failed without original bytes");
                // Cache files are immutable and never evicted while the process runs.
                return AssetBundle.LoadFromFileAsync(job.Path);
            }
            finally { Remove(iterator); }
        }

        private static void AfterMoveNext(object __instance, bool __result)
        {
            if (!__result) Remove(__instance);
        }

        private static Exception AfterException(object __instance, Exception __exception)
        {
            if (__exception != null) Remove(__instance);
            return __exception;
        }
    }
}
