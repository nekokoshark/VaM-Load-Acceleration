using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace VaM.Memory {
    public static class TexturePathScan {
        public const string Id = "vam.memory.texture-path-order";
        public static bool IsTexturePath(string value) {
            return (value.Contains("/") || value.Contains("\\")) &&
                (value.EndsWith(".jpg") || value.EndsWith(".jpeg") || value.EndsWith(".png") ||
                 value.EndsWith(".tif") || value.EndsWith(".tiff") || value.EndsWith(".JPG") ||
                 value.EndsWith(".JPEG") || value.EndsWith(".PNG") || value.EndsWith(".TIF") || value.EndsWith(".TIFF"));
        }
        public static void Install(Harmony harmony) {
            foreach (string name in new[] { "_ScanJsonForTextureKeysAndPaths", "_ScanJsonForTexturePaths" })
                harmony.Patch(AccessTools.Method(typeof(SuperController), name),
                    transpiler: new HarmonyMethod(typeof(TexturePathScan), "Reorder"));
        }
        private static IEnumerable<CodeInstruction> Reorder(IEnumerable<CodeInstruction> instructions) {
            var code = new List<CodeInstruction>(instructions);
            int first = -1, last = -1, ends = 0, contains = 0;
            for (int i = 1; i < code.Count; i++) {
                if (code[i].opcode == OpCodes.Ldstr && Equals(code[i].operand, ".jpg")) {
                    if (first != -1) throw new InvalidOperationException("Multiple texture predicates");
                    first = i - 1;
                }
                if (first < 0) continue;
                var method = code[i].operand as MethodInfo;
                if (method == null || method.DeclaringType != typeof(string)) continue;
                if (method.Name == "EndsWith") ends++;
                if (method.Name == "Contains") {
                    contains++;
                    if (contains == 2) { last = i + 1; break; }
                }
            }
            if (first < 0 || last >= code.Count || ends != 10 || contains != 2 ||
                (code[last].opcode != OpCodes.Brfalse && code[last].opcode != OpCodes.Brfalse_S))
                throw new InvalidOperationException("Texture predicate IL changed");
            // No branch from outside may target an internal removed instruction.
            var internalLabels = new HashSet<Label>();
            for (int i = first + 1; i <= last; i++) foreach (Label label in code[i].labels) internalLabels.Add(label);
            for (int i = 0; i < code.Count; i++) {
                if (i >= first && i <= last) continue;
                if (code[i].operand is Label && internalLabels.Contains((Label)code[i].operand))
                    throw new InvalidOperationException("External texture predicate branch");
            }
            for (int i = first; i <= last; i++)
                if (code[i].blocks.Count != 0) throw new InvalidOperationException("Texture predicate exception boundary");
            var load = new CodeInstruction(code[first]);
            var branch = new CodeInstruction(code[last].opcode, code[last].operand);
            code.RemoveRange(first, last - first + 1);
            code.InsertRange(first, new[] { load,
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(TexturePathScan), "IsTexturePath")), branch });
            return code;
        }
    }
}
