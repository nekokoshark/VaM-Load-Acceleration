using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using MVR.FileManagement;
using Quest3TriggerUI;
using System.IO;
using BepInEx;

namespace VaM.Memory {
    public static partial class MorphSnapshotState {
        public const string Id = "vam.memory.morph-snapshot-state";
        private static readonly FieldInfo Enabled = AccessTools.Field(typeof(DAZMorphBank), "_dirEntryCacheEnabled");
        private static readonly FieldInfo Loaded = AccessTools.Field(typeof(DAZMorphBank), "currentLoadedMorphPackageUids");
        private static readonly FieldInfo Preload = AccessTools.Field(typeof(DAZMorphBank), "currentPreloadMorphPackageUids");
        private static readonly FieldInfo BadNames = AccessTools.Field(typeof(DAZMorphBank), "badMorphNames");
        private static readonly WeakObservationIndex<State> States = new WeakObservationIndex<State>();
        private static readonly WeakObservationIndex<State> ReadyBanks = new WeakObservationIndex<State>();
        private static readonly object Gate = new object();
        private static long generation, libraryGeneration, saved, staged, restored, rejected, freshTicks, freshBanks, refreshTicks, refreshCalls;
        [ThreadStatic] private static Scope active;
        public static Action<string> Log;

        public sealed class Scope {
            internal DAZMorphBank Bank;
            internal Scope Parent;
            internal long Generation, Started;
            internal object SavedSnapshot;
            internal State SavedState, ClonedState;
            internal string SourceBefore, SourceAfter;
            internal object PureSaved;
            internal long PureBytes;
            internal bool ImportFailed;
            internal string ImportKey;
            internal Dictionary<string,object> ImportRows;
            internal object ImportSelected;
            internal MVR.FileManagement.FileEntry ImportSelectedEntry;
            internal bool ImportDirty;
            internal bool ImportCapture;
            internal string[] ImportCulture;
            internal Dictionary<MVR.FileManagement.VarPackage,string> ImportPackageStamps;
            internal HashSet<MVR.FileManagement.VarPackage> ImportProtectedPackages;
            internal HashSet<string> ImportProtectedPaths;
        }
        internal sealed class Stamp {
            internal WeakReference Entry;
            internal long EntryTicks, PackageTicks, PackageSize;
        }
        internal sealed class State {
            internal string Folder, MetadataSource;
            internal long Generation;
            internal HashSet<string> Loaded, Preload;
            internal Stamp[] Directories;
        }

