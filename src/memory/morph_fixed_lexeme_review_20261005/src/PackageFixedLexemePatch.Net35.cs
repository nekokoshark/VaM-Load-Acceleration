using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace VaM.MorphFixedLexemes
{
    // Compile against the user's actual VaM .NET 3.5 reference assemblies and
    // installed Harmony. This source has NOT been compiled in the review environment.
    // Does not replace JSON.Parse, cache ownership, LoadMetaFromJSON or PersonPrepared.
    public static class PackageFixedLexemePatch
    {
        public const string HarmonyId = "vam.morph.package-fixed-lexemes.v1";
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic |
                                         BindingFlags.Instance | BindingFlags.Static;
        private static readonly object Gate = new object();
        private static Harmony harmony;
        private static bool enabled;
        private static int activeCalls;
        private static long parses, failures, keyHits, typeHits, utf16Units;
        private static long keyCopyHits, keyCopyUnits;

        [ThreadStatic] private static string source;
        [ThreadStatic] private static int depth;
        [ThreadStatic] private static long threadKeyHits;
        [ThreadStatic] private static long threadTypeHits;
        [ThreadStatic] private static long threadUtf16Units;
        [ThreadStatic] private static long threadKeyCopyHits;
        [ThreadStatic] private static long threadKeyCopyUnits;

        public struct Counters
        {
            public long PackageParses, FailedParses, KeyHits, TypeHits, Utf16Units;
            public long KeyCopyHits, KeyCopyUnits;
            public int ActiveCalls;
            public bool Enabled;
        }

        public static Counters ReadCounters()
        {
            Counters c = new Counters();
            c.PackageParses = Interlocked.Read(ref parses);
            c.FailedParses = Interlocked.Read(ref failures);
            c.KeyHits = Interlocked.Read(ref keyHits);
            c.TypeHits = Interlocked.Read(ref typeHits);
            c.Utf16Units = Interlocked.Read(ref utf16Units);
            c.KeyCopyHits = Interlocked.Read(ref keyCopyHits);
            c.KeyCopyUnits = Interlocked.Read(ref keyCopyUnits);
            lock (Gate) { c.ActiveCalls = activeCalls; c.Enabled = enabled; }
            return c; // Numeric observer, not a consistent heap snapshot.
        }

        // Called ONLY in place of VarPackage.GetJSONCache's existing JSON.Parse call.
        // Calls the real JSON.Parse, so installed parser prefixes/postfixes and other
        // transpilers still participate. No private second parser or shadow tree.
        public static JSONNode ParsePackage(string text)
        {
            bool admitted;
            lock (Gate)
            {
                admitted = enabled;
                if (admitted) activeCalls++;
            }
            if (!admitted) return JSON.Parse(text);

            string previousSource = source;
            int previousDepth = depth;
            if (previousDepth == 0)
            {
                threadKeyHits = 0;
                threadTypeHits = 0;
                threadUtf16Units = 0;
                threadKeyCopyHits = threadKeyCopyUnits = 0;
            }
            source = text;
            depth = previousDepth + 1;
            Interlocked.Increment(ref parses);
            try
            {
                return JSON.Parse(text);
            }
            catch
            {
                Interlocked.Increment(ref failures);
                throw; // Original exception and original VarPackage catch remain.
            }
            finally
            {
                // Restore even on parser exception or nested same-package calls.
                source = previousSource;
                depth = previousDepth;
                if (previousDepth == 0)
                {
                    Interlocked.Add(ref keyHits, threadKeyHits);
                    Interlocked.Add(ref typeHits, threadTypeHits);
                    Interlocked.Add(ref utf16Units, threadUtf16Units);
                    Interlocked.Add(ref keyCopyHits, threadKeyCopyHits);
                    Interlocked.Add(ref keyCopyUnits, threadKeyCopyUnits);
                    threadKeyHits = threadTypeHits = threadUtf16Units = 0;
                    threadKeyCopyHits = threadKeyCopyUnits = 0;
                }
                lock (Gate) { activeCalls--; }
            }
        }

        // Replaces only the eight StringBuilder.ToString call sites in the supplied
        // JSONNode.Parse. An extra ldarg.0 supplies the exact parser input identity.
        // Outside the selected synchronous package parse, this is the original call.
        public static string Materialize(StringBuilder builder, string parserInput)
        {
            if (depth == 0 || !object.ReferenceEquals(source, parserInput))
                return builder.ToString();
            string literal;
            byte category;
            if (!FixedMorphLexemes.TryGet(builder, out literal, out category))
                return builder.ToString();
            if (category == 1) threadKeyHits++;
            else threadTypeHits++;
            threadUtf16Units += literal.Length;
            return literal; // IMPORTANT: ToString was never called on a hit.
        }

        // The included source copies a key with keyBuilder.Append(valueBuilder).
        // Against .NET 3.5 this is normally Append(object), which calls ToString.
        // Exact IL must confirm that overload. This removes that *temporary* copy
        // for the same fixed vocabulary; it is NOT additional retained-tree saving.
        public static StringBuilder AppendKeyToken(StringBuilder destination,
                                                    object value, string parserInput)
        {
            if (depth != 0 && object.ReferenceEquals(source, parserInput))
            {
                StringBuilder token = value as StringBuilder;
                string literal;
                byte category;
                if (token != null && FixedMorphLexemes.TryGet(token, out literal, out category))
                {
                    threadKeyCopyHits++;
                    threadKeyCopyUnits += literal.Length;
                    return destination.Append(literal);
                }
            }
            return destination.Append(value);
        }

        private static bool IsCall(CodeInstruction instruction, MethodInfo method)
        {
            return (instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt)
                && method.Equals(instruction.operand);
        }

        private static List<CodeInstruction> Copy(IEnumerable<CodeInstruction> input)
        {
            List<CodeInstruction> result = new List<CodeInstruction>();
            foreach (CodeInstruction c in input) result.Add(new CodeInstruction(c));
            return result;
        }

        private static IEnumerable<CodeInstruction> RewritePackage(
            IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> codes = Copy(instructions);
            MethodInfo parse = typeof(JSON).GetMethod("Parse", new Type[] { typeof(string) });
            MethodInfo replacement = typeof(PackageFixedLexemePatch).GetMethod("ParsePackage", All);
            int count = 0;
            foreach (CodeInstruction c in codes)
            {
                if (!IsCall(c, parse)) continue;
                c.opcode = OpCodes.Call;
                c.operand = replacement;
                count++;
                // Same stack shape; existing labels/EH stay attached.
            }
            if (count != 1)
                throw new InvalidDataException("GetJSONCache JSON.Parse anchor count=" + count);
            return codes;
        }

        private static int LocalIndex(CodeInstruction c)
        {
            if (c.opcode == OpCodes.Ldloc_0) return 0;
            if (c.opcode == OpCodes.Ldloc_1) return 1;
            if (c.opcode == OpCodes.Ldloc_2) return 2;
            if (c.opcode == OpCodes.Ldloc_3) return 3;
            if (c.opcode != OpCodes.Ldloc && c.opcode != OpCodes.Ldloc_S) return -1;
            LocalBuilder builder = c.operand as LocalBuilder;
            if (builder != null) return builder.LocalIndex;
            LocalVariableInfo variable = c.operand as LocalVariableInfo;
            if (variable != null) return variable.LocalIndex;
            if (c.operand is int) return (int)c.operand;
            if (c.operand is byte) return (byte)c.operand;
            if (c.operand is short) return (short)c.operand;
            return -1;
        }

        private static bool LoadsBuilder(CodeInstruction c, MethodBody body)
        {
            LocalBuilder local = c.operand as LocalBuilder;
            if (local != null && (c.opcode == OpCodes.Ldloc || c.opcode == OpCodes.Ldloc_S))
                return local.LocalType == typeof(StringBuilder);
            int n = LocalIndex(c);
            return n >= 0 && n < body.LocalVariables.Count &&
                   body.LocalVariables[n].LocalType == typeof(StringBuilder);
        }

        private static bool IsBuilderToString(List<CodeInstruction> codes, int i, MethodBody body)
        {
            CodeInstruction c = codes[i];
            if (c.opcode != OpCodes.Call && c.opcode != OpCodes.Callvirt) return false;
            MethodInfo m = c.operand as MethodInfo;
            if (m == null || m.Name != "ToString" || m.IsStatic ||
                m.ReturnType != typeof(string) || m.GetParameters().Length != 0)
                return false;
            // Some compilers encode the root virtual slot Object.ToString.
            if (m.DeclaringType != typeof(StringBuilder) && m.DeclaringType != typeof(object))
                return false;
            if (i == 0) return false;
            LocalBuilder local = codes[i - 1].operand as LocalBuilder;
            if (local != null && (codes[i - 1].opcode == OpCodes.Ldloc ||
                                  codes[i - 1].opcode == OpCodes.Ldloc_S))
                return local.LocalType == typeof(StringBuilder);
            int n = LocalIndex(codes[i - 1]);
            return n >= 0 && n < body.LocalVariables.Count &&
                   body.LocalVariables[n].LocalType == typeof(StringBuilder);
        }

        private static IEnumerable<CodeInstruction> RewriteParser(
            IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            MethodBody body = __originalMethod.GetMethodBody();
            if (!__originalMethod.IsStatic || body == null)
                throw new InvalidDataException("JSONNode.Parse method shape changed");
            List<CodeInstruction> codes = Copy(instructions);
            MethodInfo replacement = typeof(PackageFixedLexemePatch).GetMethod("Materialize", All);
            MethodInfo appendObject = typeof(StringBuilder).GetMethod("Append", new Type[] { typeof(object) });
            MethodInfo appendReplacement = typeof(PackageFixedLexemePatch).GetMethod("AppendKeyToken", All);
            int count = 0, appendCount = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                bool keyTransfer = IsCall(codes[i], appendObject) && i >= 2 &&
                    LoadsBuilder(codes[i - 1], body) && LoadsBuilder(codes[i - 2], body);
                if (!keyTransfer && !IsBuilderToString(codes, i, body)) continue;
                CodeInstruction originalCall = codes[i];
                if (originalCall.blocks.Count != 0)
                    throw new InvalidDataException("Parser token call crosses new EH boundary");
                CodeInstruction loadInput = new CodeInstruction(OpCodes.Ldarg_0);
                loadInput.labels.AddRange(originalCall.labels);
                originalCall.labels.Clear();
                codes.Insert(i, loadInput);
                i++;
                originalCall.opcode = OpCodes.Call;
                originalCall.operand = keyTransfer ? appendReplacement : replacement;
                if (keyTransfer) appendCount++; else count++;
            }
            // Derived from included source, NOT a claim about unseen production IL.
            if (count != 8)
                throw new InvalidDataException("JSONNode.Parse builder anchors=" + count + "; expected 8");
            if (appendCount != 1)
                throw new InvalidDataException("Parser key Append(object) anchors=" + appendCount + "; expected 1");
            return codes;
        }

        private static void RejectOtherGeneration(MethodBase method)
        {
            var info = Harmony.GetPatchInfo(method);
            if (info == null) return;
            foreach (string owner in info.Owners)
                if (owner == HarmonyId)
                    throw new InvalidOperationException("Prior fixed-lexeme generation must drain/uninstall first");
        }

        public static void Install()
        {
            lock (Gate)
            {
                if (harmony != null) { enabled = true; return; }
                MethodInfo get = typeof(VarPackage).GetMethod("GetJSONCache", new Type[] { typeof(string) });
                MethodInfo parse = typeof(JSONNode).GetMethod("Parse", new Type[] { typeof(string) });
                if (get == null || parse == null || get.ReturnType != typeof(JSONNode) ||
                    parse.ReturnType != typeof(JSONNode))
                    throw new MissingMethodException("Package/parser public signature changed");
                RejectOtherGeneration(get);
                RejectOtherGeneration(parse);
                Harmony instance = new Harmony(HarmonyId);
                try
                {
                    // Materialize is a no-op until a wrapper admits a parse.
                    instance.Patch(parse, transpiler: new HarmonyMethod(
                        typeof(PackageFixedLexemePatch).GetMethod("RewriteParser", All)));
                    instance.Patch(get, transpiler: new HarmonyMethod(
                        typeof(PackageFixedLexemePatch).GetMethod("RewritePackage", All)));
                    harmony = instance;
                    enabled = true;
                }
                catch
                {
                    enabled = false;
                    instance.UnpatchAll(HarmonyId); // Own owner only.
                    harmony = null;
                    throw;
                }
            }
        }

        // Stop admission immediately. Returning false means an admitted synchronous
        // call is still executing; retry at its actual completion, not after N seconds.
        public static bool TryShutdown()
        {
            lock (Gate)
            {
                enabled = false;
                if (activeCalls != 0) return false;
                if (harmony != null) harmony.UnpatchAll(HarmonyId);
                harmony = null;
                return true;
            }
        }

        // Content-equivalence probe only. Do not use as a live-game concurrency API.
        public static void SetEnabledForProbe(bool value)
        {
            lock (Gate)
            {
                if (harmony == null || activeCalls != 0)
                    throw new InvalidOperationException("Probe requires installed, idle patch");
                enabled = value;
            }
        }
    }
}
