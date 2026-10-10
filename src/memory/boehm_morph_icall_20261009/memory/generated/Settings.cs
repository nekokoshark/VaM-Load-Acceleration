using BepInEx.Configuration;
using Quest3TriggerUI;
namespace VaM.Memory { public sealed partial class MemoryPlugin { private void BindModules() {
AudioCacheJanitor.Enabled = Bind(
                "Audio", "CacheEviction", true,
                "Evict audio clips that no AudioSource has referenced for CacheGraceSeconds — VaM caches every decoded clip forever in URLAudioClipManager/EmbeddedAudioClipManager (~2GB observed). Evicted URL clips re-decode lazily if needed again.");
AudioCacheJanitor.GraceSeconds = Bind(
                "Audio", "CacheGraceSeconds", 180f,
                "Seconds a clip may stay unreferenced before the audio cache janitor evicts it.");
AudioCacheJanitor.IncludeEmbedded = Bind(
                "Audio", "CacheEvictEmbedded", false,
                "Also evict EmbeddedAudioClipManager clips (bigger pool; embedded clip data may not be re-loadable until VaM restarts — off by default).");
AudioCacheJanitor.EvictNow = Bind(
                "Diagnostics", "AudioCacheEvictNow", false,
                "One-shot: evict all currently-unreferenced audio clips immediately (ignores grace window), then resets to false.");
PresetCleanupCoalescer.Enabled = Bind(
                "TextureLoading", "CoalesceCoveredCleanup", true,
                "Reuse a recent full sweep for supplemental cleanup instead of paying a second full-heap mark.");
PresetCleanupCoalescer.ReuseSeconds = Bind(
                "TextureLoading", "CoalesceReuseSeconds", 80f,
                "Max age of a previous full sweep that may still cover the janitor's supplemental cleanup.");
PresetCleanupCoalescer.DeferMax = Bind(
                "TextureLoading", "CoalesceDeferMax", 4,
                "Max release events a covered sweep may defer before the janitor runs a backstop cleanup.");
PresetInstanceReuse.Enabled = Bind(
                "TextureLoading", "ReusePresetInstances", true,
                "Temporarily retain ready same-base clothing/hair instances across native preset reset; restore all parameters normally.");
PresetDeltaApply.Enabled = Bind("PresetLoading", "DeltaActiveItems", true,
                "Keep matching live same-base clothing/hair active during preset reset; restore all target parameters and reset physics normally.");
PresetHairRenderBatch.Enabled = Bind("PresetLoading", "BatchHairRenderUpdates", true,
                "Combine repeated hair render-particle updates within parameter restore; preserves density, physics and final parameter values.");
PresetSweepGate.Enabled = Bind("PresetLoading", "SkipUnchangedCharacterSweep", true,
                "Skip verified unchanged appearance/clothing UUA after a completed sweep; preserve actual release debt, pressure, manual cleanup and 120s request-time bound.");
PresetSweepGate.SkipUnchangedGC = Bind("PresetLoading", "SkipUnchangedPresetGC", true,
                "Skip verified unchanged preset GC at low growth; settle pending native preset GC after async loading (30s cap), or immediately at 75% RAM pressure. Keep 256MiB/120s GC limits and manual cleanup.");
PresetSweepGate.DeferSweep = Bind("PresetLoading", "DeferPresetSweep", true,
                "A sweep requested from inside a preset restore is submitted at the next quiet moment instead of mid-load (measured 8.68s wall / 6.3s freeze inside a 10.2s no-change reload). Same global mark, same debt, fewer waiting seconds; off restores the old inline submit.");
PresetSweepGate.GcYieldGate = Bind("PresetLoading", "DeferLowYieldPresetGC", true,
                "Also defer the post-swap GC when the previous one barely paid for itself (yield below max(64MiB, 5% of the pre-collect heap)). The tail-settled branch was the one path that collected without rechecking growth, and it fires 0.25s after a swap, i.e. once the character is already on screen: 20 measured swaps each cost 3.6-3.9s for 31MiB..1.5GiB. While deferred the request stays pending for the existing 30s bound, the 75% pressure line and the 1.5GB/60s idle watchdog, so retained garbage is capped at one 30s window (~0.2GB at the documented 0.28-0.34GB/min idle churn). Set false to collect at every settled tail again.");
PresetSweepGate.SwapGcToIdle = Bind("PresetLoading", "HandSwapGCToIdle", true,
                "Do not run the post-sweep preset GC inside the click window. Over 20 consecutive swaps each inline collect cost 3.9-4.1s and froze 4.8-6.9s of frame time to free 0.4-0.9GiB, while the 1.5GiB/60s idle watchdog returns 2.2-3.8GiB for the same 4s. With headroom the request is dropped and the watchdog owns the collection; memory pressure still collects inline and the 30s/120s bounds are unchanged. Set false to collect inline again.");
PresetSweepGate.IdleGcAboveLine = Bind("PresetLoading", "IdleGcWhenAboveLoadLine", true,
                "Above the 80% load line raise the idle watchdog's growth band to 3GiB instead of refusing the collect. Measured here: the session's steady state is load=82-88% with the external trimmer off, the 1.5GiB gate fired every cycle, and the load line refused every attempt until the heap sawtoothed 19.74 -> 26.6GB of 28.90GB and one 4.4-4.7s mark landed wherever Boehm picked it. The physical floor (2GB available RAM, 8GB page file) and the 60s minimum interval still apply. Set false to keep the 80% line absolute.");
PresetSweepGate.IdleGcOnExhaust = Bind("PresetLoading", "IdleGcOnHeapExhaust", true,
                "Also let the idle watchdog collect while RAM load sits above its 80% line, once the managed heap itself is nearly out of room (free below 8% of capacity, floor 256MiB). Measured twice in one idle session: the watchdog logged 'idle GC blocked ... load=84%' for the whole climb, monoFree fell to 0.24-0.28GB of 26.17GB, and Boehm then collected on its own from inside a UI frame at 4.2-4.5s of freeze. Same mark either way, cheaper frame. The hardware floor (2GB available RAM, 8GB page file) still applies. Set false to keep the load line absolute.");
ColdTextureBufferLayout.Enabled = Bind("TextureLoading", "CompactColdDecodeBuffers", true,
                "Use exact upload-sized cold buffers and convert bump maps in place after a local Unity byte-layout selftest; no new resident pool or cache format change.");
BumpNormalRowConverter.Enabled = Bind(
                "TextureLoading", "BumpNormalThreeRows", true,
                "Use three scratch rows for native-equivalent bump-to-normal conversion; preserve output format and resolution.");
TextureUploadReuse.Enabled = Bind("TextureLoading", "ReuseBeforeUpload", true,
                "Reuse a compatible completed native texture before duplicate upload; preserve callbacks and force reload.");
TextureInFlight.Enabled = Bind("TextureLoading", "ShareInFlightDecode", true,
                "Share identical in-flight worker decode results; native callbacks and byte reservations remain independent.");
TextureCompletionBudget.Enabled = Bind("TextureLoading", "BudgetCompletionQueue", true,
                "Keep native four completions; drain up to 16 cheap items within 2ms/32MiB soft budgets.");
TextureMetadataReuse.Enabled = Bind("TextureLoading", "ReuseRequestMetadata", true,
                "Reuse admission metadata in the same request after cache file stamp checks.");
TextureScratchLifetime.Enabled = Bind("TextureLoading", "ReleaseDecodeScratchEarly", true,
                "Release GDI source after drawing and destination after pixel copy; unchanged image processing.");
TextureCacheWriteBudget.Enabled = Bind("TextureLoading", "AccountCacheWrites", true,
                "Charge asynchronous cache-write arrays to the existing texture admission budget until completion.");
TextureCacheBc7Convert.Enabled = Bind("TextureLoading", "ConvertCacheToBc7", true,
                "Re-encode an uncompressed RGBA32/RGB24 image cache as BC7 in the background right after the game read it, so the next load reads a quarter of the bytes. Only runs while that texture is still cached and no scene is loading; the pair is swapped in one rename and journalled so an interrupted swap is repaired at the next start.");
TextureCacheBc7Convert.ToolPath = Bind("TextureLoading", "Bc7ToolPath", "",
                "Optional path to texconv.exe (DirectXTex). Empty tries the plugin folder, then XnView MP. Without a tool the automatic conversion stays off.");
TextureCacheBc7Convert.DiagnoseLayout = Bind("TextureLoading", "DiagnoseBc7CacheLayout", true,
                "One-off startup probe that asks Unity for the storage size of BC7 chains on non multiple-of-four dimensions and logs it. Set false once that layout question is settled.");
TextureCacheBc7Convert.IdleSeconds = Bind("TextureLoading", "Bc7IdleSeconds", 180,
                "Seconds without any input before a background re-encode may start when Bc7Immediate is off. 0 disables the in-session path so conversion only happens after the game exits.");
TextureCacheBc7Convert.Immediate = Bind("TextureLoading", "Bc7Immediate", true,
                "Re-encode each cache entry as soon as its load window closes instead of waiting for Bc7IdleSeconds without input. Encoding stays on the background worker and the swap still needs the window clear, so a first load never waits; immediate mode also swaps while the texture is still resident, which is when the swap can actually land.");
TextureCacheBc7Convert.ConvertOnExit = Bind("TextureLoading", "Bc7ConvertOnExit", true,
                "Hand the entries collected this session to a hidden helper that converts them one at a time after the game has exited.");
TextureDecodeBudget.Enabled = Bind(
                "TextureLoading", "DecodeBudgetEnabled", true,
                "Bound estimated pending/decoding/upload-wait texture bytes; preserves image quality.");
TextureDecodeBudget.BudgetMiB = Bind(
                "TextureLoading", "DecodeBudgetMiB", 2048,
                "Estimated in-flight budget (256..8192 MiB), reduced under physical RAM or system commit pressure. One oversize image may proceed.");
TextureDecodeBudget.EarlyDiscard = Bind(
                "TextureLoading", "DiscardStaleBeforeDecode", true,
                "Skip decoding only when all known native receivers are proven obsolete; retain native completion.");
WardrobeJanitor.Enabled = Bind(
                "Wardrobe", "AutoUnload", true,
                "Destroyed-removed clothing/hair reclamation: VaM keeps every swapped-out item's GameObject+textures until you press optimize memory. This unloads items that have been inactive for WardrobeIdleSeconds, a few per frame — reclaims their RAM+VRAM without the big optimize stall.");
WardrobeJanitor.IdleSeconds = Bind(
                "Wardrobe", "IdleSeconds", 60f,
                "Seconds a removed clothing/hair item may sit inactive before it is destroyed. Longer = safer when cycling the same outfit set (re-wearing a destroyed item reloads it from scratch).");
WardrobeJanitor.MaxPerFrame = Bind(
                "Wardrobe", "MaxPerFrame", 2,
                "Max inactive wardrobe items destroyed per frame — spreads the Destroy cost so reclamation is near-invisible.");
WardrobeJanitor.PurgeOnLoad = Bind(
                "Wardrobe", "PurgeOnLoad", true,
                "After successful appearance/clothing/hair restore, wait for native image/character load tail before reclaiming inactive instances. Full appearance only also purges morph scratch; independent of AutoUnload.");
WardrobeJanitor.PurgeDelaySeconds = Bind(
                "Wardrobe", "PurgeDelaySeconds", 5f,
                "Seconds after a full person preset load before the inactive-item purge runs — lets the async texture/morph tail settle first.");
TextureOrphanSweeper.Enabled = Bind(
                "Wardrobe", "TexOrphanSweep", true,
                "Release unused textureCache ownership only for observed native character/material callback results. Unknown/UI/preload consumers are retained; no direct texture destruction.");
TextureOrphanSweeper.AgeSeconds = Bind(
                "Wardrobe", "TexOrphanSeconds", 120f,
                "Seconds a cache entry may sit without any use count before its cache ownership is released — only observed native callbacks qualify; UI, preloads and unknown consumers are excluded.");
TextureOrphanSweeper.UnusedBudgetMiB = Bind(
                "Wardrobe", "UnusedNativeTextureBudgetMiB", 512,
                "Soft budget for observed native unused textures only; live/shared/UI/preload/unknown consumers retained; 15s grace and sweep batch apply.");
TextureOrphanSweeper.MaxPerSweep = Bind(
                "Wardrobe", "TexOrphanMaxPerSweep", 16,
                "Max orphan cache entries released per 10s sweep — spreads the work.");
SceneOrphanSweep.SliceMs = Bind(
                "Wardrobe", "OrphanSweepSliceMs", 3f,
                "Per-frame CPU budget (ms) for the post-scene-load orphan sweep. It used to run every leg inside one frame (measured 10.8s freeze); the same work is now spread across frames under this budget. Range 0.5 (lightest, slowest) to 16 (heaviest, fastest).");
SceneOrphanSweep.SweepObjects = Bind(
                "Wardrobe", "OrphanSweepObjects", false,
                "Include the GameObject census leg of the post-scene-load orphan sweep (groups inactive (Clone) trees, feeds the clone-kill pass). Measured 16.1s of a 17.7s sweep with zero kills, so it is off unless a clone leak is suspected.");
SceneOrphanSweep.Census = Bind(
                "Wardrobe", "OrphanSweepCensus", false,
                "Include the observation-only component census leg of the post-scene-load orphan sweep (stranded-component count plus a per-MonoBehaviour-type histogram). Nothing but the log line reads it, and it measured 6147ms of slice time in the last sweep, so it is off unless the census is what is being investigated.");
SceneOrphanSweep.MeasureGC = Bind(
                "Wardrobe", "OrphanSweepMeasureGC", false,
                "The sweep additionally forces a full GC just to log liveMiB vs retained heap. Off: the same numbers come from the scheduled PresetSweepGate GC without a multi-second collect inside the sweep.");
DecodedBufferPool.Enabled = Bind(
                "Wardrobe", "DecodedBufferPool", true,
                "Pool QueuedImage.raw decode buffers by exact size — reuses the arrays instead of allocating ~1.5GB of throwaway managed bytes per person preset load.");
DecodedBufferPool.BudgetMiB = Bind(
                "Wardrobe", "DecodedBufferPoolBudgetMiB", 1536,
                "Configured ceiling for idle decode buffers; effective maximum is 64MiB, individual arrays up to 16MiB, idle expiry 30s. Larger or untracked arrays are not retained.");
TextureCacheByteReuse.Enabled = Bind(
                "TextureLoading", "ReuseCachedTextureBytes", true,
                "Read a completed .vamcache through the exact-length decode pool instead of a fresh allocation. A preset switch served entirely from the texture cache still allocated 0.4-1.4GB of throwaway managed bytes per cycle (texture-budget managedDecodeMiB). Any deviation from the native read falls back to the native read.");
TextureCacheByteReuse.MinBytesKB = Bind(
                "TextureLoading", "ReuseCachedTextureBytesMinKB", 256,
                "Smallest cached texture (KB) worth pooling; thumbnails, meta files and other small reads stay on the native path.");
NativeCacheBuffer.Enabled = Bind(
                "TextureLoading", "NativeCacheReadBuffers", true,
                "Stage a completed .vamcache read in a native block (VirtualAlloc) and upload it with Texture2D.LoadRawTextureData(IntPtr,int); large blocks are released as soon as the upload consumed them; small blocks use a bounded 10s reuse window. These pages do not join Boehm's free lists. Small files, oversize files, budget and every read failure fall back to the pooled managed read, so the request always uploads the same bytes.");
NativeCacheBuffer.BudgetMiB = Bind(
                "TextureLoading", "NativeCacheReadBudgetMiB", 1024,
                "Total rounded native capacity, including filling, uploading and idle blocks. At most 16 reservations; budget refusal keeps the managed compatibility path.");
NativeCacheBuffer.IdleMiB = Bind(
                "TextureLoading", "NativeCacheReadReuseMiB", 192,
                "Configured ceiling for committed native reuse; effective maximum is 64MiB, individual blocks up to 16MiB, idle expiry 10s. Larger blocks are VirtualFree-d immediately.");
NativeCacheBuffer.MaxFileMiB = Bind(
                "TextureLoading", "NativeCacheReadMaxFileMiB", 512,
                "Largest single cache file staged natively; anything bigger keeps the pooled managed read.");
WardrobeJanitor.PurgeMorphDeltas = Bind(
                "Wardrobe", "PurgeMorphDeltas", true,
                "The post-load purge also calls UnloadRuntimeMorphDeltas (the same call VaM's optimize-memory makes) to drop the previous preset's runtime morph deltas.");
InstanceAssetLedger.Enabled = Bind(
                "Wardrobe", "InstanceAssetLedger", true,
                "Record the materials/meshes a clothing or hair instance referenced before the janitor destroys it, so the objects it leaves behind can be released without a full-heap UUA mark. Registration only reads the shared* accessors; it never instantiates and never destroys.");
InstanceAssetLedger.DestroyMaterials = Bind(
                "Wardrobe", "InstanceAssetDestroyMaterials", false,
                "Release the ledger's material candidates that no live renderer, graphic or skinned mesh references. Only Unity-named \"(Clone)\" runtime instances qualify; package assets are counted, never touched. Off by default: read the asset-ledger log line for one swap first.");
InstanceAssetLedger.DestroyMeshes = Bind(
                "Wardrobe", "InstanceAssetDestroyMeshes", false,
                "Release unreferenced meshes from the ledger as well. Off by default and not recommended until the log shows what the mesh candidates are: Destroy() on a mesh that came from a package is how a character loses hair or turns pink.");
InstanceAssetLedger.SettleSeconds = Bind(
                "Wardrobe", "InstanceAssetSettleSeconds", 2f,
                "Seconds between an instance unload and the liveness census for its objects, so the native teardown and image tail have finished first.");
GraftBoundaryTiles.Enabled = Bind("Performance", "GraftBoundaryTiles", true,
                "Tile Boundary graft weights without changing scalar accumulation order or physical simulation.");
ColdRasterDecode.Enabled = Bind("TextureLoading", "NativeColdDecodeBuffers", true,
                "Decode temporary cold-image pixels directly into the existing native budget; preserve pixel layout, shared requests and original texture completion. Large native raster buffers release after their last uploader.");
UuaTypeCensus.Enabled = Bind("Diagnostics", "UuaTypeCensus", true,
                "Measure what each Resources.UnloadUnusedAssets sweep actually reclaims, by class: one census of Material/Mesh/Texture2D/RenderTexture counts and estimated bytes plus the Unity allocator, graphics-driver and mono totals immediately before the sweep, one right after its AsyncOperation reports done, then the delta. UUA's price is the full-heap mark (8-21s here, unloaded=0..5), so this is the number that decides whether the swap path can stop calling it. Measurement only, but the two censuses cost about 0.3-0.5s per sweep.");
UuaTypeCensus.Heavy = Bind("Diagnostics", "UuaTypeCensusHeavy", false,
                "Also enumerate GameObject and Component in the census. SceneOrphanSweep measured those legs at 4.7s/16.1s including per-object analysis; a bare count may be far cheaper, and every [uua-census] line reports censusMs so the real cost shows up. Off by default because it lands on the sweep path.");
UuaGate.Enabled = Bind("Diagnostics", "UuaGate", true,
                "Demote the native sweep the preset path submits after a swap settles. Measured over four real swaps (character x2, appearance, clothing): 8.57-9.12s of main-thread stall for 0-16 MiB of CPU allocation, 0 MiB of VRAM, 3-6 materials, 0-3 textures totalling <=1 MiB and no render targets, while the managed GC in the same swap frees 2.5-3.0GB in ~3.7s. Only a sweep whose caller chain names PresetSweepGate.SubmitSweep is skipped, and only after a real sweep has run once in this session (its completed operation is what gets handed back); standby, scene load, janitor cleanup and unrecognised callers keep their sweep. On by default (the enabled path): this changes behaviour on the swap path; set false to hand every sweep back to the engine.");
UuaGate.BackstopSeconds = Bind("Diagnostics", "UuaGateBackstopSeconds", 180,
                "Hard bound on how long the preset path may keep handing back a completed operation instead of running a real sweep. A swap-only session must not drift forever, so once this many seconds have passed since the last real sweep the next demotable call really sweeps (8-21s). Set 0 to keep the 180s default.");
UuaGate.MaxSkips = Bind("Diagnostics", "UuaGateMaxSkips", 8,
                "Hard bound on how many demotable sweeps may be handed a completed operation in a row. Reaching either this or UuaGateBackstopSeconds lets the next one run for real. Set 0 to keep the 8 default.");
} } }
