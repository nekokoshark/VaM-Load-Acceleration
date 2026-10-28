using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
class InspectCompat {
 static int Main(string[] args) {
  var resolver = new DefaultAssemblyResolver();
  resolver.AddSearchDirectory(args[0]);
  resolver.AddSearchDirectory("F:/vam1.22.0.12/VaM_Data/Managed");
  foreach (string file in args.Skip(2)) {
   using (var m = ModuleDefinition.ReadModule(file, new ReaderParameters { AssemblyResolver = resolver })) {
    Console.WriteLine("MODULE " + m.Assembly.Name);
    foreach(var r in m.AssemblyReferences) Console.WriteLine("REF " + r);
    int checkedCount=0, failed=0;
    foreach (var t in m.GetTypes()) foreach (var method in t.Methods) {
     if (!method.HasBody) continue;
     bool dump = args[1] != "check" && (t.FullName + "." + method.Name).IndexOf(args[1], StringComparison.OrdinalIgnoreCase)>=0;
     if (dump) Console.WriteLine("METHOD " + method);
     foreach(var i in method.Body.Instructions) {
      if (dump) Console.WriteLine(i);
      var member = i.Operand as MemberReference;
      if (member == null || member.DeclaringType == null) continue;
      string scope = member.DeclaringType.Scope.Name;
      if (!(scope.StartsWith("BepInEx") || scope == "0Harmony" || scope.StartsWith("MonoMod") || scope.StartsWith("Mono.Cecil"))) continue;
      checkedCount++;
      try {
       bool found = member is MethodReference ? ((MethodReference)member).Resolve()!=null : member is FieldReference ? ((FieldReference)member).Resolve()!=null : true;
       if(!found) {failed++;Console.WriteLine("MISSING " + member);}
      } catch(Exception e) {failed++; Console.WriteLine("ERROR "+member+" "+e.Message);}
     }
    }
    Console.WriteLine("CONTRACT checked="+checkedCount+" failed="+failed);
    if(failed!=0) return 1;
   }
  }
  return 0;
 }
}