        public static void Install(Harmony harmony) {
            if (Enabled == null || Loaded == null || Preload == null || BadNames == null || BadNames.FieldType != typeof(HashSet<string>)) throw new MissingFieldException("Morph snapshot state schema");
            VerifyPureSchema();
            try {
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "Init"),
                prefix: new HarmonyMethod(typeof(MorphSnapshotState), "Begin"),
                finalizer: new HarmonyMethod(typeof(MorphSnapshotState), "End"),
                transpiler: new HarmonyMethod(typeof(MorphSnapshotState), "ReuseLookup"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "SaveMorphSnapshots"),
                postfix: new HarmonyMethod(typeof(MorphSnapshotState), "Saved"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "CloneMorphsFromCache"),
                postfix: new HarmonyMethod(typeof(MorphSnapshotState), "Cloned"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "RuntimeImportMorph"),
                finalizer: new HarmonyMethod(typeof(MorphSnapshotState), "ImportEnded"),
                transpiler: new HarmonyMethod(typeof(MorphSnapshotState), "ReplayImport"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "RefreshPackageMorphs"),
                prefix: new HarmonyMethod(typeof(MorphSnapshotState), "RefreshBegin"),
                finalizer: new HarmonyMethod(typeof(MorphSnapshotState), "RefreshEnd"));
            foreach (MethodInfo method in new[] {
                AccessTools.Method(typeof(FileManager), "Refresh"),
                AccessTools.Method(typeof(FileManager), "RegisterPackage"),
                AccessTools.Method(typeof(FileManager), "UnregisterPackage") })
                harmony.Patch(method, prefix: new HarmonyMethod(typeof(MorphSnapshotState), "Invalidate"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "EnableDirectoryCache"),
                prefix: new HarmonyMethod(typeof(MorphSnapshotState), "BeginLoad"));
            harmony.Patch(AccessTools.Method(typeof(DAZMorphBank), "DisableDirectoryCache"),
                postfix: new HarmonyMethod(typeof(MorphSnapshotState), "Finish"));
            } catch(Exception original) {
                try { harmony.UnpatchAll(Id); } catch(Exception cleanup) { original.Data["unpatchError"]=cleanup.ToString(); }
                Invalidate();throw;
            }
        }
        public static void Invalidate() {
            lock (Gate) { generation++; libraryGeneration++; States.Clear(); ReadyBanks.Clear(); ClearMetadata(); }
        }
        private static void BeginLoad() {
            lock (Gate) { generation++; States.Clear(); }
            saved = staged = restored = rejected = freshTicks = freshBanks = refreshTicks = refreshCalls = 0;
        }
        private static void Finish() {
            if (Log != null) Log("[cold-load-morph] freshBanks=" + freshBanks + " freshInclusiveMs=" + Ms(freshTicks) +
                " refreshCalls=" + refreshCalls + " refreshInclusiveMs=" + Ms(refreshTicks) +
                " saved=" + saved + " stateStaged=" + staged + " stateRestoredBeforeCacheClose=" + restored + " rejected=" + rejected + " snapshotKeys=weak importReceipts=" + ImportReceiptStats() + " metadataReuse=" + MetadataStats());
            if (Log != null) Log("[asset-anatomy-result] " + AssetAnatomyPass.Stats());
            // Pending bank receipts own no bank/snapshot. First Refresh may run after this cache closes.
            lock (Gate) { generation++; States.Clear(); }
        }
        private static string Ms(long ticks) {
            return (ticks * 1000.0 / Stopwatch.Frequency).ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
        }
        private static void Begin(DAZMorphBank __instance, bool ___wasInit, ref Scope __state) {
            if (___wasInit || !(bool)Enabled.GetValue(null)) return;
            __state = new Scope { Bank = __instance, Parent = active, Generation = generation, Started = Stopwatch.GetTimestamp() };
            active = __state;
            __state.SourceBefore = ScopeSourceSignature(__instance.autoImportFolder);
        }
        private static void Saved(DAZMorphBank __instance, object __result) {
            Scope scope = active;
            if (scope == null || !ReferenceEquals(scope.Bank, __instance) || __result == null) return;
            scope.SavedSnapshot = __result;
            scope.SavedState = Capture(__instance, libraryGeneration);
            scope.SourceAfter = ScopeSourceSignature(__instance.autoImportFolder);
            var bad = (HashSet<string>)BadNames.GetValue(__instance);
            // Snapshots omit rejected names and import diagnostics. Do not persist an incomplete build.
            if(importRoot == null && !scope.ImportFailed && (bad == null || bad.Count == 0) && scope.SavedState != null && scope.SourceBefore != null && scope.SourceBefore == scope.SourceAfter)
                scope.PureSaved = PureCopy(__result, out scope.PureBytes);
        }
        private static Exception ImportEnded(DAZMorphBank __instance, bool __result, Exception __exception) {
            Scope scope = active;
            if (scope != null && ReferenceEquals(scope.Bank, __instance) && (!__result || __exception != null)) scope.ImportFailed = true;
            return __exception;
        }
        private static void Cloned(DAZMorphBank __instance, object __0, bool __result) {
            Scope scope = active;
            if (!__result || scope == null || !ReferenceEquals(scope.Bank, __instance)) return;
            lock (Gate) States.TryGetValue(__0, out scope.ClonedState);
        }
        private static Exception End(Exception __exception, Scope __state) {
            if (__state == null) return __exception;
            try {
                freshBanks++; freshTicks += Stopwatch.GetTimestamp() - __state.Started;
                CloseImportScope(__state, __exception);
                if (__exception != null || __state.Generation != generation || !(bool)Enabled.GetValue(null)) return __exception;
                if (__state.SavedState != null) {
                    lock (Gate) {
                        if (States.Count >= 128) States.Clear();
                        States[__state.SavedSnapshot] = __state.SavedState;
                        saved++;
                        PublishMetadata(__state);
                    }
                }
                State state = __state.ClonedState;
                if (state != null) {
                    if (Matches(__state.Bank, state)) {
                        // Do not publish sets yet: directory/preload state can change before the first Refresh.
                        lock (Gate) {
                            if (ReadyBanks.Count >= 128) ReadyBanks.Clear();
                            ReadyBanks[__state.Bank] = state;
                            staged++;
                        }
                    } else rejected++;
                }
                return __exception;
            } finally {
                active = __state.Parent;
                __state.Bank = null; __state.Parent = null; __state.SavedSnapshot = null;
                __state.SavedState = null; __state.ClonedState = null;
                __state.PureSaved=null; __state.SourceBefore=__state.SourceAfter=null;
                __state.ImportRows=null; __state.ImportSelected=null; __state.ImportSelectedEntry=null; __state.ImportKey=null;
                __state.ImportCulture=null; __state.ImportProtectedPackages=null; __state.ImportProtectedPaths=null;
            }
        }
        private static State Capture(DAZMorphBank bank, long epoch) {
            var loaded = Loaded.GetValue(bank) as HashSet<string>;
            var preload = Preload.GetValue(bank) as HashSet<string>;
            if (loaded == null || preload == null || string.IsNullOrEmpty(bank.autoImportFolder)) return null;
            var dirs = FileManager.FindVarDirectories(bank.autoImportFolder);
            var actualLoaded = new HashSet<string>(); var actualPreload = new HashSet<string>();
            var stamps = new Stamp[dirs.Count];
            for (int i = 0; i < dirs.Count; i++) {
                VarDirectoryEntry entry = dirs[i]; VarPackage package = entry.Package;
                if (package.forceRefresh) return null;
                actualLoaded.Add(package.Uid);
                if (package.Group.GetCustomOption("preloadMorphs")) actualPreload.Add(package.Uid);
                stamps[i] = new Stamp { Entry = new WeakReference(entry), EntryTicks = entry.LastWriteTime.Ticks,
                    PackageTicks = package.LastWriteTime.Ticks, PackageSize = package.Size };
            }
            if (!loaded.SetEquals(actualLoaded) || !preload.SetEquals(actualPreload)) return null;
            return new State { Folder = bank.autoImportFolder, Generation = epoch,
                Loaded = new HashSet<string>(loaded, loaded.Comparer), Preload = new HashSet<string>(preload, preload.Comparer),
                Directories = stamps };
        }
        private static bool Matches(DAZMorphBank bank, State state) {
            if (state.Generation != libraryGeneration || state.Folder != bank.autoImportFolder) return false;
            var dirs = FileManager.FindVarDirectories(bank.autoImportFolder);
            if (dirs.Count != state.Directories.Length) return false;
            var loaded = new HashSet<string>(); var preload = new HashSet<string>();
            for (int i = 0; i < dirs.Count; i++) {
                VarDirectoryEntry entry = dirs[i]; VarPackage package = entry.Package;
                Stamp stamp = state.Directories[i];
                if (!ReferenceEquals(stamp.Entry.Target, entry) || entry.LastWriteTime.Ticks != stamp.EntryTicks ||
                    package.LastWriteTime.Ticks != stamp.PackageTicks || package.Size != stamp.PackageSize || package.forceRefresh) return false;
                loaded.Add(package.Uid);
                if (package.Group.GetCustomOption("preloadMorphs")) preload.Add(package.Uid);
            }
            return state.Loaded.SetEquals(loaded) && state.Preload.SetEquals(preload) &&
                (state.MetadataSource == null || state.MetadataSource == SourceSignature(bank.autoImportFolder));
        }
        private static void RefreshBegin(DAZMorphBank __instance, ref long __state) {
            __state = Stopwatch.GetTimestamp();
            State state;
            lock (Gate) {
                if (!ReadyBanks.TryGetValue(__instance, out state) || state == null) return;
                ReadyBanks[__instance] = null;
            }
            if (Loaded.GetValue(__instance) == null && Preload.GetValue(__instance) == null && Matches(__instance, state)) {
                // Validate at consumption time, preserve foreign field writes, and keep both mutable sets private.
                Loaded.SetValue(__instance, new HashSet<string>(state.Loaded, state.Loaded.Comparer));
                Preload.SetValue(__instance, new HashSet<string>(state.Preload, state.Preload.Comparer));
                restored++;
                if (Log != null) Log("[cold-load-morph-state] inheritedAtFirstRefresh=True libraryGeneration=" + libraryGeneration);
            } else rejected++;
        }
        private static void RefreshEnd(long __state) { refreshCalls++; refreshTicks += Stopwatch.GetTimestamp() - __state; }
    }
    public sealed partial class MemoryPlugin {
        private void InstallColdSceneLoad() {
            if (Config.Bind("SceneLoading", "TexturePathPredicate", true, "Filter non-path scalars before original image suffix checks.").Value)
                InstallModule("TexturePathPredicate", delegate { TexturePathScan.Install(new Harmony(TexturePathScan.Id)); });
            if (Config.Bind("SceneLoading", "MorphSnapshotPackageState", true, "Inherit completed package state with private sets and scene-scoped weak snapshot keys.").Value)
                InstallModule("MorphSnapshotPackageState", delegate {
                    MorphSnapshotState.Log = delegate(string text) { Logger.LogInfo(text); };
                    MorphSnapshotState.Install(new Harmony(MorphSnapshotState.Id));
                    if(Config.Bind("SceneLoading","MorphImportDiskReceipts",true,"Replay pre-filter pure metadata with per-package physical source stamps; original import, diagnostics and links remain.").Value)
                        MorphSnapshotState.ConfigureImportReceipts(MemoryCachePaths.DirectoryFor("MorphImports"));
                });
            Logger.LogInfo("[cold-load] generic=True texturePathPredicate=integrated morphSnapshotState=integrated sceneNameChecks=0 newDlls=0");
        }
    }
}
