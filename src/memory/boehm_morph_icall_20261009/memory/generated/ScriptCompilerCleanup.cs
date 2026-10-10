using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Threading;
using BepInEx;
using DynamicCSharp.Compiler;
using HarmonyLib;
using Mono.CSharp;

namespace ScriptCompilerCleanup
{
    // Clear compilation-only roots after the original compiler has finalized output and diagnostics.
    // Assemblies, script instances, bytecode, and per-plugin static isolation remain original.
    internal static class Feature
    {
        internal static Action<string> Logger;
        private static Harmony harmony;
        private static long cleaned;
        private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        [ThreadStatic] private static int compilerScope;

        private static string Hash(string path)
        {
            using (var stream = File.OpenRead(path))
            using (var sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }
        internal static MethodInfo Target()
        {
            return typeof(ScriptCompiler).Assembly.GetType("DynamicCSharp.Compiler.McsCompiler")
                .GetMethod("CompileFromSettings", All, null, new[] { typeof(CompilerSettings), typeof(bool) }, null);
        }
        private static Exception Finished(CompilerSettings settings, Exception __exception)
        {
            // The original ScriptCompiler holds this lock throughout this original compiler call.
            // Direct external compiler users are not changed by this feature.
            if (compilerScope == 0) return __exception;
            var root = RootContext.ToplevelTypes;
            if (root == null || !ReferenceEquals(root.Compiler.Settings, settings)) return __exception;
            CompilerCallableEntryPoint.Reset();
            RootContext.ToplevelTypes = null;
            long count = Interlocked.Increment(ref cleaned);
            if (Logger != null && (count <= 3 || count % 32 == 0))
                Logger("[script-compiler-cleanup] completed=" + count + " transientRoots=cleared assemblies=unchanged countsNotBytes=True");
            return __exception;
        }
        private static void EnterScope() { compilerScope++; }
        private static Exception LeaveScope(Exception __exception) { compilerScope--; return __exception; }
        internal static void Install()
        {
            if (harmony != null) return;
            if (Hash(typeof(ScriptCompiler).Assembly.Location) != "2440172dcd4fc5bf71b677fc464cc26d7ea0e8d5289209c37217f7870df6875c" ||
                Hash(typeof(RootContext).Assembly.Location) != "1937721e54e1ab5cda1a1ea86b4e3f6b4160d5e05d1cee3ea9aeff2298a4890d")
                throw new InvalidOperationException("Compiler input identity differs from the verified original");
            var target = Target();
            if (target == null || target.ReturnType != typeof(CompilerResults)) throw new MissingMethodException("Original CompileFromSettings");
            var patches = Harmony.GetPatchInfo(target);
            if (patches != null && patches.Owners.Count != 0) throw new InvalidOperationException("CompileFromSettings already patched");
            var sources = typeof(ScriptCompiler).GetMethod("CompileSources");
            var files = typeof(ScriptCompiler).GetMethod("CompileFiles");
            foreach (var entry in new[] { sources, files })
            {
                var info = Harmony.GetPatchInfo(entry);
                if (info != null && info.Owners.Count != 0) throw new InvalidOperationException("Original compiler scope already patched");
            }
            harmony = new Harmony("vam.script.compiler.transient.cleanup.cold.v1");
            try
            {
                foreach (var entry in new[] { sources, files })
                    harmony.Patch(entry, prefix: new HarmonyMethod(typeof(Feature), "EnterScope"), finalizer: new HarmonyMethod(typeof(Feature), "LeaveScope"));
                harmony.Patch(target, finalizer: new HarmonyMethod(typeof(Feature), "Finished"));
            }
            catch { harmony.UnpatchAll(harmony.Id); harmony = null; throw; }
        }
        internal static void Remove()
        {
            if (harmony == null) return;
            harmony.UnpatchAll(harmony.Id);
            harmony = null;
        }
    }

}
