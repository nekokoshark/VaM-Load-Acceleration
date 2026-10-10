using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using DynamicCSharp;
using HarmonyLib;
using MVR.FileManagement;
using Quest3TriggerUI;

namespace VaM.Memory {
    // Share only scalar classification receipts across equal target regions.
    // No morph, listener, bank, byte payload, entry, package or stream is a strong cache root.
    public static class MorphClassificationMemo {
        public const string Id="vam.memory.morph-classification-memo";
        private static readonly object Gate=new object();
        private static readonly WeakObservationIndex<Receipt> Files=new WeakObservationIndex<Receipt>();
        private static readonly WeakObservationIndex<Region> Sets=new WeakObservationIndex<Region>();
        private static readonly List<Region> Regions=new List<Region>();
        private static readonly Dictionary<MethodBase,Schema> Schemas=new Dictionary<MethodBase,Schema>();
        private static readonly HashSet<Assembly> Seen=new HashSet<Assembly>();
        private static long epoch,results,regionVertices;
        public static long Hits,Misses;
        [ThreadStatic] private static Scope active;
        public static Action<string> Log;
        internal sealed class Schema { internal FieldInfo Set,FileCount,VertexCount; internal string Hash; }
        private sealed class Region { internal int Id; internal HashSet<int> Vertices; internal string DiskKey; }
        internal sealed class Receipt {
            internal long Size,Time,PackageSize,PackageTime;
            internal WeakReference Package;
            internal string InternalPath;
            internal Dictionary<int,Result> Results;
        }
        internal struct Result { internal bool Value; internal long Vertices; }
        public sealed class Scope {
            internal Scope Parent;
            internal object Listener;
            internal VarFileEntry Entry;
            internal byte[] Bytes;
            internal string Path;
            internal int Key;
            internal long Epoch,BeforeVertices;
            internal Receipt Stamp;
            internal Schema Schema;
            internal ClassificationSourceStamp DiskStamp;
            internal string DiskRegion;
            internal MethodBase Method;
            internal HashSet<int> RegionVertices;
        }
        public sealed class Phase {
            internal long Start,Hits,Misses,Exact,Cultural,DiskHits,DiskRejected,Checks;
        }
        private static void PhaseBegin(ref Phase __state) {
            __state=new Phase {Start=Stopwatch.GetTimestamp(),Hits=Hits,Misses=Misses,
                Exact=ZipEntryLookup.ExactHits,Cultural=ZipEntryLookup.CulturalHits,
                DiskHits=ClassificationDiskReceipts.Hits,DiskRejected=ClassificationDiskReceipts.Rejected,
                Checks=ClassificationDiskReceipts.CompatibilityChecks};
        }
        private static Exception PhaseEnd(Exception __exception,MethodBase __originalMethod,Phase __state) {
            if(__state==null)return __exception;
            double ms=(Stopwatch.GetTimestamp()-__state.Start)*1000.0/Stopwatch.Frequency;
            if(Log!=null && ms>=100)Log("[morph-classification-phase] caller="+__originalMethod.DeclaringType.FullName+"."+__originalMethod.Name+
                " inclusiveMs="+ms.ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+
                " memoHits="+(Hits-__state.Hits)+" misses="+(Misses-__state.Misses)+
                " zipExact="+(ZipEntryLookup.ExactHits-__state.Exact)+" zipCultural="+(ZipEntryLookup.CulturalHits-__state.Cultural)+
                " diskHits="+(ClassificationDiskReceipts.Hits-__state.DiskHits)+" diskRejected="+(ClassificationDiskReceipts.Rejected-__state.DiskRejected)+
                " compatibilityChecks="+(ClassificationDiskReceipts.CompatibilityChecks-__state.Checks)+
                " exception="+(__exception==null?"none":__exception.GetType().Name));
            return __exception;
        }
        public static void Clear() { lock(Gate) {epoch++;Files.Clear();Sets.Clear();Regions.Clear();results=regionVertices=0;} }
        public static void Install() {
            var harmony=new Harmony(Id);
            harmony.Patch(AccessTools.Method(typeof(MVR.FileManagementSecure.FileManagerSecure),"ReadAllBytes"),
                postfix:new HarmonyMethod(typeof(MorphClassificationMemo),"Read"));
            foreach(string name in new[]{"Refresh","RegisterPackage","UnregisterPackage"})
                harmony.Patch(AccessTools.Method(typeof(FileManager),name),prefix:new HarmonyMethod(typeof(MorphClassificationMemo),"Clear"));
            foreach(string name in new[]{"ReadEntries","DisposeInternal","BeginUpdate"})
                foreach(MethodInfo method in typeof(ICSharpCode.SharpZipLib.Zip.ZipFile).GetMethods(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic))
                    if(method.Name==name)harmony.Patch(method,prefix:new HarmonyMethod(typeof(MorphClassificationMemo),"Clear"));
            ConstructorInfo ctor=typeof(ScriptAssembly).GetConstructor(BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic,
                null,new[]{typeof(ScriptDomain),typeof(Assembly)},null);
            if(ctor==null)throw new MissingMethodException("ScriptAssembly wrapper");
            harmony.Patch(ctor,postfix:new HarmonyMethod(typeof(MorphClassificationMemo),"Wrapped"));
            foreach(Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())Inspect(assembly);
        }
        private static void Wrapped(ScriptAssembly __instance) {Inspect(__instance.RawAssembly);}
        public static int Inspect(Assembly assembly) {
            lock(Gate) {
                if(assembly==null || assembly is System.Reflection.Emit.AssemblyBuilder || Seen.Contains(assembly))return 0;
                Type[] types;
                try{types=assembly.GetTypes();}catch(ReflectionTypeLoadException){return 0;}
                int count=0;
                foreach(Type type in types) foreach(string name in new[]{"ClassifyMorphFile","IncludeMorphFile"}) {
                    MethodInfo method=type.GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.DeclaredOnly);
                    if(method==null || method.GetParameters().Length!=1 || method.GetParameters()[0].ParameterType!=typeof(string))continue;
                    string hash=MorphFilterMethodAudit.Fingerprint(method);
                    // These two full original method fingerprints use fixed threshold 0.005f.
                    // Distribution-sum versions and unfamiliar algorithms keep their original route.
                    if(hash!="DDBE2F4AC08700BB4092AEDBC0607355B7B9BEF65CB6E876A6D98C942655E2C5" &&
                        hash!="71EFFE184BDFD5CFFDE3FC4041EBD189DED35E1243D5C9815098EF540FA6E79C")continue;
                    var schema=new Schema {Hash=hash,Set=AccessTools.Field(type,"_vertexIndices"),
                        FileCount=AccessTools.Field(type,"_classificationFiles"),VertexCount=AccessTools.Field(type,"_classificationVertices")};
                    if(schema.Set==null || schema.Set.FieldType!=typeof(HashSet<int>) || schema.FileCount==null ||
                        schema.FileCount.FieldType!=typeof(long) || schema.VertexCount==null || schema.VertexCount.FieldType!=typeof(long))continue;
                    Schemas[method]=schema;
                    new Harmony(Id).Patch(method,prefix:new HarmonyMethod(typeof(MorphClassificationMemo),"Before"),
                        finalizer:new HarmonyMethod(typeof(MorphClassificationMemo),"After"));
                    var phase=type.GetMethod("ProcessMorphs",BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.DeclaredOnly,
                        null,new[]{typeof(DAZMorphBank)},null);
                    if(phase!=null && phase.ReturnType==typeof(void))
                        new Harmony(Id).Patch(phase,prefix:new HarmonyMethod(typeof(MorphClassificationMemo),"PhaseBegin"),
                            finalizer:new HarmonyMethod(typeof(MorphClassificationMemo),"PhaseEnd"));
                    count++;
                    if(Log!=null)Log("[morph-classification-memo] patched="+type.FullName+"."+name+" receipts=scalar payloadCache=none");
                }
                Seen.Add(assembly);return count;
            }
        }
        private static Region RegionFor(HashSet<int> set) {
            if(Sets.Count>=128)Sets.Clear();
            Region region;
            if(Sets.TryGetValue(set,out region) && region!=null && region.Vertices.SetEquals(set))return region;
            foreach(Region existing in Regions)if(existing.Vertices.SetEquals(set)){Sets[set]=existing;return existing;}
            if(Regions.Count>=64 || Sets.Count>=128 || regionVertices+set.Count>65536)Clear();
            region=new Region{Id=Regions.Count,Vertices=new HashSet<int>(set)};
            Regions.Add(region);Sets[set]=region;regionVertices+=set.Count;return region;
        }
        private static Receipt Stamp(VarFileEntry entry) {
            if(entry==null || entry.Simulated || entry.Package==null || entry.Package.forceRefresh)return null;
            return new Receipt{Size=entry.Size,Time=entry.LastWriteTime.Ticks,
                PackageSize=entry.Package.Size,PackageTime=entry.Package.LastWriteTime.Ticks,
                Package=new WeakReference(entry.Package),InternalPath=entry.InternalSlashPath};
        }
        private static bool Equal(Receipt a,Receipt b) {
            return a!=null && b!=null && a.Size==b.Size && a.Time==b.Time && a.PackageSize==b.PackageSize && a.PackageTime==b.PackageTime &&
                ReferenceEquals(a.Package.Target,b.Package.Target) && a.InternalPath==b.InternalPath;
        }
        private static bool Before(object __instance,MethodBase __originalMethod,string __0,ref bool __result,ref Scope __state) {
            Schema schema;
            lock(Gate)if(!Schemas.TryGetValue(__originalMethod,out schema))return true;
            var set=schema.Set.GetValue(__instance) as HashSet<int>;
            if(set==null || set.GetType()!=typeof(HashSet<int>) || set.Count>8192 ||
                !ReferenceEquals(set.Comparer,EqualityComparer<int>.Default))return true;
            var entry=FileManager.GetFileEntry(__0,true) as VarFileEntry;
            Receipt stamp=Stamp(entry);
            if(stamp==null)return true;
            lock(Gate) {
                Region region=RegionFor(set);
                int key=region.Id;
                Receipt receipt;Result result;
                if(Files.TryGetValue(entry,out receipt) && Equal(receipt,stamp) && receipt.Results.TryGetValue(key,out result)) {
                    schema.FileCount.SetValue(__instance,(long)schema.FileCount.GetValue(__instance)+1);
                    schema.VertexCount.SetValue(__instance,(long)schema.VertexCount.GetValue(__instance)+result.Vertices);
                    __result=result.Value;Hits++;
                    if(Log!=null && (Hits==1 || Hits%4096==0))Log("[morph-classification-memo] hits="+Hits+" misses="+Misses+" fileRows="+Files.Count+" resultRows="+results+" payloadCache=none");
                    return false;
                }
                if(region.DiskKey==null)region.DiskKey=ClassificationSourceStamp.RegionKey(region.Vertices);
                ClassificationSourceStamp diskStamp;
                if(ClassificationDiskReceipts.TryGet(entry,schema.Hash,region.DiskKey,__originalMethod,out result,out diskStamp)) {
                    schema.FileCount.SetValue(__instance,(long)schema.FileCount.GetValue(__instance)+1);
                    schema.VertexCount.SetValue(__instance,(long)schema.VertexCount.GetValue(__instance)+result.Vertices);
                    if(Files.Count<24576 && results<49152) {
                        if(!Files.TryGetValue(entry,out receipt) || !Equal(receipt,stamp))receipt=stamp;
                        if(receipt.Results==null)receipt.Results=new Dictionary<int,Result>();
                        if(!receipt.Results.ContainsKey(key))results++;
                        receipt.Results[key]=result;Files[entry]=receipt;
                    }
                    __result=result.Value;Hits++;return false;
                }
                __state=new Scope{DiskStamp=diskStamp,DiskRegion=region.DiskKey,Method=__originalMethod,RegionVertices=region.Vertices,Parent=active,Listener=__instance,Entry=entry,Stamp=stamp,Schema=schema,Path=__0,Key=key,
                    Epoch=epoch,BeforeVertices=(long)schema.VertexCount.GetValue(__instance)};
                active=__state;Misses++;return true;
            }
        }
        private static void Read(string __0,byte[] __result) {if(active!=null && active.Path==__0)active.Bytes=__result;}
        private static Exception After(Exception __exception,bool __result,Scope __state) {
            if(__state==null)return __exception;
            try {
                lock(Gate) {
                    if(__exception!=null || __state.Epoch!=epoch || __state.Bytes==null ||
                        !ReferenceEquals(FileManager.GetFileEntry(__state.Path,true),__state.Entry) || !Equal(Stamp(__state.Entry),__state.Stamp))return __exception;
                    long count=(long)__state.Schema.VertexCount.GetValue(__state.Listener)-__state.BeforeVertices;
                    if(count<0 || count*16L>__state.Bytes.LongLength-4)return __exception;
                    // Bounds include receipts/regions even after weak keys die, never resource payloads.
                    if(Files.Count>=24576 || results>=49152) {Clear();return __exception;}
                    Receipt receipt;
                    if(!Files.TryGetValue(__state.Entry,out receipt) || !Equal(receipt,__state.Stamp))receipt=__state.Stamp;
                    if(receipt.Results==null)receipt.Results=new Dictionary<int,Result>();
                    if(!receipt.Results.ContainsKey(__state.Key))results++;
                    Result completed=new Result{Value=__result,Vertices=count};
                    receipt.Results[__state.Key]=completed;
                    var currentSet=__state.Schema.Set.GetValue(__state.Listener) as HashSet<int>;
                    if(currentSet!=null && __state.RegionVertices.SetEquals(currentSet))
                        ClassificationDiskReceipts.Record(__state.Entry,__state.Schema.Hash,__state.DiskRegion,__state.Method,__state.DiskStamp,completed);
                    Files[__state.Entry]=receipt;
                }
                return __exception;
            } catch(Exception) {
                // A failed optional post-observation cannot replace the original classifier outcome.
                return __exception;
            } finally {
                active=__state.Parent;
                __state.Parent=null;__state.Listener=null;__state.Entry=null;__state.Bytes=null;__state.Schema=null;__state.Stamp=null;
                __state.Path=null;__state.DiskStamp=null;__state.DiskRegion=null;__state.Method=null;__state.RegionVertices=null;
            }
        }
        public static void Stop() {new Harmony(Id).UnpatchAll(Id);Clear();lock(Gate){Schemas.Clear();Seen.Clear();}}
    }
}
