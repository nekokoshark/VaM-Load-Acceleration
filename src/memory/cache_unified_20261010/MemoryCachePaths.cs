using System;
using System.IO;
using HarmonyLib;
using MVR.FileManagement;
using SimpleJSON;

namespace VaM.Memory {
 // Resolve once for this process. Tools and worker executables are not cache data.
 public static class MemoryCachePaths {
  private static string root;
  public static string Root {get {if(root==null)throw new InvalidOperationException("VaM cache root not initialized");return root;}}
  public static void Initialize(string gameRoot) {
   gameRoot=Path.GetFullPath(gameRoot);
   var singleton=AccessTools.Field(typeof(CacheManager),"singleton");
   if(singleton==null || singleton.FieldType!=typeof(CacheManager))throw new MissingFieldException("CacheManager.singleton");
   var manager=(CacheManager)singleton.GetValue(null);
   string directory=ReferenceEquals(manager,null)?null:manager.CacheDir;
   if(string.IsNullOrEmpty(directory)) {
    string prefs=Path.Combine(gameRoot,"prefs.json");
    if(File.Exists(prefs))directory=JSON.Parse(File.ReadAllText(prefs))["cacheFolder"].Value;
   }
   if(string.IsNullOrEmpty(directory))directory="Cache"; // Original CacheManager default.
   root=Path.GetFullPath(Path.IsPathRooted(directory)?directory:Path.Combine(gameRoot,directory));
  }
  public static string DirectoryFor(string name) {
   if(string.IsNullOrEmpty(name) || name=="." || name==".." || name.IndexOfAny(new[]{'/', '\\', ':'})>=0)
    throw new ArgumentException("cache subdirectory must be a single name","name");
   return Path.Combine(Path.Combine(Root,"VaMMemory"),name);
  }
 }
}
