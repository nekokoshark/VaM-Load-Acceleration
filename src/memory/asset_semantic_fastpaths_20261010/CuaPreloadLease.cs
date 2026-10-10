using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using MeshVR;
using UnityEngine;
namespace CuaPreloadFix
{
 internal static class Feature
 {
  internal static Action<string> Logger;
  private static Harmony harmony;
  private static long deferred;
  private static readonly FieldInfo Paths = typeof(AssetLoader).GetField("pathToAssetBundle",BindingFlags.Instance|BindingFlags.NonPublic);
  private static readonly FieldInfo Counts = typeof(AssetLoader).GetField("assetBundleReferenceCounts",BindingFlags.Instance|BindingFlags.NonPublic);
  private static readonly FieldInfo Singleton = typeof(AssetLoader).GetField("singleton",BindingFlags.Static|BindingFlags.NonPublic);
  internal static void Submit(AssetLoader.AssetBundleFromFileRequest request)
  {
   if(request==null || request.callback!=null)
   {
    AssetLoader.QueueLoadAssetBundleFromFile(request);
    return;
   }
   deferred++;
   if(Logger!=null && (deferred<=4 || deferred%64==0))
    Logger("[cua-preload-lease] consumerDeferred total="+deferred+" speculativeLease=notAcquired actualLoad=native countsNotBytes=True");
  }

