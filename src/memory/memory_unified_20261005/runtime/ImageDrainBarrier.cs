using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;

namespace VaM.Memory
{
    internal static class ImageDrainBarrier
    {
        private const BindingFlags All = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo Queued = typeof(ImageLoaderThreaded).GetField("queuedImages", All);
        private static readonly FieldInfo Pending = typeof(ImageLoaderThreaded).GetField("_pendingQueue", All);
        private static readonly FieldInfo Processed = typeof(ImageLoaderThreaded).GetField("_processedQueue", All);
        private static readonly FieldInfo PendingLock = typeof(ImageLoaderThreaded).GetField("_pendingLock", All);
        private static readonly FieldInfo ProcessedLock = typeof(ImageLoaderThreaded).GetField("_processedLock", All);
        private static Harmony harmony;
        private static int workers;
        [ThreadStatic] private static bool ownsWork;

        private static ImageLoaderThreaded.QueuedImage Take(Queue<ImageLoaderThreaded.QueuedImage> queue)
        {
            // Runs inside the original pending lock. No dequeue->Process gap
            // can make both the queue and our in-flight receipt look empty.
            var image = queue.Dequeue();
            if (image != null) { ownsWork = true; Interlocked.Increment(ref workers); }
            return image;
        }

        private static void HandOff(Queue<ImageLoaderThreaded.QueuedImage> queue, ImageLoaderThreaded.QueuedImage image)
        {
            // Original processed lock: publish first, relinquish worker receipt last.
            queue.Enqueue(image);
            ReleaseReceipt();
        }

        private static void ReleaseReceipt()
        {
            if (!ownsWork) return;
            ownsWork = false;
            Interlocked.Decrement(ref workers);
        }

        private static Exception WorkerExit(Exception __exception)
        {
            // Only the worker method's actual exit, never cancel/timeout. Other
            // allocators still own their bytes and their own failure finalizers.
            ReleaseReceipt();
            return __exception;
        }

        internal static bool Busy()
        {
            var loader = ImageLoaderThreaded.singleton;
            if (ReferenceEquals(loader, null)) return false;
            var queued = Queued.GetValue(loader) as LinkedList<ImageLoaderThreaded.QueuedImage>;
            if (queued == null || queued.Count != 0) return true;
            object gate = PendingLock.GetValue(loader);
            if (gate == null) return true;
            lock (gate)
            {
                var pending = Pending.GetValue(loader) as Queue<ImageLoaderThreaded.QueuedImage>;
                if (pending == null || pending.Count != 0 || Interlocked.CompareExchange(ref workers, 0, 0) != 0) return true;
            }
            gate = ProcessedLock.GetValue(loader);
            if (gate == null) return true;
            lock (gate)
            {
                var processed = Processed.GetValue(loader) as Queue<ImageLoaderThreaded.QueuedImage>;
                return processed == null || processed.Count != 0 || Interlocked.CompareExchange(ref workers, 0, 0) != 0;
            }
        }

        private static IEnumerable<CodeInstruction> Route(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var take = typeof(Queue<ImageLoaderThreaded.QueuedImage>).GetMethod("Dequeue");
            var handOff = typeof(Queue<ImageLoaderThreaded.QueuedImage>).GetMethod("Enqueue");
            int takes = 0, handOffs = 0;
            foreach (var instruction in code)
            {
                if (instruction.opcode != OpCodes.Callvirt) continue;
                string target = null;
                if (Equals(instruction.operand, take)) { target = "Take"; takes++; }
                else if (Equals(instruction.operand, handOff)) { target = "HandOff"; handOffs++; }
                if (target == null) continue;
                instruction.opcode = OpCodes.Call;
                instruction.operand = typeof(ImageDrainBarrier).GetMethod(target, BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (takes != 1 || handOffs != 1) throw new InvalidOperationException("Worker handoff anchors changed");
            return code;
        }

        private static void Check(FieldInfo field, Type type)
        {
            if (field == null || field.FieldType != type) throw new MissingFieldException("Image drain " + type);
        }

        internal static void Install()
        {
            if (harmony != null) return;
            Check(Queued, typeof(LinkedList<ImageLoaderThreaded.QueuedImage>));
            Check(Pending, typeof(Queue<ImageLoaderThreaded.QueuedImage>));
            Check(Processed, typeof(Queue<ImageLoaderThreaded.QueuedImage>));
            Check(PendingLock, typeof(object)); Check(ProcessedLock, typeof(object));
            MethodInfo worker = typeof(ImageLoaderThreaded).GetMethod("MTWorkerTask", All);
            if (worker == null) throw new MissingMethodException("Image worker");
            harmony = new Harmony("vam.memory.image-drain");
            try
            {
                harmony.Patch(worker, transpiler: new HarmonyMethod(typeof(ImageDrainBarrier), "Route"),
                    finalizer: new HarmonyMethod(typeof(ImageDrainBarrier), "WorkerExit"));
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; throw; }
        }
    }
}
