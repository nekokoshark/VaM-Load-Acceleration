using System;
using System.CodeDom.Compiler;
using System.IO;
using System.Reflection;
public static class CollectorProbe {
 public static string Compile() {
  try {
   string[] settings=File.ReadAllLines("compile-settings.txt");
   string output=settings[0],managed=settings[1];
   var assembly=Assembly.LoadFrom(Path.Combine(managed,"Assembly-CSharp.dll"));
   Type type=assembly.GetType("DynamicCSharp.Compiler.McsCompiler",true);
   type.GetProperty("OutputDirectory",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,output,null);
   for(int i=0;i<2;i++) {
    var compiler=(ICodeCompiler)Activator.CreateInstance(type,true);
    var options=new CompilerParameters();options.GenerateInMemory=false;options.GenerateExecutable=false;
    options.OutputAssembly="Classifier"+i;options.WarningLevel=4;
    foreach(string file in Directory.GetFiles(managed,"*.dll")) {
     string n=Path.GetFileName(file);
     if(n=="mscorlib.dll"||n=="System.dll"||n=="System.Core.dll"||n=="Assembly-CSharp.dll"||n.StartsWith("UnityEngine",StringComparison.Ordinal))options.ReferencedAssemblies.Add(file);
    }
    var result=compiler.CompileAssemblyFromFileBatch(options,new[]{settings[2+i]});
    foreach(CompilerError e in result.Errors)if(!e.IsWarning)throw new InvalidDataException(e.ToString());
    Console.WriteLine("PROFILE_COMPILED "+i+" "+result.PathToAssembly);
   }
   return "PROFILE_COMPILE_PASS";
  } catch(Exception e){Console.WriteLine("TOOL_EXCEPTION: "+e);return "PROFILE_COMPILE_FAILED";}
 }
}
