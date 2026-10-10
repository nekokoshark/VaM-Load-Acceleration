using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using System.Runtime.InteropServices;
using HarmonyLib;
namespace VaM.Memory {
 // Compact views live only inside SyncAnatomy or one synchronous Reset call.
 // Every new Sync call validates all source slots; no cross-frame item roots.
 public static class AssetAnatomyPass {
  public const string Id="vam.memory.asset-anatomy-pass";
  [ThreadStatic]private static Scope current;
  [ThreadStatic]private static Scope batch;
  public sealed class Scope {
   internal Scope Parent;internal DAZCharacterSelector Owner;
   internal DAZClothingItem[] SourceClothes,ActiveClothes;
   internal DAZHairGroup[] SourceHair,ActiveHair;
   internal DAZClothingItem[] ShadowClothes;internal DAZHairGroup[] ShadowHair;
   internal GCHandle PinSourceClothes,PinShadowClothes,PinSourceHair,PinShadowHair;
   internal long ClothesEpoch,HairEpoch;
  }
  [DllImport("msvcrt.dll",CallingConvention=CallingConvention.Cdecl,EntryPoint="memcmp")]
  private static extern int EqualMemory(IntPtr left,IntPtr right,UIntPtr size);
  private static long activeEpoch,checkedEpoch=-1;
  private static bool compatible,installed;
  private static MethodInfo[] fixtureMethods=new MethodInfo[0];
  private static readonly MethodInfo Target=AccessTools.Method(typeof(DAZCharacterSelector),"SyncAnatomy",Type.EmptyTypes);
  private static readonly MethodInfo ClothesGetter=AccessTools.PropertyGetter(typeof(DAZCharacterSelector),"clothingItems");
  private static readonly MethodInfo HairGetter=AccessTools.PropertyGetter(typeof(DAZCharacterSelector),"hairItems");
  private static readonly Func<DAZCharacterSelector,DAZClothingItem[]> OriginalClothes=(Func<DAZCharacterSelector,DAZClothingItem[]>)Delegate.CreateDelegate(typeof(Func<DAZCharacterSelector,DAZClothingItem[]>),ClothesGetter);
  private static readonly Func<DAZCharacterSelector,DAZHairGroup[]> OriginalHair=(Func<DAZCharacterSelector,DAZHairGroup[]>)Delegate.CreateDelegate(typeof(Func<DAZCharacterSelector,DAZHairGroup[]>),HairGetter);
  private static List<MethodBase> sensitive;
  private static readonly MethodInfo ActiveSetter=AccessTools.PropertySetter(typeof(DAZDynamicItem),"active");
  private static readonly Func<DAZDynamicItem,bool> ReadActive=CreateActiveReader();
  private static Func<DAZDynamicItem,bool> CreateActiveReader() {
   var f=AccessTools.Field(typeof(DAZDynamicItem),"_active");
   if(f==null || f.FieldType!=typeof(bool))throw new MissingFieldException("DAZDynamicItem._active");
   var m=new DynamicMethod("ReadItemActive",typeof(bool),new[]{typeof(DAZDynamicItem)},typeof(DAZDynamicItem),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,f);il.Emit(OpCodes.Ret);
   return (Func<DAZDynamicItem,bool>)m.CreateDelegate(typeof(Func<DAZDynamicItem,bool>));
  }
  private static readonly MethodInfo ActiveCapture=AccessTools.Method(typeof(AssetAnatomyPass),"ActiveBefore");
  private static readonly MethodInfo ActiveObserver=AccessTools.Method(typeof(AssetAnatomyPass),"ActiveChanged");
  private static string rejectedMethod;
  public static string Stats(){return "scans="+Scans+" reuses="+Reuses+" rejected="+Rejected+" liveScopes="+LiveScopes+" liveBatches="+LiveBatches+" livePins="+LivePins+" batchReuses="+BatchReuses+" blocked="+(rejectedMethod??"none");}
  public static long Scans,Reuses,Rejected;
  public static int LiveScopes,LiveBatches,LivePins;
  public static long BatchReuses,SlotChecks;
  public static void SetFixtureMethods(MethodInfo[] methods){fixtureMethods=methods??new MethodInfo[0];checkedEpoch=-1;}
  private static bool Allowed(MethodBase method) {
   var info=Harmony.GetPatchInfo(method);if(info==null)return true;
   foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers})foreach(var patch in patches) {
    if(patch.PatchMethod==AccessTools.Method(typeof(AssetAnatomyPass),"Begin") ||
       patch.PatchMethod==AccessTools.Method(typeof(AssetAnatomyPass),"Rewrite") ||
       patch.PatchMethod==AccessTools.Method(typeof(AssetAnatomyPass),"End") ||
       patch.PatchMethod==AccessTools.Method(typeof(AnatomyInactiveGuard),"Rewrite"))continue;
    bool known=false;foreach(var item in fixtureMethods)if(item==patch.PatchMethod){known=true;break;}
    if(!known)return false;
   }
   return true;
  }
  private static bool Compatible() {
   long epoch=MorphSnapshotState.HookEpoch;if(MorphSnapshotState.HooksChanging)return false;
   if(epoch==checkedEpoch)return compatible;
   // SyncAnatomy never calls the active setter. Foreign setter prefixes (e.g.
   // clothing bans) can veto writes, but our postfix still invalidates any view.
   // Require that exact observer to remain installed instead of rejecting all
   // setter hooks. Actual getters and side-effect callbacks remain guarded.
   var info=Harmony.GetPatchInfo(ActiveSetter);bool result=false;
   if(info!=null)foreach(var patch in info.Postfixes)if(patch.PatchMethod==ActiveObserver){result=true;break;}
   bool captured=false;if(info!=null)foreach(var patch in info.Prefixes)if(patch.PatchMethod==ActiveCapture){captured=true;break;}
   result&=captured;
   rejectedMethod=result?null:"active-observer-missing";
   if(result)foreach(var method in sensitive)if(!Allowed(method)){result=false;rejectedMethod=method.DeclaringType.FullName+"."+method.Name;break;}
   if(epoch!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   checkedEpoch=epoch;compatible=result;return result;
  }
  private static void Begin(DAZCharacterSelector __instance,ref Scope __state) {
   if(!Compatible()){Rejected++;return;}
   __state=new Scope {Parent=current,Owner=__instance};current=__state;LiveScopes++;
  }
  private static Exception End(Exception __exception,Scope __state) {
   if(__state!=null) {
    current=__state.Parent;__state.Parent=null;__state.Owner=null;
    __state.SourceClothes=__state.ActiveClothes=null;__state.SourceHair=__state.ActiveHair=null;LiveScopes--;
   }
   return __exception;
  }
  private static void ActiveBefore(DAZDynamicItem __instance,ref bool __state){__state=ReadActive(__instance);}
  private static void ActiveChanged(DAZDynamicItem __instance,bool __state){if(__state!=ReadActive(__instance))Interlocked.Increment(ref activeEpoch);}
  private static void BeginBatch(DAZCharacterSelector __instance,ref Scope __state) {
   if(!Compatible())return;
   __state=new Scope {Parent=batch,Owner=__instance};batch=__state;LiveBatches++;
  }
  private static Exception EndBatch(Exception __exception,Scope __state) {
   if(__state!=null) {
    batch=__state.Parent;__state.Parent=null;__state.Owner=null;
    FreePin(ref __state.PinSourceClothes);FreePin(ref __state.PinShadowClothes);FreePin(ref __state.PinSourceHair);FreePin(ref __state.PinShadowHair);
    __state.SourceClothes=__state.ActiveClothes=__state.ShadowClothes=null;
    __state.SourceHair=__state.ActiveHair=__state.ShadowHair=null;LiveBatches--;
   }
   return __exception;
  }
  private static void FreePin(ref GCHandle pin){if(pin.IsAllocated){pin.Free();Interlocked.Decrement(ref LivePins);}pin=default(GCHandle);}
  private static void PinSlots(Array source,Array shadow,ref GCHandle left,ref GCHandle right) {
   if(left.IsAllocated && ReferenceEquals(left.Target,source) && right.IsAllocated && ReferenceEquals(right.Target,shadow))return;
   FreePin(ref left);FreePin(ref right);
   left=GCHandle.Alloc(source,GCHandleType.Pinned);Interlocked.Increment(ref LivePins);
   right=GCHandle.Alloc(shadow,GCHandleType.Pinned);Interlocked.Increment(ref LivePins);
  }
  private static bool EqualSlots<T>(T[] source,T[] shadow,GCHandle left,GCHandle right) where T:class {
   if(shadow==null || shadow.Length!=source.Length || !left.IsAllocated || !right.IsAllocated)return false;
   SlotChecks+=source.Length;
   // The pinned array data contains native-width object references in this
   // original Mono. Compare EVERY reference bit, not a probabilistic hash.
   return EqualMemory(left.AddrOfPinnedObject(),right.AddrOfPinnedObject(),new UIntPtr(checked((ulong)source.Length*(ulong)IntPtr.Size)))==0;
  }
  private static void SaveClothes(DAZClothingItem[] source,DAZClothingItem[] active,long epoch) {
   var b=batch;if(b==null || current==null || !ReferenceEquals(b.Owner,current.Owner))return;
   b.SourceClothes=source;b.ActiveClothes=active;if(b.ShadowClothes==null || b.ShadowClothes.Length!=source.Length)b.ShadowClothes=(DAZClothingItem[])source.Clone();else Array.Copy(source,b.ShadowClothes,source.Length);
   PinSlots(source,b.ShadowClothes,ref b.PinSourceClothes,ref b.PinShadowClothes);b.ClothesEpoch=epoch;
  }
  private static void SaveHair(DAZHairGroup[] source,DAZHairGroup[] active,long epoch) {
   var b=batch;if(b==null || current==null || !ReferenceEquals(b.Owner,current.Owner))return;
   b.SourceHair=source;b.ActiveHair=active;if(b.ShadowHair==null || b.ShadowHair.Length!=source.Length)b.ShadowHair=(DAZHairGroup[])source.Clone();else Array.Copy(source,b.ShadowHair,source.Length);
   PinSlots(source,b.ShadowHair,ref b.PinSourceHair,ref b.PinShadowHair);b.HairEpoch=epoch;
  }
  private static DAZClothingItem[] Clothes(DAZCharacterSelector owner) {
   var source=OriginalClothes(owner);var scope=current;
   if(scope==null || !ReferenceEquals(scope.Owner,owner) || source==null || source.Length<64 || !Compatible())return source;
   long epoch=Interlocked.Read(ref activeEpoch);
   if(ReferenceEquals(scope.SourceClothes,source) && epoch==scope.ClothesEpoch){Reuses++;return scope.ActiveClothes;}
   var b=batch;
   if(b!=null && ReferenceEquals(b.Owner,owner) && ReferenceEquals(b.SourceClothes,source) && b.ClothesEpoch==epoch && EqualSlots(source,b.ShadowClothes,b.PinSourceClothes,b.PinShadowClothes)) {
    BatchReuses++;scope.SourceClothes=source;scope.ClothesEpoch=epoch;scope.ActiveClothes=b.ActiveClothes;return scope.ActiveClothes;
   }
   var list=new List<DAZClothingItem>();foreach(var item in source)if(!ReferenceEquals(item,null) && item.active)list.Add(item);
   Scans++;scope.SourceClothes=source;scope.ClothesEpoch=epoch;scope.ActiveClothes=list.ToArray();SaveClothes(source,scope.ActiveClothes,epoch);return scope.ActiveClothes;
  }
  private static DAZHairGroup[] Hair(DAZCharacterSelector owner) {
   var source=OriginalHair(owner);var scope=current;
   if(scope==null || !ReferenceEquals(scope.Owner,owner) || source==null || source.Length<64 || !Compatible())return source;
   long epoch=Interlocked.Read(ref activeEpoch);
   if(ReferenceEquals(scope.SourceHair,source) && epoch==scope.HairEpoch){Reuses++;return scope.ActiveHair;}
   var b=batch;
   if(b!=null && ReferenceEquals(b.Owner,owner) && ReferenceEquals(b.SourceHair,source) && b.HairEpoch==epoch && EqualSlots(source,b.ShadowHair,b.PinSourceHair,b.PinShadowHair)) {
    BatchReuses++;scope.SourceHair=source;scope.HairEpoch=epoch;scope.ActiveHair=b.ActiveHair;return scope.ActiveHair;
   }
   var list=new List<DAZHairGroup>();foreach(var item in source)if(!ReferenceEquals(item,null) && item.active)list.Add(item);
   Scans++;scope.SourceHair=source;scope.HairEpoch=epoch;scope.ActiveHair=list.ToArray();SaveHair(source,scope.ActiveHair,epoch);return scope.ActiveHair;
  }
  public static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> input) {
   int clothes=0,hair=0;var code=new List<CodeInstruction>(input);
   foreach(var c in code) {
    if(Equals(c.operand,ClothesGetter)){c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AssetAnatomyPass),"Clothes");clothes++;}
    else if(Equals(c.operand,HairGetter)){c.opcode=OpCodes.Call;c.operand=AccessTools.Method(typeof(AssetAnatomyPass),"Hair");hair++;}
   }
   if(clothes!=4 || hair!=2)throw new InvalidOperationException("SyncAnatomy source getter sites changed");return code;
  }
  private static void ValidatePinnedSlots() {
   var source=new object[]{new object(),new object(),new object()};var shadow=(object[])source.Clone();
   var left=default(GCHandle);var right=default(GCHandle);
   try {
    PinSlots(source,shadow,ref left,ref right);
    if(!EqualSlots(source,shadow,left,right))throw new InvalidOperationException("Pinned reference layout mismatch");
    for(int i=0;i<source.Length;i++) {
     var saved=shadow[i];shadow[i]=new object();
     if(EqualSlots(source,shadow,left,right))throw new InvalidOperationException("Pinned slot change missed");
     shadow[i]=saved;
    }
   }finally{FreePin(ref left);FreePin(ref right);}
  }
  public static void Install() {
   if(installed)return;ValidatePinnedSlots();MorphSnapshotState.ObserveHooks();
   sensitive=new List<MethodBase>{Target,ClothesGetter,HairGetter,AccessTools.PropertyGetter(typeof(DAZDynamicItem),"active"),ActiveObserver};
   // All direct managed/native side-effect entry points are checked, not merely the getters.
   foreach(var instruction in PatchProcessor.GetOriginalInstructions(Target)) {
    var method=instruction.operand as MethodBase;if(method!=null && !sensitive.Contains(method))sensitive.Add(method);
   }
   foreach(string name in new[]{"springDamperMultiplierOn","springDamperMultiplier"}) {
    var setter=AccessTools.PropertySetter(typeof(AdjustJoints),name);
    if(setter!=null && setter.GetMethodBody()!=null)foreach(var instruction in PatchProcessor.GetOriginalInstructions(setter)) {
     var method=instruction.operand as MethodBase;if(method!=null && !sensitive.Contains(method))sensitive.Add(method);
    }
   }
   var harmony=new Harmony(Id);
   try {
    harmony.Patch(AccessTools.PropertySetter(typeof(DAZDynamicItem),"active"),prefix:new HarmonyMethod(typeof(AssetAnatomyPass),"ActiveBefore"){priority=Priority.First},postfix:new HarmonyMethod(typeof(AssetAnatomyPass),"ActiveChanged"));
    harmony.Patch(Target,prefix:new HarmonyMethod(typeof(AssetAnatomyPass),"Begin"),transpiler:new HarmonyMethod(typeof(AssetAnatomyPass),"Rewrite"),finalizer:new HarmonyMethod(typeof(AssetAnatomyPass),"End"));
    foreach(string name in new[]{"ResetClothing","ResetHair"})
     harmony.Patch(AccessTools.Method(typeof(DAZCharacterSelector),name,new[]{typeof(bool)}),prefix:new HarmonyMethod(typeof(AssetAnatomyPass),"BeginBatch"),finalizer:new HarmonyMethod(typeof(AssetAnatomyPass),"EndBatch"));
    installed=true;checkedEpoch=-1;
   }catch(Exception error){harmony.UnpatchAll(Id);throw new InvalidOperationException("asset anatomy install",error);}
  }
  public static void Stop(){if(installed)new Harmony(Id).UnpatchAll(Id);installed=false;current=null;batch=null;}
 }
 public sealed partial class MemoryPlugin {
  private void InstallAssetAnatomyPass() {
   if(Config.Bind("SceneLoading","AssetAnatomyPass",true,"Compact clothing/hair within original SyncAnatomy and synchronous Reset calls; validate every source slot before cross-call reuse, observe actual active changes, preserve callbacks, release on exception; no cross-frame asset roots.").Value)
    InstallModule("AssetAnatomyPass",delegate {AssetAnatomyPass.Install();Logger.LogInfo("[asset-anatomy] perCall=True resetBatch=True slotValidation=full persistentItemRoots=0 sourceGetters=original activeChanges=rechecked");});
  }
 }
}
