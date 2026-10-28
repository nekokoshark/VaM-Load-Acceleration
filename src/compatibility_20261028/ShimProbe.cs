using System;
using System.IO;
using Mono.Cecil;
using HarmonyXInterop;
class ShimProbe {
 static int Main(string[] args) {
  try {
   var resolver=new DefaultAssemblyResolver();
   resolver.AddSearchDirectory(Path.GetDirectoryName(args[0]));
   resolver.AddSearchDirectory(Path.GetDirectoryName(typeof(HarmonyInterop).Assembly.Location));
   resolver.AddSearchDirectory(args[2]);
   byte[] before=File.ReadAllBytes(args[0]);int shims=0;
   HarmonyInterop.Initialize(Path.Combine(args[1],"cache"));
   var output=HarmonyInterop.TryShim(args[0],args[1],message=>{Console.WriteLine(message);if(message.StartsWith("Shimming "))shims++;},new ReaderParameters{AssemblyResolver=resolver});
   Console.WriteLine("SHIM_RESULT shims="+shims+" returned="+(output==null?0:output.Length)+" before="+before.Length);
   return 0;
  } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
 }
}
