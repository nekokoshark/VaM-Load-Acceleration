using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using Quest3TriggerUI;
using VaM.PersonPrepared.Runtime;
using VaM.MorphFixedLexemes;

namespace VaM.Memory
{
    [BepInPlugin("vam.memory", "VaM Memory", "1.8.26")]
    public sealed partial class MemoryPlugin : BaseUnityPlugin
    {
        private ConfigFile legacy;
        private bool started, person;
        private bool criticalTimingPending;
        private int startupFailures;
        private long reportAt;
        private string lastPerson;
        private string lastDelegateShare;
        private string lastAssetCreation;

        private ConfigEntry<T> Bind<T>(string section, string key, T value, string description)
        {
            T inherited = legacy.Bind<T>(section, key, value, description).Value;
            return Config.Bind<T>(section, key, inherited, description);
        }

        private bool ExistingSwitch(string path, string section, bool value)
        {
            var old = new ConfigFile(Path.Combine(Paths.ConfigPath, path), false);
            old.SaveOnConfigSet = false;
            return old.Bind<bool>(section, "Enabled", value).Value;
        }

        private void Awake()
        {
            // Process lifetime: disk updates/removal become effective only on the next start.
            Quest3TriggerUIPlugin.Instance = this;
            Quest3TriggerUIPlugin.Log = Logger;
            legacy = new ConfigFile(Path.Combine(Paths.ConfigPath, "local.vam.quest3-trigger-ui.cfg"), false);
            legacy.SaveOnConfigSet = false;
            InstallModule("CacheRoot",delegate {MemoryCachePaths.Initialize(Paths.GameRootPath);Logger.LogInfo("[memory-cache-root] root="+MemoryCachePaths.Root+" nativeFolders=unchanged pruning=False");});
            if (Config.Bind("Diagnostics", "CriticalPathTiming", false, "Explicit load-time call-site attribution; not a speed optimization.").Value)
                InstallModule("CriticalPathTiming", InstallCriticalPathTiming);
            BindModules();
            ConfigurePackageTreeDisk();
            InstallBoehmOptimizations();
            InstallColdSceneLoad();
            InstallColdFileLookups();
            InstallColdAnatomy();
            InstallAssetAnatomyPass();
            InstallPathCharacterSearch();
            InstallAssetEntryMetadata();
            InstallPackageNegativeLookup();
            InstallVarDirectorySelection();
            Logger.LogInfo("[observation-lifetime] active identityContainers=15 sampledKeys=weak scalarReceipts=retained resourceLifecycle=unchanged");
            legacy = null;
            if (Config.Bind("ResourceLifetime", "ImmutableDelegateGraphs", true,
                "Keep legacy delegate search/snapshot history out of active subscription roots; effective next startup.").Value)
                InstallModule("DelegateGraphLifetime", delegate
                {
                    DelegateGraphLifetime.ShareLog = message => Logger.LogInfo(message);
                    DelegateGraphLifetime.Install();
                    Logger.LogInfo("[delegate-graphs] active combine=clean-left-sharing snapshot=immutable search=local cloneHistory=retired actualSubscribers=preserved gcPolicy=unchanged");
                });
            InstallModule("VarEntryLazyFlags", VarEntryLazyFlags.Install);
            ScriptCompilerCleanup.Feature.Logger = message => Logger.LogInfo(message);
            InstallModule("ScriptCompilerCleanup", ScriptCompilerCleanup.Feature.Install);
            CuaPreloadFix.Feature.Logger = message => Logger.LogInfo(message);
            InstallModule("CuaPreloadLease", CuaPreloadFix.Feature.Install);
            InstallModules();
            if (Config.Bind("ResourceLifetime", "EventRootRetirement", true, "Retire exact destroyed-instance descriptor roots after explicit unload.").Value)
                InstallModule("InstanceRootRetirement", InstanceRootRetirement.Install);
            if (Config.Bind("ResourceLifetime", "PresetOwnerRetirement", true, "Retire destroyed instance registrations from preset-owned lists and indexes.").Value)
                InstallModule("PresetOwnerRetirement", PresetOwnerRetirement.Install);
            InstallModule("BundleDiskCache", delegate
            {
                string support = SupportFiles.Extract(Path.Combine(Paths.BepInExRootPath, "cache/VaMMemory/support"));
                string converter = Path.Combine(support, "bundle/Memory1823.py");
                CompilerRuntime.Extract("VaM.Memory.BundleConverter", converter);
                VamBundleDiskCache.Backend.Configure(Path.Combine(support, "python/python.exe"),
                    converter, MemoryCachePaths.DirectoryFor("BundleDiskCache"));
                VamBundleDiskCache.Feature.Logger = message => Logger.LogInfo(message);
                VamBundleDiskCache.Feature.Install();
            });
            if (Config.Bind("Compiler", "IsolatedWorker", true, "Compile scripts in a bounded original-Mono worker; effective next startup.").Value)
                InstallModule("CompilerWorker", delegate
                {
                    CompilerRuntime.Install(Config.Bind("Compiler", "BytecodeCache", true, "Cache code templates, preserving distinct loaded assemblies/statics.").Value,
                        Config.Bind("Compiler", "WorkerBudgetMiB", 256, "Worker private-memory soft budget; 128 to 512 MiB, checked after each request.").Value,
                        message => Logger.LogInfo(message));
                });
            if (Config.Bind("Morph", "FixedLexemes", ExistingSwitch("vam.package.fixedlexemes.cfg", "Optimization", true), "Reuse fixed Morph JSON tokens.").Value && !packageStreamingEnabled)
                InstallModule("FixedLexemes", PackageFixedLexemePatch.Install);
            if (Config.Bind("Person", "PreparedOwnership", ExistingSwitch("vam.personprepared.integration.cfg", "Integration", true), "Transfer prepared Person acquisitions to the real Atom.").Value)
            {
                InstallModule("PersonPrepared", delegate
                { RuntimeControl.InstallForIntegration(); RuntimeControl.OpenForIntegration(); person = true; });
            }
            if (Config.Bind("Reclamation", "LoadTailTransactions", true,
                "Coalesce automatic cleanup after consumers/writers/retirement drain; no long-idle handoff. Effective next startup.").Value)
                InstallModule("LoadReclamation", LoadReclamation.Install);
            started = true;
            Logger.LogInfo("[vam-memory] active version=1.8.26 entry=single lifetime=process config=preserved uninstall=nextStart startupFailures=" + startupFailures);
        }

