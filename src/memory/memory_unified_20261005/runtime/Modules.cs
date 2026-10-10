using Quest3TriggerUI;

namespace VaM.Memory
{
    public sealed partial class MemoryPlugin
    {
        // Same order as the deployed UI payload; lazy image hooks remain lazy.
        private void InstallModules()
        {
            InstallModule("BodySmootherCompatibility", BodySmootherCompatibility.Install);
            InstallModule("ClothingScriptAssemblyReuse", ClothingScriptAssemblyReuse.Install);
            InstallModule("PackageJsonWeakRetention", PackageJsonWeakRetention.Install);
            InstallModule("MorphTargetStringReuse", MorphTargetStringReuse.Install);
            InstallModule("JointLifetimeRetirement", JointLifetimeRetirement.Install);
            InstallModule("AtomPoolHairRetirement", AtomPoolHairRetirement.Install);
            InstallModule("AssetCallbackRetirement", AssetCallbackRetirement.Install);
            InstallModule("AllocatedObjectRetirement", AllocatedObjectRetirement.Install);
            InstallModule("FailedAssetOperationRetirement", FailedAssetOperationRetirement.Install);
            InstallModule("CancelledBundleDependencyRetirement", CancelledBundleDependencyRetirement.Install);
            InstallModule("FailedDynamicBundleLeaseRetirement", FailedDynamicBundleLeaseRetirement.Install);
            InstallModule("InterruptedDynamicBundleRetirement", InterruptedDynamicBundleRetirement.Install);
            InstallModule("CancelledCuaRequestRetirement", CancelledCuaRequestRetirement.Install);
            InstallModule("SkinBlendTiled", SkinBlendTiled.Install);
            InstallModule("DrawCameraArrayReuse", DrawCameraArrayReuse.Install);
            InstallModule("GraftBoundaryTiles", GraftBoundaryTiles.Install);
            InstallModule("AudioEncodedStream", AudioEncodedStream.Install);
            InstallModule("AudioPcmChunks", AudioPcmChunks.Install);
            InstallModule("LegacyMorphFilterStreaming", LegacyMorphFilterStreaming.Install);
            InstallModule("SceneJsonRuntime", SceneJsonRuntime.Install);
            InstallModule("DependencyErrorPropagation", DependencyErrorPropagation.Install);
            InstallModule("ScenePrefabPresence", ScenePrefabPresence.Install);
            InstallModule("ScenePreloadLease", ScenePreloadLease.Install);
            InstallModule("RefreshDelegateRetirement", RefreshDelegateRetirement.Install);
            InstallModule("CatalogueActionRetention", CatalogueActionRetention.Install);
            InstallModule("PresetDeltaApply", PresetDeltaApply.Install);
            InstallModule("PresetHairRenderBatch", PresetHairRenderBatch.Install);
            InstallModule("PresetSweepGate", PresetSweepGate.Install);
            InstallModule("TextureUploadReuse", TextureUploadReuse.Install);
            InstallModule("TextureInFlight", TextureInFlight.Install);
            InstallModule("TextureCompletionBudget", TextureCompletionBudget.Install);
            if (Config.Bind("Diagnostics", "TextureRequestObservation", true, "Observe superseded clothing/hair requests; does not retire textures.").Value)
                InstallModule("MaterialTextureIntent", MaterialTextureIntent.InstallObserveOnly);
            InstallModule("TextureMetadataReuse", TextureMetadataReuse.Install);
            InstallModule("TextureScratchLifetime", TextureScratchLifetime.Install);
            InstallModule("TextureCacheWriteBudget", TextureCacheWriteBudget.Install);
            InstallModule("TextureCacheBc7Convert", TextureCacheBc7Convert.Install);
            UuaGate.Report();
            InstallModule("UuaTypeCensus", UuaTypeCensus.Install);
            TextureDecodeBudget.ColdEstimate = ColdTextureHeader.Estimate;
        }

        private void TickModules()
        {
            BodySmootherCompatibility.Tick();
            AudioCacheJanitor.Tick();
            WardrobeJanitor.Tick();
            ColdRasterDecode.Install();
            PresetSweepGate.Tick();
            MemoryRetentionReport.Tick();
            MeshOwnerRetentionProbe.Tick();
            LegacyMorphFilterStreaming.Tick();
            AtomPoolHairRetirement.Tick();
            GpuPhysicsRetentionProbe.Tick();
            UuaTypeCensus.Tick();
            SceneOrphanSweep.Tick();
            CatalogueRetirement.Tick();
            TextureCacheBc7Convert.Tick();
            MaterialTextureIntent.Tick();
        }
    }
}
