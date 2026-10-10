using System;
using System.CodeDom.Compiler;
using System.Reflection;
using DynamicCSharp.Compiler;
using HarmonyLib;
using Mono.CSharp;

namespace VaM.CompilerIsolation
{
    public static class CompilerHooks
    {
        static readonly BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly Type MarshalType = typeof(ScriptCompiler).Assembly.GetType("DynamicCSharp.Compiler.McsMarshal", true);
        static readonly Type CompilerType = typeof(ScriptCompiler).Assembly.GetType("DynamicCSharp.Compiler.McsCompiler", true);
        static readonly FieldInfo OutputDirectory = MarshalType.GetField("outputDirectory", All), GenerateSymbols = MarshalType.GetField("generateSymbols", All);
        static readonly FieldInfo AssemblyData = MarshalType.GetField("assemblyData", All), SymbolsData = MarshalType.GetField("symbolsData", All);
        static readonly Type ErrorType = typeof(ScriptCompiler).Assembly.GetType("DynamicCSharp.Compiler.ScriptCompilerError", true);
        static readonly object CompilerLock = typeof(ScriptCompiler).GetField("compilerLock", All).GetValue(null);
        static Harmony harmony;
        public static CompilerService Service;
        [ThreadStatic] static bool active;
        [ThreadStatic] static CompileReply reply;
        public sealed class NativeScope { internal bool Active; internal CompileReply Reply; }

        static bool FromSettings(CompilerSettings settings, bool generateInMemory, ref CompilerResults __result)
        {
            if (!active || generateInMemory) return true;
            try { reply = Service.Compile(settings); }
            catch (System.IO.IOException e)
            { reply = new CompileReply(); reply.Errors.Add(new CompilerError("", -1, -1, "0", "Compiler worker IO: " + e.Message)); }
            catch (System.ComponentModel.Win32Exception e)
            { reply = new CompileReply(); reply.Errors.Add(new CompilerError("", -1, -1, "0", "Compiler worker start: " + e.Message)); }
            __result = new CompilerResults(new TempFileCollection());
            foreach (CompilerError e in reply.Errors) __result.Errors.Add(e);
            return false;
        }
        static bool Shared(object __instance, Func<CompilerParameters, string[], CompilerResults> compileMethod,
            CompilerParameters parameters, string[] sourceOrFiles, ref object __result, out NativeScope __state)
        {
            __state = null;
            // Retain the real parent GetSettings (including its output counter), source
            // construction, ScriptCompiler lock/reset/diagnostics, and ScriptDomain loader.
            if (parameters.GenerateInMemory || parameters.GenerateExecutable ||
                (compileMethod.Method.Name != "CompileAssemblyFromFileBatch" && compileMethod.Method.Name != "CompileAssemblyFromSourceBatch") || !Service.CanOffload(parameters))
            {
                if (active) { __state = new NativeScope { Active = active, Reply = reply }; active = false; reply = null; }
                return true;
            }
            CompilerType.GetProperty("OutputDirectory", All).SetValue(null, OutputDirectory.GetValue(__instance), null);
            CompilerType.GetProperty("GenerateSymbols", All).SetValue(null, GenerateSymbols.GetValue(__instance), null);
            AssemblyData.SetValue(__instance, null); SymbolsData.SetValue(__instance, null);
            CompilerResults results;
            bool previousActive = active; CompileReply previousReply = reply;
            active = true; reply = null;
            try
            {
                results = compileMethod(parameters, sourceOrFiles);
                if (reply == null) throw new InvalidOperationException("Original compiler did not enter verified worker route");
                parameters.ReferencedAssemblies.Clear();
                var errors = Array.CreateInstance(ErrorType, results.Errors.Count); int i = 0;
                foreach (CompilerError e in results.Errors)
                {
                    object row = Activator.CreateInstance(ErrorType);
                    string[] names = { "errorCode", "errorText", "fileName", "line", "column", "isWarning" };
                    object[] values = { e.ErrorNumber, e.ErrorText, e.FileName, e.Line, e.Column, e.IsWarning };
                    for (int j = 0; j < names.Length; j++) ErrorType.GetField(names[j], All).SetValue(row, values[j]);
                    errors.SetValue(row, i++);
                }
                AssemblyData.SetValue(__instance, reply.Assembly); SymbolsData.SetValue(__instance, reply.Symbols);
                __result = errors; return false;
            }
            finally { active = previousActive; reply = previousReply; }
        }
        static Exception RestoreNativeScope(NativeScope __state, Exception __exception)
        { if (__state != null) { active = __state.Active; reply = __state.Reply; } return __exception; }
        public static void Install(CompilerService service)
        { lock (CompilerLock) InstallLocked(service); }
        static void InstallLocked(CompilerService service)
        {
            if (harmony != null) throw new InvalidOperationException("Compiler worker already installed");
            var shared = MarshalType.GetMethod("CompileShared", All);
            var compile = CompilerType.GetMethod("CompileFromSettings", All);
            if (shared == null || compile == null || AssemblyData == null || SymbolsData == null) throw new MissingMemberException("Compiler adapter bindings");
            var info = Harmony.GetPatchInfo(shared);
            if (info != null && info.Owners.Count != 0) throw new InvalidOperationException("CompileShared has another adapter");
            info = Harmony.GetPatchInfo(compile);
            if (info != null) foreach (string owner in info.Owners)
                if (owner != "vam.script.compiler.transient.cleanup.cold.v1") throw new InvalidOperationException("Unverified compiler combination: " + owner);
            Service = service; harmony = new Harmony("vam.memory.compiler.cached-worker.v2");
            try
            {
                harmony.Patch(shared, prefix: new HarmonyMethod(typeof(CompilerHooks), "Shared"), finalizer: new HarmonyMethod(typeof(CompilerHooks), "RestoreNativeScope"));
                harmony.Patch(compile, prefix: new HarmonyMethod(typeof(CompilerHooks), "FromSettings"));
            }
            catch { Remove(); throw; }
        }
        public static void Remove()
        {
            lock (CompilerLock) RemoveLocked();
        }
        static void RemoveLocked()
        {
            if (harmony != null) harmony.UnpatchAll(harmony.Id);
            harmony = null;
            if (Service != null) Service.Dispose(); Service = null;
        }
    }
}
