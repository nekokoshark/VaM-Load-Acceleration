using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using HarmonyLib;
using NativeLibraryDoorstop;
using UnityEngine;
using Quest3TriggerUI;

namespace VaM.Memory
{
    // Diagnostic candidate only. No delayed callbacks, lazy UI, asset reuse,
    // render-state changes, native allocation hooks or hold-flag changes.
    internal static class CriticalPathProbe
    {
        internal enum Kind
        {
            CuaCallback, CuaCreate, ScriptController, ScriptInit, ScriptUI,
            ScriptElement, BundleLoadAsset, CloneTransform, RendererRegister,
            CanvasScan, MeshRendererScan, MeshDerive, SkinInitMesh, SkinWrapInit,
            CompilerRequest, MaterialUVTemplate, DynamicLoad, SkinGPUInit, Count
        }

        internal struct Token
        {
            internal int Depth, Kind;
            internal bool Active;
        }

        internal struct Frame { internal long Start, Child; internal int Kind; }
        internal sealed class State
        {
            internal readonly Frame[] Frames = new Frame[64];
            internal readonly long[] Calls = new long[(int)Kind.Count];
            internal readonly long[] Ticks = new long[(int)Kind.Count];
            internal readonly long[] SelfTicks = new long[(int)Kind.Count];
            internal int Depth;
            internal long Errors, Overflows;
            internal string Identity;
        }
        [ThreadStatic] static State state;
        internal static Action<string> Logger;
        static Harmony harmony;
        static bool pending;

        internal static void Arm() { pending = true; }
        internal static void InstallPending()
        {
            if (!pending || !Doorstop.isInitted) return;
            pending = false;
            Install();
            if (Logger != null) Logger("[load-critical-path] installed targets=11 afterNativeInit=True");
        }

        internal static Token Enter(int kind)
        {
            State s = state;
            if (s == null || s.Depth == 0) return default(Token);
            return Push(s, kind);
        }

        static Token Push(State s, int kind)
        {
            if (s.Depth == s.Frames.Length) { s.Overflows++; return default(Token); }
            int depth = s.Depth++;
            s.Frames[depth] = new Frame { Start = Stopwatch.GetTimestamp(), Kind = kind };
            return new Token { Depth = depth, Kind = kind, Active = true };
        }

        internal static Token StartForTest(int kind)
        {
            if (state == null) state = new State();
            return Push(state, kind);
        }

        internal static void Exit(Token token, bool failed)
        {
            if (!token.Active) return;
            State s = state;
            if (s.Depth != token.Depth + 1 || s.Frames[token.Depth].Kind != token.Kind)
                throw new InvalidOperationException("Unbalanced critical-path timing scopes");
            Frame f = s.Frames[token.Depth];
            long elapsed = Stopwatch.GetTimestamp() - f.Start;
            s.Calls[token.Kind]++;
            s.Ticks[token.Kind] += elapsed;
            s.SelfTicks[token.Kind] += elapsed - f.Child;
            if (failed) s.Errors++;
            s.Frames[token.Depth] = default(Frame);
            s.Depth--;
            if (s.Depth != 0) s.Frames[s.Depth-1].Child += elapsed;
        }

        internal static State CurrentForTest() { return state; }
        internal static void ClearForTest() { state = null; }

        static bool Loading()
        {
            var controller = SuperController.singleton;
            return SceneLoadAccelerator.SceneLoadActive ||
                (!ReferenceEquals(controller, null) && controller.isLoading);
        }

        static int MethodKind(MethodBase method)
        {
            Type owner = method.DeclaringType;
            if (owner == typeof(CustomUnityAssetLoader))
                return method.Name == "BundleRequestCallback" ? (int)Kind.CuaCallback : (int)Kind.CuaCreate;
            if (owner == typeof(MVRPluginManager)) return (int)Kind.ScriptController;
            if (owner == typeof(MVRScript)) return (int)Kind.ScriptElement;
            if (owner == typeof(DAZMesh)) return (int)Kind.MeshDerive;
            if (owner == typeof(DAZSkinV2)) return method.Name == "SkinMeshGPUInit" ? (int)Kind.SkinGPUInit : (int)Kind.SkinInitMesh;
            if (owner == typeof(DAZSkinWrap)) return (int)Kind.SkinWrapInit;
            if (owner == typeof(MaterialOptions)) return (int)Kind.MaterialUVTemplate;
            if (owner == typeof(MeshVR.DAZDynamic)) return (int)Kind.DynamicLoad;
            return (int)Kind.CompilerRequest;
        }