        private void InstallModule(string name, Action action)
        {
            try { action(); }
            catch (Exception error)
            {
                startupFailures++;
                Logger.LogError("[vam-memory] module=" + name + " startup failed: " + error);
            }
        }

        private string lastBundlePreparation;
        private void Update()
        {
            if (!started) return;
            if (criticalTimingPending && NativeLibraryDoorstop.Doorstop.isInitted)
            {
                criticalTimingPending = false;
                InstallModule("CriticalPathNativeReady", CriticalPathProbe.InstallPending);
            }
            BoehmOptimizations.Tick();
            if (BoehmBatch.Ready) InstallModule("BatchNativeReady", BoehmBatch.InstallPending);
            TickModules();
            InstanceRootRetirement.Tick();
            PresetOwnerRetirement.Tick();
            if (UnityEngine.Time.frameCount % 300 == 0) VamBundleDiskCache.Feature.Sweep();
            if (person) RuntimeControl.Pump();
            LoadReclamation.Tick();
            var controller=SuperController.singleton;
            CuaPreloadFix.Feature.Tick(!ReferenceEquals(controller,null) && controller.isLoading);
            ClassificationDiskReceipts.TickIdle(DateTime.UtcNow.Ticks,
                ReferenceEquals(controller,null) || controller.isLoading || SceneLoadAccelerator.SceneLoadActive || LoadWindow.PresetBusy);
            if (!person) return;
            long now = DateTime.UtcNow.Ticks;
            if (now < reportAt) return;
            reportAt = now + TimeSpan.TicksPerSecond * 5;
            string assets=AssetAnatomyPass.Stats()+" "+AssetEntryMetadata.Stats()+" searches="+PathCharacterSearch.Accelerated+" bulkTexts="+MorphSnapshotState.BulkTextReads+" directRows="+MorphSnapshotState.DirectRows+" directImports="+MorphSnapshotState.DirectImports+" negativeSkipped="+PackageNegativeLookup.ImpossibleProbesSkipped+" packageTrees="+PackageTreeDisk.Stats()+" varSelection="+VarDirectorySelection.Stats();
            if(assets!=lastAssetCreation){lastAssetCreation=assets;Logger.LogInfo("[asset-creation-io] "+assets);}
            string bundle=CuaPreloadFix.Feature.Stats()+" workers="+VamBundleDiskCache.Backend.WorkerStarts+" receiptHits="+VamBundleDiskCache.Backend.ReceiptHits+" extractedBytes="+VamBundleDiskCache.Backend.ExtractedBytes;
            if(bundle!=lastBundlePreparation){lastBundlePreparation=bundle;Logger.LogInfo("[bundle-preparation] "+bundle+" workerBudgetMiB=256 requestBudget=64");}
            string shared = DelegateGraphLifetime.ShareStats();
            if (shared != lastDelegateShare) { lastDelegateShare = shared; Logger.LogInfo("[delegate-sharing] " + shared); }
            string snapshot = RuntimeControl.Snapshot();
            if (snapshot == lastPerson) return;
            lastPerson = snapshot;
            Logger.LogInfo("[PersonPrepared] " + snapshot);
        }

        private void OnApplicationQuit()
        {
            if (!started) return;
            CriticalPathProbe.Stop();
            CuaPreloadFix.Feature.Stop();
            PackageTreeDisk.ClearBlueprints();
            VarDirectorySelection.Stop();
            CacheSha256.ClearBuffers();
            PackageNegativeLookup.Stop();
            AssetEntryMetadata.Stop();
            AssetAnatomyPass.Stop();
            AnatomyInactiveGuard.Stop();
            MorphSnapshotState.Invalidate();
            BoehmOptimizations.Stop();
            ZipEntryLookup.Clear();
            ClassificationDiskReceipts.Flush();
            ClassificationDiskReceipts.Disable();
            MorphClassificationMemo.Clear();
            LoadReclamation.Stop();
            MaterialTextureIntent.StopAccepting();
            TextureCacheBc7Convert.OnExit();
            VamBundleDiskCache.Backend.Stop();
            CompilerRuntime.Stop();
            if (person) RuntimeControl.CloseEntry();
        }

        private void InstallCriticalPathTiming()
        {
            CriticalPathProbe.Logger = LogCriticalPath;
            CriticalPathProbe.Arm();
            criticalTimingPending = true;
        }

        private void LogCriticalPath(string message) { Logger.LogInfo(message); }
    }
}
