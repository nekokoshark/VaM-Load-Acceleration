using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace VaM.Memory {
    public static class BoehmOptimizations {
        private static readonly object Gate = new object();
        private static readonly Queue<Assembly> Pending = new Queue<Assembly>();
        private static readonly HashSet<MethodInfo> Consumers = new HashSet<MethodInfo>();
        private static volatile bool pending;
        private static Harmony morph;
        public static Action<string> Log;

        public static void ArmMorph() {
            if (morph != null) return;
            morph = new Harmony("local.vam.startup-morph-index");
            StartupMorphIndex.Runtime.Log = Log;
            AppDomain.CurrentDomain.AssemblyLoad += Loaded;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies()) Queue(assembly);
            Tick();
        }
        private static void Queue(Assembly assembly) {
            lock (Gate) { Pending.Enqueue(assembly); pending = true; }
        }
        private static void Loaded(object sender, AssemblyLoadEventArgs args) { Queue(args.LoadedAssembly); }
        public static void Tick() {
            if (!pending) return;
            while (true) {
                Assembly assembly;
                lock (Gate) {
                    if (Pending.Count == 0) { pending = false; return; }
                    assembly = Pending.Dequeue();
                }
                Type type = assembly.GetType("JayJayWon.MorphCache", false);
                if (type == null) continue;
                MethodInfo method = type.GetMethod("GetMorphUIDs", BindingFlags.NonPublic | BindingFlags.Static);
                if (method == null || !Consumers.Add(method)) continue;
                StartupMorphIndex.Runtime.Register(method);
                morph.Patch(method, prefix: new HarmonyMethod(typeof(StartupMorphIndex.Runtime), "Prefix"),
                    finalizer: new HarmonyMethod(typeof(StartupMorphIndex.Runtime), "Finalizer"));
                if (Log != null) Log("[startup-morph-index] integrated consumer=" + type.FullName + " fields=11 firstCallOnly=True");
            }
        }
        public static void Stop() {
            AppDomain.CurrentDomain.AssemblyLoad -= Loaded;
            lock (Gate) { Pending.Clear(); pending = false; }
        }
    }

    public sealed partial class MemoryPlugin {
        private bool packageStreamingEnabled;
        private void InstallBoehmOptimizations() {
            BoehmOptimizations.Log = delegate(string text) { Logger.LogInfo(text); };
            if (Config.Bind("BoehmOptimization", "MorphScalarIndex", true, "First-call scalar index; original cold/changed input path remains.").Value)
                InstallModule("MorphScalarIndex", BoehmOptimizations.ArmMorph);
            if (Config.Bind("BoehmOptimization", "PackageStream", true, "Stream package JSON without retaining its full input string.").Value)
                InstallModule("PackageStream", delegate {
                    PackageStreamRuntime.Install(new Harmony("local.vam.startup-package-stream"));
                    packageStreamingEnabled = true;
                    Logger.LogInfo("[package-stream] tokenStorage=parseLocal supersedes=legacyFixedLexemeParser cacheLifetime=unchanged");
                });
            if (Config.Bind("BoehmOptimization", "BatchCapacityFinally", true, "Checked four-pass descriptor capacity and exception release; no moving-GC pins.").Value)
                InstallModule("BatchCapacityFinally", BoehmBatch.Arm);
            if (Config.Bind("BoehmOptimization", "ListingNames", true, "Project fresh directory names without entry metadata objects.").Value)
                InstallModule("ListingNames", delegate { ListingNamesRuntime.Install(new Harmony(ListingNamesRuntime.Id)); });
            if (Config.Bind("BoehmOptimization", "CanonicalPaths", true, "Preserve path semantics and foreign callbacks with less transient allocation.").Value)
                InstallModule("CanonicalPaths", delegate { CanonicalPathsRuntime.Install(new Harmony(CanonicalPathsRuntime.Id)); });
            if (Config.Bind("BoehmOptimization", "JsonCapacity", true, "Smaller original dictionary initial capacity; preserve callbacks.").Value)
                InstallModule("JsonCapacity", delegate { JsonCapacityRuntime.Install(new Harmony(JsonCapacityRuntime.Id)); });
            Logger.LogInfo("[boehm-optimizations] integrated assembly=VaM.Memory standaloneDlls=0 morphPolling=False tokenStorage=parseLocal");
        }
    }
}
