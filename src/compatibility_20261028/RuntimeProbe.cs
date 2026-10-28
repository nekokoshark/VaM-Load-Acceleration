using System;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using HarmonyLib;
public static class CollectorProbe {
 const BindingFlags All=BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
 static object Call(Type t,string method,params object[] args) {return t.GetMethod(method,All).Invoke(null,args);}
 static int PatchCount() {return Harmony.GetAllPatchedMethods().Sum(m=> {var p=Harmony.GetPatchInfo(m);return p.Prefixes.Count+p.Postfixes.Count+p.Transpilers.Count+p.Finalizers.Count;});}
 [MethodImpl(MethodImplOptions.NoInlining)] public static int UiTarget() {return 1;}
 public static bool UiPrefix(ref int __result) {__result=2;return false;}
 public static string Run() {
  try {return Check();} catch(Exception e) {Console.WriteLine("PROBE_EXCEPTION "+e);throw;}
 }
 static string Check() {
  string root=Path.GetDirectoryName(typeof(CollectorProbe).Assembly.Location);
  var memory=Assembly.LoadFrom(Path.Combine(root,"VaM.Memory.dll"));
  var log=Logger.CreateLogSource("CompatibilityProbe");
  log.LogEvent+=(sender,e)=>Console.WriteLine("PLUGIN_LOG "+e.Data);
  memory.GetType("Quest3TriggerUI.Quest3TriggerUIPlugin",true).GetField("Log",All).SetValue(null,log);
  Console.WriteLine("RUNTIME bepinex="+typeof(Logger).Assembly.GetName().Version+" harmony="+typeof(Harmony).Assembly.GetName().Version);
  string[] names={"TextureCompletionBudget","TextureUploadReuse","TextureInFlight","TextureCacheByteReuse","TextureMetadataReuse","MorphTargetStringReuse"};
  int installed=0;
  foreach(string name in names) {
   int before=PatchCount();
   Call(memory.GetType("Quest3TriggerUI."+name,true),"Install");
   int after=PatchCount();
   if(after<=before)throw new InvalidOperationException("No new patches: "+name);
   installed++;
   Console.WriteLine("PATCH_PASS "+name+" targets="+(after-before));
  }
  var classification=memory.GetType("VaM.Memory.ClassificationStreamCompatibility",true);
  RuntimeHelpers.RunClassConstructor(classification.TypeHandle);
  if(classification.GetField("Constructor",All).GetValue(null)==null)throw new Exception("Constructor lookup failed");
  Console.WriteLine("CONSTRUCTOR_PASS VarFileEntryStream");
  // Exercise the patched native morph constructor, not just patch registration.
  var game=Assembly.Load("Assembly-CSharp");
  var formula=game.GetType("DAZMorphFormula",true);
  if(Activator.CreateInstance(formula)==null)throw new Exception("Native morph constructor failed");
  Console.WriteLine("NATIVE_MORPH_PASS constructor=True");
  var snapshot=memory.GetType("VaM.Memory.MorphSnapshotState",true);
  var harmonyType=snapshot.GetMethod("Install",All).GetParameters()[0].ParameterType;
  object morphHarmony=Activator.CreateInstance(harmonyType,new object[]{"vam.memory.morph-snapshot-state"});
  int previous=PatchCount();
  Call(snapshot,"Install",morphHarmony);
  if(PatchCount()<=previous)throw new Exception("Morph-bank patch missing");
  Console.WriteLine("MORPH_BANK_PASS patches="+(PatchCount()-previous));
  var qtype=game.GetType("ImageLoaderThreaded+QueuedImage",true);
  foreach(int format in new[]{3,4}) {
   object q=Activator.CreateInstance(qtype);
   foreach(string name in new[]{"setSize","isThumbnail"})qtype.GetField(name,All).SetValue(q,true);
   qtype.GetField("createMipMaps",All).SetValue(q,false);
   foreach(string name in new[]{"width","height"})qtype.GetField(name,All).SetValue(q,8);
   var f=qtype.GetField("textureFormat",All);f.SetValue(q,Enum.ToObject(f.FieldType,format));
   using(var bitmap=new Bitmap(8,8,format==3?PixelFormat.Format24bppRgb:PixelFormat.Format32bppArgb))using(var graphics=Graphics.FromImage(bitmap))using(var stream=new MemoryStream()) {
    graphics.Clear(Color.FromArgb(255,32,64,128));bitmap.Save(stream,ImageFormat.Png);stream.Position=0;
    object lease=Call(memory.GetType("Quest3TriggerUI.ColdRasterDecode",true),"Decode",q,stream);
    try {lease.GetType().GetMethod("Publish",All).Invoke(lease,new[]{q});} finally {((IDisposable)lease).Dispose();}
   }
   var raw=(byte[])qtype.GetField("raw",All).GetValue(q);
   if(Convert.ToInt32(f.GetValue(q))!=format)throw new Exception("Decoded format mismatch");
   for(int pixel=0;pixel<64;pixel++) {
    if(raw[pixel*format]!=32 || raw[pixel*format+1]!=64 || raw[pixel*format+2]!=128 || (format==4 && raw[pixel*format+3]!=255))
     throw new Exception("Thumbnail pixel mismatch format="+format+" pixel="+pixel+" actual="+string.Join(",",raw.Take(16).Select(v=>v.ToString()).ToArray()));
   }
   Console.WriteLine("THUMBNAIL_PIXELS_PASS format="+format+" pixels=64 color=32,64,128 alpha=255");
  }
  harmonyType.GetMethod("UnpatchAll",new[]{typeof(string)}).Invoke(morphHarmony,new object[]{"vam.memory.morph-snapshot-state"});
  var ui=Assembly.LoadFrom(Path.Combine(root,"Quest3TriggerUI.payload.dll.disabled"));
  var compat=ui.GetTypes().Single(t=>t.Name=="HarmonyCompat");
  var uiHarmony=new Harmony("compatibility.ui-probe");
  compat.GetMethod("Patch",All).Invoke(null,new object[]{uiHarmony,typeof(CollectorProbe).GetMethod("UiTarget"),new HarmonyMethod(typeof(CollectorProbe).GetMethod("UiPrefix")),null,null,null});
  if(UiTarget()!=2)throw new Exception("UI Harmony wrapper failed");
  uiHarmony.UnpatchSelf();
  if(UiTarget()!=1)throw new Exception("UI Harmony cleanup failed");
  Console.WriteLine("UI_WRAPPER_PASS patched=2 restored=1");
  for(int i=names.Length-1;i>=0;i--)Call(memory.GetType("Quest3TriggerUI."+names[i],true),"Shutdown");
  if(Harmony.GetAllPatchedMethods().Any(m=>Harmony.GetPatchInfo(m).Owners.Any(o=>o.StartsWith("Quest3TriggerUI."))))
   throw new Exception("Patch cleanup incomplete");
  return "RUNTIME_PROBE_PASS modules="+(installed+1)+" constructor=True morph=True thumbnailPixels=True ui=True cleanup=True";
 }
}