  // Scope owns one URL lease per native speculative request, never a prefab or CUA.
  // Owners are weak: pending work must not root LoadCo, its JSON or scene atoms.
  private sealed class Scope
  {
   internal WeakReference Owner, Loader;
   internal bool Closed;
   internal readonly List<Lease> Leases=new List<Lease>();
   internal readonly HashSet<string> Seen=new HashSet<string>(StringComparer.Ordinal);
  }
  private sealed class Lease
  {
   internal Scope Scope;
   internal string Path;
   internal AssetLoader.AssetBundleFromFileRequest Request;
   internal bool Completed, Released;
   internal void Complete(AssetLoader.AssetBundleFromFileRequest request)
   {
    if(Completed)return;
    Completed=true;
    // Called by duplicate waiters, or Tick after the primary worker fully drains.
    request.callback=null; request.assetBundle=null; Request=null;
    completed++;
    if(Scope.Closed)Release();
   }
   internal void Release()
   {
    if(!Completed || Released)return;
    Released=true;
    if(ReferenceEquals(Scope.Loader.Target,Singleton.GetValue(null)))
     AssetLoader.DoneWithAssetBundleFromFile(Path);
    released++;
   }
  }
  private static readonly List<Scope> Scopes=new List<Scope>();
  private static readonly FieldInfo Completion=typeof(AssetLoader).GetField("_completionQueue",BindingFlags.Instance|BindingFlags.NonPublic);
  private static readonly FieldInfo CallbackIndex=typeof(AssetLoader).GetField("_nextCallbackIndex",BindingFlags.Instance|BindingFlags.NonPublic);
  private static long submitted,completed,released,limited;
  private static Func<AssetLoader.AssetBundleFromFileRequest,bool> ReadCompleted;
  private static void BindCompleted()
  {
   var field=typeof(AssetLoader.AssetBundleFromFileRequest).GetField("loadCompleted",BindingFlags.NonPublic|BindingFlags.Public|BindingFlags.Instance);
   if(field==null || field.FieldType!=typeof(bool))throw new MissingFieldException("request completion flag");
   var method=new DynamicMethod("VamSpeculativeCompleted",typeof(bool),new[]{typeof(AssetLoader.AssetBundleFromFileRequest)},typeof(Feature),true);
   var il=method.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,field);il.Emit(OpCodes.Ret);
   ReadCompleted=(Func<AssetLoader.AssetBundleFromFileRequest,bool>)method.CreateDelegate(typeof(Func<AssetLoader.AssetBundleFromFileRequest,bool>));
  }
  private const int MaxSpeculativeRequests=32;
  internal static string Stats()
  {
   int pending=0,held=0;
   foreach(var scope in Scopes)foreach(var lease in scope.Leases)
   {if(!lease.Completed)pending++;else if(!lease.Released)held++;}
   return "submitted="+submitted+" completed="+completed+" released="+released+" limited="+limited+" pending="+pending+" held="+held+" scopes="+Scopes.Count;
  }
  internal static void SubmitScoped(AssetLoader.AssetBundleFromFileRequest request,object owner)
  {
   if(request==null || request.callback!=null){AssetLoader.QueueLoadAssetBundleFromFile(request);return;}
   var loader=Singleton.GetValue(null) as AssetLoader;
   if(ReferenceEquals(loader,null) || owner==null || string.IsNullOrEmpty(request.path)){Submit(request);return;}
   Scope scope=null;
   foreach(var item in Scopes)if(!item.Closed && ReferenceEquals(item.Owner.Target,owner) && ReferenceEquals(item.Loader.Target,loader)){scope=item;break;}
   if(scope==null){scope=new Scope{Owner=new WeakReference(owner),Loader=new WeakReference(loader)};Scopes.Add(scope);}
   if(!scope.Seen.Add(request.path))return;
   int active=0;
   foreach(var item in Scopes)foreach(var entry in item.Leases)if(!entry.Released)active++;
   if(active>=MaxSpeculativeRequests){limited++;return;}
   var lease=new Lease{Scope=scope,Path=request.path,Request=request};
   scope.Leases.Add(lease);
   request.callback=lease.Complete;
   AssetLoader.QueueLoadAssetBundleFromFile(request);
   // Queue appends synchronously. Keep actual consumer callback order untouched:
   // speculative loads never introduce dispatcher head-of-line blocking.
   var queue=(List<AssetLoader.AssetBundleFromFileRequest>)Completion.GetValue(loader);
   int index=queue.Count-1;
   if(index<(int)CallbackIndex.GetValue(loader) || !ReferenceEquals(queue[index],request))
    throw new InvalidOperationException("speculative completion tail changed");
   queue.RemoveAt(index);
   submitted++;
  }
  private static void Close(Scope scope)
  {
   if(scope.Closed)return;
   scope.Closed=true;scope.Owner=null;scope.Seen.Clear();
   foreach(var lease in scope.Leases)lease.Release();
  }
  internal static void EndScope(object owner)
  {
   foreach(var scope in Scopes)if(!scope.Closed && ReferenceEquals(scope.Owner.Target,owner))Close(scope);
   Tick(true);
  }
  internal static void Tick(bool loading)
  {
   for(int i=Scopes.Count-1;i>=0;i--)
   {
    var scope=Scopes[i];
    if(!scope.Closed && (!loading || !scope.Owner.IsAlive || !ReferenceEquals(scope.Loader.Target,Singleton.GetValue(null))))Close(scope);
    for(int j=scope.Leases.Count-1;j>=0;j--)
    {
     var lease=scope.Leases[j];
     if(!lease.Completed && ReadCompleted(lease.Request))lease.Complete(lease.Request);
     if(lease.Released)scope.Leases.RemoveAt(j);
    }
    if(scope.Closed && scope.Leases.Count==0)Scopes.RemoveAt(i);
   }
  }
  internal static void Stop(){Tick(false);}
  private static void AfterMove(object __instance,bool __result){if(!__result)EndScope(__instance);}
  private static Exception MoveFailed(object __instance,Exception __exception){if(__exception!=null)EndScope(__instance);return __exception;}
  private static void AfterDispose(object __instance){EndScope(__instance);}

