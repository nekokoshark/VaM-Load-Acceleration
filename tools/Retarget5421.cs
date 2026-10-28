using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
// Retarget only the verified ABI differences; never alter gameplay bodies/resources.
class Retarget5421 {
 static int Main(string[] args) {
  try {
   using(var harmony=ModuleDefinition.ReadModule(Path.Combine(args[2],"0Harmony.dll")))
   using(var m=ModuleDefinition.ReadModule(args[0])) {
    foreach(var r in m.AssemblyReferences) {
     if(r.Name=="BepInEx") r.Version=new Version(5,4,21,0);
     if(r.Name=="0Harmony") r.Version=new Version(2,9,0,0);
    }
    var patch=harmony.GetType("HarmonyLib.Harmony").Methods.Single(x=>x.Name=="Patch" && x.Parameters.Count==5);
    var ctor=harmony.GetType("HarmonyLib.AccessTools").Methods.Single(x=>x.Name=="Constructor" && x.Parameters.Count==3);
    if(patch.ReturnType.FullName!="System.Reflection.MethodInfo" || ctor.Parameters[2].ParameterType.FullName!="System.Boolean")
     throw new InvalidDataException("Unsupported Harmony ABI");
    int patches=0, constructors=0;
    foreach(var t in m.GetTypes()) foreach(var method in t.Methods) {
     if(!method.HasBody) continue;
     var code=method.Body.Instructions;
     foreach(var i in code.ToArray()) {
      var call=i.Operand as MethodReference;
      if(call==null || call.DeclaringType.Scope.Name!="0Harmony") continue;
      if(call.DeclaringType.FullName=="HarmonyLib.Harmony" && call.Name=="Patch" && call.Parameters.Count==5 && call.ReturnType.FullName=="System.Reflection.Emit.DynamicMethod") {
       if(i.Next==null || i.Next.OpCode!=OpCodes.Pop) throw new InvalidDataException("Non-discarded Patch result: "+method);
       i.Operand=m.ImportReference(patch); patches++;
      } else if(call.DeclaringType.FullName=="HarmonyLib.AccessTools" && call.Name=="Constructor" && call.Parameters.Count==2) {
       // Keep branch/EH anchors on the existing instruction, including the new argument.
       var opcode=i.OpCode; i.OpCode=OpCodes.Ldc_I4_0; i.Operand=null;
       method.Body.GetILProcessor().InsertAfter(i,Instruction.Create(opcode,m.ImportReference(ctor)));
       constructors++;
      }
     }
    }
    m.Write(args[1]);
    using(var reopened=ModuleDefinition.ReadModule(args[1])) {
     var expected=m.GetTypes().SelectMany(t=>t.Methods).Where(x=>x.HasBody).ToArray();
     var actual=reopened.GetTypes().SelectMany(t=>t.Methods).Where(x=>x.HasBody).ToArray();
     if(expected.Length!=actual.Length)throw new InvalidDataException("Method count changed");
     for(int n=0;n<expected.Length;n++) {
      var left=expected[n].Body.Instructions;var right=actual[n].Body.Instructions;
      if(left.Count!=right.Count)throw new InvalidDataException("Instruction count changed");
      for(int i=0;i<left.Count;i++) {
       if(left[i].OpCode!=right[i].OpCode)throw new InvalidDataException("Opcode changed");
       if(left[i].Operand is Instruction && (!(right[i].Operand is Instruction) || left.IndexOf((Instruction)left[i].Operand)!=right.IndexOf((Instruction)right[i].Operand)))
        throw new InvalidDataException("Branch encoding changed: "+expected[n]);
      }
     }
    }
    Console.WriteLine("RETARGET_PASS patch="+patches+" constructor="+constructors+" resources="+m.Resources.Count+" mvid="+m.Mvid);
   }
   return 0;
  } catch(Exception e) {Console.Error.WriteLine(e);return 1;}
 }
}