        static void Before(MethodBase __originalMethod, object __instance, out Token __state)
        {
            State s = state;
            int kind = MethodKind(__originalMethod);
            if (s != null && s.Depth != 0) { __state = Push(s, kind); return; }
            if (!Loading()) { __state = default(Token); return; }
            if (s == null) state = s = new State();
            Array.Clear(s.Calls, 0, s.Calls.Length);
            Array.Clear(s.Ticks, 0, s.Ticks.Length);
            Array.Clear(s.SelfTicks, 0, s.SelfTicks.Length);
            s.Errors = s.Overflows = 0;
            // Names only, never Component/Atom/script/mesh owners in timing state.
            s.Identity = __instance == null ? __originalMethod.DeclaringType.FullName : __instance.GetType().FullName;
            __state = Push(s, kind);
        }

        static void BeforeScript(MethodBase __originalMethod, MVRPluginManager __instance, MVRPlugin mvrp, out Token __state)
        {
            Before(__originalMethod, __instance, out __state);
            if (__state.Active && __state.Depth == 0 && mvrp != null && mvrp.pluginURLJSON != null)
                state.Identity = mvrp.pluginURLJSON.val;
        }

        static Exception After(Exception __exception, Token __state)
        {
            Exit(__state, __exception != null);
            if (__state.Active && state.Depth == 0)
            {
                try { if (Logger != null) Logger(Format(state, __state.Kind)); }
                finally { state.Identity = null; }
            }
            return __exception;
        }

