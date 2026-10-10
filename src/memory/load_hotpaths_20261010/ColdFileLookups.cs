using System;

namespace VaM.Memory {
    public sealed partial class MemoryPlugin {
        private void InstallColdFileLookups() {
            if(Config.Bind("SceneLoading","ZipEntryLookupIndex",true,"Index original invariant-cultural ZIP entry names with weak archive identity and bounded metadata.").Value)
                InstallModule("ZipEntryLookup",delegate {
                    ZipEntryLookup.Log=message=>Logger.LogInfo(message);
                    ZipEntryLookup.Install();
                });
            if(Config.Bind("SceneLoading","MorphClassificationReceipts",true,"Reuse audited fixed-threshold file classification across equal target regions; no morph or file payload cache.").Value)
                InstallModule("MorphClassificationReceipts",delegate {
                    MorphClassificationMemo.Log=message=>Logger.LogInfo(message);
                    MorphClassificationMemo.Install();
                    ClassificationDiskReceipts.Log=message=>Logger.LogInfo(message);
                    if(Config.Bind("SceneLoading","MorphClassificationDiskReceipts",true,"Persist bounded scalar classification receipts across sessions; no morph payload cache.").Value) {
                        int megabytes=Config.Bind("SceneLoading","ClassificationDiskBudgetMiB",512,new BepInEx.Configuration.ConfigDescription("Classification receipt disk budget, 16 to 16384 MiB; memory budget stays independent.",new BepInEx.Configuration.AcceptableValueRange<int>(16,16384))).Value;
                        int files=Config.Bind("SceneLoading","ClassificationDiskTableLimit",8192,new BepInEx.Configuration.ConfigDescription("Classification receipt disk table limit, 128 to 65536; only inactive validated receipts may be retired.",new BepInEx.Configuration.AcceptableValueRange<int>(128,65536))).Value;
                        ClassificationDiskReceipts.SetDiskLimits(megabytes,files);
                        ClassificationDiskReceipts.DisableDiskPruning();
                        ClassificationDiskReceipts.Configure(MemoryCachePaths.DirectoryFor("ClassificationReceipts"));
                    }
                });
            Logger.LogInfo("[cold-load-files] zipNameIndex=weak classification=scalar sceneNameChecks=0 newDlls=0");
        }
    }
}