  // Original counts are keyed by URL; native bundles are shared across URL aliases.
  private static bool BeforeRelease(string path, ref List<string> __state)
  {
   var loader=(AssetLoader)Singleton.GetValue(null);
   if(loader==null)return true;
   var paths=(Dictionary<string,AssetBundle>)Paths.GetValue(loader);
   var counts=(Dictionary<string,int>)Counts.GetValue(loader);
   int own; AssetBundle bundle;
   if(paths==null || counts==null || !counts.TryGetValue(path,out own) || own>1 || !paths.TryGetValue(path,out bundle) || bundle==null)return true;
   foreach(var pair in paths)
   {
    int remaining;
    if(pair.Key!=path && pair.Value==bundle && counts.TryGetValue(pair.Key,out remaining) && remaining>0)
    {
     counts.Remove(path);
     paths.Remove(path);
     return false;
    }
   }
   // Capture aliases while the native object is live; dead Unity objects compare differently.
   __state=new List<string>();
   foreach(var pair in paths)if(pair.Value==bundle)__state.Add(pair.Key);
   return true;
  }
  private static void AfterRelease(string path, List<string> __state)
  {
   if(__state==null)return;
   var loader=(AssetLoader)Singleton.GetValue(null);
   var paths=(Dictionary<string,AssetBundle>)Paths.GetValue(loader);
   var counts=(Dictionary<string,int>)Counts.GetValue(loader);
   if(counts.ContainsKey(path) || paths.ContainsKey(path))return;
   foreach(var alias in __state)if(!counts.ContainsKey(alias))paths.Remove(alias);
  }
  private static MethodInfo Target()
  {
   var type=typeof(SuperController).GetNestedType("<LoadCo>d__1347",BindingFlags.NonPublic);
   return type==null?null:type.GetMethod("MoveNext",BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance);
  }
  private static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions)
  {
   var code=new List<CodeInstruction>(instructions);
   var native=typeof(AssetLoader).GetMethod("QueueLoadAssetBundleFromFile",new[]{typeof(AssetLoader.AssetBundleFromFileRequest)});
   var replacement=typeof(Feature).GetMethod("SubmitScoped",BindingFlags.NonPublic|BindingFlags.Static);
   int count=0;
   for(int index=0;index<code.Count;index++)if(code[index].Calls(native))
   {
    var i=code[index];var owner=new CodeInstruction(OpCodes.Ldarg_0);
    owner.labels.AddRange(i.labels);i.labels.Clear();
    owner.blocks.AddRange(i.blocks);i.blocks.Clear();
    code.Insert(index,owner);index++;
    i.opcode=OpCodes.Call;i.operand=replacement;count++;
   }
   if(count!=1) throw new InvalidOperationException("CUA speculative queue anchors="+count);
   return code;
  }
  internal static int CheckAnchor()
  {
   int count=0; var replacement=typeof(Feature).GetMethod("SubmitScoped",BindingFlags.NonPublic|BindingFlags.Static);
   foreach(var i in Rewrite(PatchProcessor.GetOriginalInstructions(Target(),(ILGenerator)null)))if(i.Calls(replacement))count++;
   return count;
  }
  internal static void Install()
  {
   if(harmony!=null)return;
   CheckAnchor();
   BindCompleted();
   if(Paths==null || Counts==null || Singleton==null || Completion==null || CallbackIndex==null)throw new MissingFieldException("AssetLoader lease fields");
   harmony=new Harmony("vam.cua.speculative.lease.cold.v1");
   try
   {
    harmony.Patch(Target(),transpiler:new HarmonyMethod(typeof(Feature),"Rewrite"),postfix:new HarmonyMethod(typeof(Feature),"AfterMove"),finalizer:new HarmonyMethod(typeof(Feature),"MoveFailed"));
    var dispose=Target().DeclaringType.GetMethod("System.IDisposable.Dispose",BindingFlags.Instance|BindingFlags.NonPublic);
    if(dispose==null)throw new MissingMethodException("LoadCo Dispose");
    harmony.Patch(dispose,postfix:new HarmonyMethod(typeof(Feature),"AfterDispose"));
    harmony.Patch(typeof(AssetLoader).GetMethod("DoneWithAssetBundleFromFile"),prefix:new HarmonyMethod(typeof(Feature),"BeforeRelease"),postfix:new HarmonyMethod(typeof(Feature),"AfterRelease"));
   }
   catch{harmony.UnpatchAll(harmony.Id);harmony=null;throw;}
  }
 }
}
