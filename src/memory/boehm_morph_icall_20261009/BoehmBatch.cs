using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using HarmonyLib;
using NativeLibraryDoorstop;

namespace VaM.Memory {
    public static class BoehmBatch {
        public const string Id = "vam.memory.boehm-batch";
        public static readonly MethodInfo Target = typeof(DAZMorphBank).GetMethod("ApplyMorphsThreadedFast");
        private static bool pending;
        public static void Arm() { pending = true; }
        public static bool Ready { get { return pending && Doorstop.isInitted; } }
        public static void InstallPending() {
            if (!Ready) return;
            pending = false;
            // Harmony compiles the complete wrapper, including native icalls.
            // Compile only after the original Doorstop initialization returned.
            Install(new Harmony(Id));
            if (BoehmOptimizations.Log != null)
                BoehmOptimizations.Log("[boehm-batch] installed afterNativeInit=True eagerCompilation=False");
        }
        public static void Install(Harmony harmony) {
            harmony.Patch(Target, transpiler: new HarmonyMethod(typeof(BoehmBatch), "Rewrite"));
        }
        public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input, ILGenerator generator) {
            var codes = new List<CodeInstruction>(input);
            int size = -1, allocation = -1, free = -1;
            for (int i = 0; i < codes.Count; i++) {
                if (codes[i].opcode == OpCodes.Sizeof && Object.Equals(codes[i].operand, typeof(DAZMorphVertexFastBatch))) {
                    if (size < 0) size = i;
                }
                var method = codes[i].operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(Marshal)) continue;
                if (method.Name == "AllocHGlobal") allocation = i;
                if (method.Name == "FreeHGlobal") free = i;
            }
            if (size < 1 || allocation <= size || free <= allocation || codes[size + 1].opcode != OpCodes.Mul ||
                !codes[allocation + 1].opcode.Name.StartsWith("stloc") || codes[free - 1].opcode != OpCodes.Ldloc_S ||
                codes.Exists(c => c.blocks.Count != 0)) throw new InvalidProgramException("Original batch IL differs");
            // Original loop bound is five before decrement: at most four writes.
            if (codes[2].opcode != OpCodes.Ldc_I4_5) throw new InvalidProgramException("Batch loop bound differs");
            object table = codes[free - 1].operand;
            var release = codes[free].operand;
            Label continuation = generator.DefineLabel();
            codes[free + 1].labels.Add(continuation);
            codes[allocation + 2].blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginExceptionBlock));
            codes[free - 1].opcode = OpCodes.Leave; codes[free - 1].operand = continuation;
            codes[free].opcode = OpCodes.Nop; codes[free].operand = null;
            var handler = new CodeInstruction(OpCodes.Ldloc, table);
            handler.blocks.Add(new ExceptionBlock(ExceptionBlockType.BeginFinallyBlock));
            var end = new CodeInstruction(OpCodes.Endfinally);
            end.blocks.Add(new ExceptionBlock(ExceptionBlockType.EndExceptionBlock));
            codes.InsertRange(free + 1, new[] { handler, new CodeInstruction(OpCodes.Call, release), end });
            codes[size + 1].opcode = OpCodes.Mul_Ovf;
            var four = new CodeInstruction(OpCodes.Ldc_I4_4);
            four.labels.AddRange(codes[size].labels); codes[size].labels.Clear();
            codes.InsertRange(size, new[] { four, new CodeInstruction(OpCodes.Mul_Ovf) });
            return codes;
        }
    }
}