        internal static string Format(State s, int root)
        {
            var text = new StringBuilder("[load-critical-path] root=");
            text.Append((Kind)root);
            if (s.Identity != null) text.Append(" identity=").Append(s.Identity.Replace('\r',' ').Replace('\n',' '));
            text.Append(" errorScopes=").Append(s.Errors).Append(" overflows=").Append(s.Overflows);
            for (int i = 0; i < (int)Kind.Count; i++)
                if (s.Calls[i] != 0)
                {
                    text.Append(' ').Append((Kind)i).Append('=').Append(s.Calls[i]).Append(':');
                    text.Append((s.Ticks[i]*1000.0/Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                    text.Append('/');
                    text.Append((s.SelfTicks[i]*1000.0/Stopwatch.Frequency).ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
                }
            return text.Append(" unit=count:inclusiveMs/selfMs nativeCalls=unchanged holdFlags=unchanged").ToString();
        }

        internal static void InitScript(MVRScript script)
        {
            Token token = Enter((int)Kind.ScriptInit);
            bool failed = true;
            try { script.Init(); failed = false; }
            finally { Exit(token, failed); }
        }

        internal static void InitScriptUI(JSONStorable script)
        {
            Token token = Enter((int)Kind.ScriptUI);
            bool failed = true;
            try { script.InitUI(); failed = false; }
            finally { Exit(token, failed); }
        }

        static GameObject LoadGameObject(AssetBundle bundle, string name)
        {
            Token token = Enter((int)Kind.BundleLoadAsset);
            bool failed = true;
            try { GameObject result = bundle.LoadAsset<GameObject>(name); failed = false; return result; }
            finally { Exit(token, failed); }
        }

        static Transform CloneTransform(Transform source)
        {
            Token token = Enter((int)Kind.CloneTransform);
            bool failed = true;
            try { Transform result = UnityEngine.Object.Instantiate<Transform>(source); failed = false; return result; }
            finally { Exit(token, failed); }
        }

        static void RegisterRenderer(Atom owner, MeshRenderer renderer)
        {
            Token token = Enter((int)Kind.RendererRegister);
            bool failed = true;
            try { owner.RegisterDynamicMeshRenderer(renderer); failed = false; }
            finally { Exit(token, failed); }
        }

        static Canvas[] ScanCanvases(Component owner)
        {
            Token token = Enter((int)Kind.CanvasScan);
            bool failed = true;
            try { Canvas[] result = owner.GetComponentsInChildren<Canvas>(); failed = false; return result; }
            finally { Exit(token, failed); }
        }

        static MeshRenderer[] ScanMeshRenderers(Component owner)
        {
            Token token = Enter((int)Kind.MeshRendererScan);
            bool failed = true;
            try { MeshRenderer[] result = owner.GetComponentsInChildren<MeshRenderer>(); failed = false; return result; }
            finally { Exit(token, failed); }
        }

        internal static IEnumerable<CodeInstruction> Calls(IEnumerable<CodeInstruction> instructions, MethodBase __originalMethod)
        {
            bool cua = __originalMethod.DeclaringType == typeof(CustomUnityAssetLoader);
            int expected = cua ? 5 : 2, changed = 0;
            var result = new List<CodeInstruction>();
            foreach (CodeInstruction instruction in instructions)
            {
                var method = instruction.operand as MethodInfo;
                string replacement = null;
                if ((instruction.opcode == OpCodes.Call || instruction.opcode == OpCodes.Callvirt) && method != null)
                {
                    if (!cua)
                    {
                        if (method.DeclaringType == typeof(MVRScript) && method.Name == "Init" && method.GetParameters().Length == 0) replacement = "InitScript";
                        if (method.DeclaringType == typeof(JSONStorable) && method.Name == "InitUI" && method.GetParameters().Length == 0) replacement = "InitScriptUI";
                    }
                    if (cua)
                    {
                        if (method.DeclaringType == typeof(Atom) && method.Name == "RegisterDynamicMeshRenderer") replacement = "RegisterRenderer";
                        if (method.IsGenericMethod)
                        {
                            Type[] arguments = method.GetGenericArguments();
                            if (arguments.Length == 1 && method.GetParameters().Length == 1 &&
                                method.DeclaringType == typeof(AssetBundle) && method.Name == "LoadAsset" && arguments[0] == typeof(GameObject))
                                replacement = "LoadGameObject";
                            if (arguments.Length == 1 && method.GetParameters().Length == 1 &&
                                method.DeclaringType == typeof(UnityEngine.Object) && method.Name == "Instantiate" && arguments[0] == typeof(Transform))
                                replacement = "CloneTransform";
                            if (arguments.Length == 1 && method.GetParameters().Length == 0 &&
                                method.DeclaringType == typeof(Component) && method.Name == "GetComponentsInChildren")
                            {
                                if (arguments[0] == typeof(Canvas)) replacement = "ScanCanvases";
                                if (arguments[0] == typeof(MeshRenderer)) replacement = "ScanMeshRenderers";
                            }
                        }
                    }
                }
                var copy = new CodeInstruction(instruction);
                if (replacement != null)
                {
                    copy.opcode = OpCodes.Call;
                    copy.operand = AccessTools.Method(typeof(CriticalPathProbe), replacement);
                    changed++;
                }
                result.Add(copy);
            }
            if (changed != expected) throw new InvalidOperationException("Critical path call-site count " + changed + "/" + expected);
            return result;
        }

        internal static void Install()
        {
            if (harmony != null) return;
            var h = new Harmony("vam.memory.critical-path-probe");
            try
            {
                MethodInfo[] targets = {
                    AccessTools.Method(typeof(CustomUnityAssetLoader), "BundleRequestCallback"),
                    AccessTools.Method(typeof(CustomUnityAssetLoader), "SyncAssetName"),
                    AccessTools.Method(typeof(MVRPluginManager), "CreateScriptController"),
                    AccessTools.Method(typeof(MVRScript), "CreateUIElement"),
                    AccessTools.Method(typeof(DAZMesh), "DeriveMeshes"),
                    AccessTools.Method(typeof(DAZSkinV2), "InitMesh"),
                    AccessTools.Method(typeof(DAZSkinWrap), "InitMesh"),
                    AccessTools.Method(typeof(VaM.CompilerIsolation.CompilerService), "Compile"),
                    AccessTools.Method(typeof(MaterialOptions), "CreateUVTemplateTexture"),
                    AccessTools.Method(typeof(MeshVR.DAZDynamic), "Load"),
                    AccessTools.Method(typeof(DAZSkinV2), "SkinMeshGPUInit")
                };
                foreach (MethodInfo target in targets)
                {
                    if (target == null) throw new MissingMethodException("Critical path target");
                    bool calls = target.Name == "SyncAssetName" || target.Name == "CreateScriptController";
                    h.Patch(target, prefix:new HarmonyMethod(typeof(CriticalPathProbe), target.Name == "CreateScriptController" ? "BeforeScript" : "Before"),
                        transpiler:calls ? new HarmonyMethod(typeof(CriticalPathProbe), "Calls") : null,
                        finalizer:new HarmonyMethod(typeof(CriticalPathProbe), "After"));
                }
                harmony = h;
            }
            catch { h.UnpatchAll("vam.memory.critical-path-probe"); throw; }
        }

        internal static void Stop()
        {
            pending = false;
            if (harmony != null) { harmony.UnpatchAll("vam.memory.critical-path-probe"); harmony = null; }
            state = null;
            Logger = null;
        }
    }
}
