using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace VaM.Memory {
 // Every read creates a new mutable tree. A bounded decoded string table shares
 // immutable strings/instructions only; source and disk payload hashes remain
 // live on every read. No package/person/JSON tree is retained.
 public static partial class PackageTreeDisk {
  private const int Magic=0x31544A56,Limit=64*1024*1024,NodeLimit=1000000;
  private static string root;
  private static long epoch=-1;
  private static bool compatible;
  private static readonly byte[] Schema=typeof(JSONNode).Module.ModuleVersionId.ToByteArray();
  private static readonly Func<JSONClass,Dictionary<string,JSONNode>> ObjectData=Getter<JSONClass,Dictionary<string,JSONNode>>("m_Dict");
  private static readonly Func<JSONArray,List<JSONNode>> ArrayData=Getter<JSONArray,List<JSONNode>>("m_List");
  private static readonly Func<JSONData,string> ScalarData=Getter<JSONData,string>("m_Data");
  private static readonly Action<JSONClass,Dictionary<string,JSONNode>> SetObject=Setter<JSONClass,Dictionary<string,JSONNode>>("m_Dict");
  private static readonly Action<JSONArray,List<JSONNode>> SetArray=Setter<JSONArray,List<JSONNode>>("m_List");
  private static MethodBase[] dependencies;
  public static long Hits,Misses,Rejected,ReadTicks,SourceBytes;
  public static void Configure(string directory) {root=Path.GetFullPath(directory);epoch=-1;MorphSnapshotState.ObserveHooks();}
  public static string Stats(){return "hits="+Hits+" misses="+Misses+" rejected="+Rejected+" writes="+Writes+" writeRejected="+WriteRejected+" sourceBytes="+SourceBytes+" hitMs="+(ReadTicks*1000.0/Stopwatch.Frequency).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" writeMs="+(WriteTicks*1000.0/Stopwatch.Frequency).ToString("F1",System.Globalization.CultureInfo.InvariantCulture)+" treeRoots=0 blueprintHits="+BlueprintHits+" blueprintBuilds="+BlueprintBuilds+" blueprintBytes="+BlueprintBytes;}
  private static Func<T,V> Getter<T,V>(string name) {
   var f=AccessTools.Field(typeof(T),name);if(f==null || f.FieldType!=typeof(V))throw new MissingFieldException(typeof(T).FullName+"."+name);
   var m=new DynamicMethod("TreeRead"+name,typeof(V),new[]{typeof(T)},typeof(PackageTreeDisk),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldfld,f);il.Emit(OpCodes.Ret);return (Func<T,V>)m.CreateDelegate(typeof(Func<T,V>));
  }
  private static Action<T,V> Setter<T,V>(string name) {
   var f=AccessTools.Field(typeof(T),name);if(f==null || f.FieldType!=typeof(V))throw new MissingFieldException(typeof(T).FullName+"."+name);
   var m=new DynamicMethod("TreeWrite"+name,typeof(void),new[]{typeof(T),typeof(V)},typeof(PackageTreeDisk),true);
   var il=m.GetILGenerator();il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Stfld,f);il.Emit(OpCodes.Ret);return (Action<T,V>)m.CreateDelegate(typeof(Action<T,V>));
  }
  private static bool Compatible() {
   long now=MorphSnapshotState.HookEpoch;if(MorphSnapshotState.HooksChanging)return false;
   if(now==epoch)return compatible;
   if(dependencies==null) {
    var list=new List<MethodBase>();
    foreach(Type t in new[]{typeof(JSONNode),typeof(JSONClass),typeof(JSONArray),typeof(JSONData),typeof(JSON),typeof(Dictionary<string,JSONNode>),typeof(List<JSONNode>)}) {
     list.AddRange(t.GetMethods(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Static|BindingFlags.Instance|BindingFlags.DeclaredOnly));
     list.AddRange(t.GetConstructors(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance));
    }
    dependencies=list.ToArray();
   }
   bool result=true;
   foreach(var method in dependencies) {
    var info=Harmony.GetPatchInfo(method);if(info==null)continue;
    foreach(var patches in new[]{info.Prefixes,info.Postfixes,info.Transpilers,info.Finalizers})foreach(var patch in patches)
     if(!(method==JsonCapacityRuntime.Target && patch.PatchMethod==AccessTools.Method(typeof(JsonCapacityRuntime),"Rewrite")))result=false;
   }
   if(now!=MorphSnapshotState.HookEpoch || MorphSnapshotState.HooksChanging)return false;
   epoch=now;compatible=result;return result;
  }
  private static bool Same(byte[] a,byte[] b) {if(a.Length!=b.Length)return false;for(int i=0;i<a.Length;i++)if(a[i]!=b[i])return false;return true;}
  private static string CachePath(byte[] hash) {return Path.Combine(root,BitConverter.ToString(hash).Replace("-","")+".vjt");}
  public static bool TryRead(string path,FileEntryStreamReader owner,out JSONNode result) {
   WriteReceipt ignored;return TryReadForLoad(path,owner,out result,out ignored);
  }
  public static bool TryReadForLoad(string path,FileEntryStreamReader owner,out JSONNode result,out WriteReceipt receipt) {
   receipt=null;
   result=null;
   if(root==null || path==null || !path.EndsWith(".vamcachejson",StringComparison.OrdinalIgnoreCase) || !Compatible() ||
      owner==null || owner.GetType()!=typeof(SystemFileEntryStreamReader) || owner.StreamReader==null ||
      owner.StreamReader.GetType()!=typeof(StreamReader) || owner.StreamReader.CurrentEncoding.CodePage!=65001)return false;
   var source=owner.StreamReader.BaseStream as FileStream;
   if(source==null || source.GetType()!=typeof(FileStream) || !source.CanSeek || source.Position!=0 || source.Length>Limit)return false;
   long begin=Stopwatch.GetTimestamp();byte[] hash;
   // Hash the actual already-authorized stream, not a second path lookup.
   // Full content checks reject same-size/same-mtime replacements as well.
   try {hash=CacheSha256.Hash(source);SourceBytes+=source.Length;}
   finally {source.Position=0;}
   receipt=new WriteReceipt {SourceHash=hash,Directory=root,Epoch=MorphSnapshotState.HookEpoch};
   try {
    string cache=CachePath(hash);
    if(!File.Exists(cache)){Interlocked.Increment(ref Misses);return false;}
    using(var input=new FileStream(cache,FileMode.Open,FileAccess.Read,FileShare.Read))using(var reader=new BinaryReader(input)) {
     if(input.Length<88 || input.Length>Limit || reader.ReadInt32()!=Magic || !Same(reader.ReadBytes(16),Schema) || !Same(reader.ReadBytes(32),hash))throw new InvalidDataException("tree source/schema");
     byte[] checksum=reader.ReadBytes(32);int size=reader.ReadInt32();
     if(size<0 || size!=input.Length-input.Position)throw new InvalidDataException("tree length");
     byte[] data=reader.ReadBytes(size);if(data.Length!=size)throw new EndOfStreamException();
     if(!Same(checksum,CacheSha256.Hash(data,0,data.Length)))throw new InvalidDataException("tree checksum");
     result=DecodeWithBlueprint(data,checksum);
    }
    if(!Compatible()){result=null;return false;}
    receipt=null;Interlocked.Increment(ref Hits);Interlocked.Add(ref ReadTicks,Stopwatch.GetTimestamp()-begin);return true;
   }catch(InvalidDataException){Interlocked.Increment(ref Rejected);return false;}
   catch(IOException){Interlocked.Increment(ref Rejected);return false;}
   catch(UnauthorizedAccessException){Interlocked.Increment(ref Rejected);return false;}
  }
  private sealed class Cursor {
   internal readonly byte[] Data;internal int Position,Nodes;internal string[] Strings;
   internal Cursor(byte[] data){Data=data;}
   internal int Int() {if(Data.Length-Position<4)throw new InvalidDataException("tree truncated");int p=Position;Position+=4;return Data[p]|Data[p+1]<<8|Data[p+2]<<16|Data[p+3]<<24;}
   internal byte Tag(){if(Position==Data.Length)throw new InvalidDataException("tree truncated");return Data[Position++];}
   internal string Ref(){int n=Int();if(n==-1)return null;if(n<0 || n>=Strings.Length)throw new InvalidDataException("tree string index");return Strings[n];}
   internal unsafe string Text() {
    int n=Int();if(n<0 || n>(Data.Length-Position)/2)throw new InvalidDataException("tree text length");
    // Copy raw UTF-16 once: preserve lone surrogates and NUL without creating a
    // temporary char[] for every interned scalar/key. The disk is little endian.
    string value;
    if(BitConverter.IsLittleEndian) {fixed(byte* bytes=Data)value=new string((char*)(bytes+Position),0,n);}
    else {char[] chars=new char[n];for(int i=0;i<n;i++)chars[i]=(char)(Data[Position+i*2]|Data[Position+i*2+1]<<8);value=new string(chars);}
    Position+=n*2;return value;
   }
  }
  private static JSONNode Decode(byte[] bytes) {
   var c=new Cursor(bytes);int count=c.Int();if(count<0 || count>NodeLimit || count>(bytes.Length-4)/4)throw new InvalidDataException("tree string count");
   c.Strings=new string[count];for(int i=0;i<count;i++)c.Strings[i]=c.Text();
   JSONNode result=Node(c,0);if(c.Position!=bytes.Length)throw new InvalidDataException("tree trailing data");return result;
  }

  private const long BlueprintLimit=32L*1024*1024;
  private const int BlueprintCountLimit=128;
  private static readonly object BlueprintGate=new object();
  private static readonly Dictionary<string,LinkedListNode<Blueprint>> Blueprints=new Dictionary<string,LinkedListNode<Blueprint>>(StringComparer.Ordinal);
  private static readonly LinkedList<Blueprint> BlueprintLru=new LinkedList<Blueprint>();
  private static readonly HashSet<string> OversizedBlueprints=new HashSet<string>(StringComparer.Ordinal);
  private static long blueprintBytes;
  public static long BlueprintHits,BlueprintBuilds;
  public static long BlueprintBytes {get{lock(BlueprintGate)return blueprintBytes;}}
  // Share the existing deduplicated immutable string table only.
  // The source and payload remain fully hashed; JSON nodes stay per-read.
  private sealed class Blueprint {
   internal readonly string Key;
   internal readonly string[] Strings;
   internal readonly int RootPosition;
   internal readonly long Bytes;
   internal Blueprint(string key,string[] strings,int position,long bytes){Key=key;Strings=strings;RootPosition=position;Bytes=bytes;}
  }
  public static void ClearBlueprints(){lock(BlueprintGate){Blueprints.Clear();BlueprintLru.Clear();OversizedBlueprints.Clear();blueprintBytes=0;}}
  private static JSONNode DecodeWithBlueprint(byte[] bytes,byte[] verifiedChecksum) {
   string key=BitConverter.ToString(verifiedChecksum);
   Blueprint plan=null;bool oversized;
   lock(BlueprintGate) {
    LinkedListNode<Blueprint> found;
    if(Blueprints.TryGetValue(key,out found)){plan=found.Value;BlueprintLru.Remove(found);BlueprintLru.AddFirst(found);}
    oversized=OversizedBlueprints.Contains(key);
   }
   if(plan!=null) {
    var cached=new Cursor(bytes){Strings=plan.Strings,Position=plan.RootPosition};
    JSONNode tree=Node(cached,0);if(cached.Position!=bytes.Length)throw new InvalidDataException("tree trailing data");
    Interlocked.Increment(ref BlueprintHits);return tree;
   }
   var c=new Cursor(bytes);int count=c.Int();
   if(count<0 || count>NodeLimit || count>(bytes.Length-4)/4)throw new InvalidDataException("tree string count");
   c.Strings=new string[count];long retained=256+8L*count;
   for(int i=0;i<count;i++){c.Strings[i]=c.Text();retained+=32+2L*c.Strings[i].Length;}
   int position=c.Position;JSONNode result=Node(c,0);
   if(c.Position!=bytes.Length)throw new InvalidDataException("tree trailing data");
   if(oversized)return result;
   if(retained>BlueprintLimit) {
    lock(BlueprintGate){if(OversizedBlueprints.Count<BlueprintCountLimit)OversizedBlueprints.Add(key);}return result;
   }
   plan=new Blueprint(key,c.Strings,position,retained);
   lock(BlueprintGate) {
    if(!Blueprints.ContainsKey(key)) {
     while(BlueprintLru.Count!=0 && (Blueprints.Count>=BlueprintCountLimit || blueprintBytes+plan.Bytes>BlueprintLimit)) {
      var last=BlueprintLru.Last;Blueprints.Remove(last.Value.Key);blueprintBytes-=last.Value.Bytes;BlueprintLru.RemoveLast();
     }
     Blueprints.Add(key,BlueprintLru.AddFirst(plan));blueprintBytes+=plan.Bytes;Interlocked.Increment(ref BlueprintBuilds);
    }
   }
   return result;
  }
  private static JSONNode Node(Cursor c,int depth) {
   if(depth>128 || ++c.Nodes>NodeLimit)throw new InvalidDataException("tree node limit");
   byte tag=c.Tag();if(tag==0)return null;if(tag==1)return new JSONData(c.Ref());
   int count=c.Int();if(count<0 || count>NodeLimit-c.Nodes || count>c.Data.Length-c.Position)throw new InvalidDataException("tree child count");
   if(tag==2) {
    var array=new JSONArray();var list=new List<JSONNode>(count);
    for(int i=0;i<count;i++)list.Add(Node(c,depth+1));SetArray(array,list);return array;
   }
   if(tag==3) {
    var obj=new JSONClass();var dict=new Dictionary<string,JSONNode>(count);
    for(int i=0;i<count;i++){string key=c.Ref();if(key==null || dict.ContainsKey(key))throw new InvalidDataException("tree duplicate/null key");dict.Add(key,Node(c,depth+1));}
    SetObject(obj,dict);return obj;
   }
   throw new InvalidDataException("tree tag");
  }
  private sealed class Encoder {
   internal readonly Dictionary<string,int> Ids=new Dictionary<string,int>(StringComparer.Ordinal);
   internal readonly List<string> Strings=new List<string>();internal int Nodes;internal long Bytes=4;
   internal void Reserve(long bytes){Bytes+=bytes;if(Bytes>Limit-88)throw new InvalidDataException("tree byte limit");}
   internal int Id(string value){if(value==null)return -1;int n;if(Ids.TryGetValue(value,out n))return n;Reserve(4+2L*value.Length);n=Strings.Count;Strings.Add(value);Ids.Add(value,n);return n;}
   internal void Scan(JSONNode node,int depth) {
    if(depth>128 || ++Nodes>NodeLimit)throw new InvalidDataException("tree node limit");
    if(ReferenceEquals(node,null)){Reserve(1);return;}
    Reserve(5);
    if(node.GetType()==typeof(JSONData)){Id(ScalarData((JSONData)node));return;}
    if(node.GetType()==typeof(JSONArray)){foreach(var child in ArrayData((JSONArray)node))Scan(child,depth+1);return;}
    if(node.GetType()==typeof(JSONClass)){foreach(var pair in ObjectData((JSONClass)node)){Reserve(4);Id(pair.Key);Scan(pair.Value,depth+1);}return;}
    throw new InvalidDataException("tree unsupported node");
   }
   internal void WriteNode(BinaryWriter writer,JSONNode node) {
    if(ReferenceEquals(node,null)){writer.Write((byte)0);return;}
    if(node.GetType()==typeof(JSONData)){writer.Write((byte)1);writer.Write(Id(ScalarData((JSONData)node)));return;}
    if(node.GetType()==typeof(JSONArray)){writer.Write((byte)2);var list=ArrayData((JSONArray)node);writer.Write(list.Count);foreach(var child in list)WriteNode(writer,child);return;}
    writer.Write((byte)3);var dict=ObjectData((JSONClass)node);writer.Write(dict.Count);foreach(var pair in dict){writer.Write(Id(pair.Key));WriteNode(writer,pair.Value);}
   }
  }
  public static byte[] Encode(JSONNode tree) {
   var e=new Encoder();e.Scan(tree,0);
   using(var buffer=new MemoryStream())using(var writer=new BinaryWriter(buffer)) {
    writer.Write(e.Strings.Count);foreach(string text in e.Strings){writer.Write(text.Length);foreach(char c in text)writer.Write((ushort)c);}
    e.WriteNode(writer,tree);writer.Flush();if(buffer.Length>Limit-88)throw new InvalidDataException("tree byte limit");return buffer.ToArray();
   }
  }
  // Explicit offline build: the caller invokes the original parser twice. Reject
  // nondeterministic trees (e.g. empty keys that generate GUIDs), never freeze them.
  public static string Build(string sourcePath) {
   if(root==null)throw new InvalidOperationException("tree directory not configured");
   if(!Compatible())throw new InvalidDataException("tree foreign parser/node callbacks");
   long hookEpoch=MorphSnapshotState.HookEpoch;
   byte[] source=File.ReadAllBytes(sourcePath);if(source.Length>Limit)throw new InvalidDataException("tree source limit");byte[] hash;
   hash=CacheSha256.Hash(source,0,source.Length);
   string text;using(var stream=new MemoryStream(source,false))using(var reader=new StreamReader(stream))text=reader.ReadToEnd();
   byte[] a=Encode(JSON.Parse(text)),b=Encode(JSON.Parse(text));if(!Same(a,b))throw new InvalidDataException("tree nondeterministic parser");
   // Decoder roundtrip catches raw UTF-16, node/order and codec mistakes offline.
   if(!Same(a,Encode(Decode(a))))throw new InvalidDataException("tree codec roundtrip");
   if(hookEpoch!=MorphSnapshotState.HookEpoch || !Compatible())throw new InvalidDataException("tree callbacks changed during build");
   string target=CachePath(hash);Directory.CreateDirectory(root);string staged=target+"."+Guid.NewGuid().ToString("N")+".tmp";
   try {
    using(var stream=new FileStream(staged,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var writer=new BinaryWriter(stream)) {
     writer.Write(Magic);writer.Write(Schema);writer.Write(hash);writer.Write(CacheSha256.Hash(a,0,a.Length));writer.Write(a.Length);writer.Write(a);
    }
    if(File.Exists(target))File.Replace(staged,target,null);else File.Move(staged,target);return target;
   }finally {if(File.Exists(staged))File.Delete(staged);}
  }
 }
 public sealed partial class MemoryPlugin {
  private void ConfigurePackageTreeDisk() {
   if(Config.Bind("SceneLoading","PackageJsonBinaryCache",true,"Read offline built package JSON trees after full source SHA256 validation; per-read mutable trees, no retained trees, original parser on changed/corrupt/unavailable cache or foreign callbacks.").Value)
    InstallModule("PackageJsonBinaryCache",delegate {PackageTreeDisk.Configure(MemoryCachePaths.DirectoryFor("PackageTrees"));Logger.LogInfo("[package-tree-disk] configured sourceValidation=fullSHA256 trees=perRead startupBuild=False runtimeWrite=True root="+MemoryCachePaths.DirectoryFor("PackageTrees"));});
  }
 }
}
