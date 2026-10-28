using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;

class MutationPatch {
 static string Body(MethodDefinition m) {
  if(!m.HasBody)return "";
  return string.Join("\n",m.Body.Instructions.Select(i=>i.ToString()).ToArray());
 }
 static int Main(string[] a) {
  try {
   if(a[0]=="compile-reference") {
    using(var m=ModuleDefinition.ReadModule(a[1])) {
     foreach(var t in m.GetTypes())foreach(var f in t.Methods)
      foreach(var c in f.CustomAttributes.Where(x=>x.AttributeType.FullName=="System.ObsoleteAttribute").ToArray())f.CustomAttributes.Remove(c);
     m.Write(a[2]);
    }
    Console.WriteLine("COMPILE_REFERENCE_ONLY runtimeUnchanged=True");return 0;
   }
   using(var old=ModuleDefinition.ReadModule(a[0]))
   using(var donor=ModuleDefinition.ReadModule(a[1])) {
    var oldMethods=old.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
    var oldFields=old.GetTypes().SelectMany(t=>t.Fields).ToDictionary(m=>m.FullName);
    var oldTypes=old.GetTypes().ToDictionary(t=>t.FullName);
    var selected=new HashSet<string> {
     "VaM.Memory.MorphSnapshotState::InstallImportPatchObserver", "VaM.Memory.MorphSnapshotState::AllowedPatches",
     "ListingNamesRuntime::Foreign", "CanonicalPathsRuntime::Before", "JsonCapacityRuntime::CreateDictionary",
     "StartupMorphIndex.Runtime::PatchIdentity", "VaM.Memory.ClassificationDiskReceipts::Allowed",
     "VaM.Memory.ClassificationStreamCompatibility::Unpatched", "VaM.Memory.AssetAnatomyPass::Allowed",
     "VaM.Memory.PathCharacterSearch::Compatible", "VaM.Memory.AssetEntryMetadata::Compatible",
     "VaM.Memory.PackageTreeDisk::Compatible", "VaM.Memory.PackageNegativeLookup::Compatible",
     "VaM.Memory.VarDirectorySelection::Compatible"
    };
    int count=0;
    foreach(var src in donor.GetTypes().SelectMany(t=>t.Methods)) {
     if(!selected.Contains(src.DeclaringType.FullName+"::"+src.Name))continue;
     if(!src.HasBody)throw new InvalidDataException("Missing donor body");
     var dst=oldMethods[src.FullName];
     Console.WriteLine("REPLACE "+src.FullName);
     var body=new MethodBody(dst);dst.Body=body;
     body.InitLocals=src.Body.InitLocals;body.MaxStackSize=src.Body.MaxStackSize;
     foreach(var v in src.Body.Variables)body.Variables.Add(new VariableDefinition(old.ImportReference(v.VariableType)));
     var map=new Dictionary<Instruction,Instruction>();
     foreach(var ins in src.Body.Instructions) {
      var copy=Instruction.Create(OpCodes.Nop);copy.OpCode=ins.OpCode;copy.Operand=ins.Operand;
      body.Instructions.Add(copy);map.Add(ins,copy);
     }
     foreach(var ins in body.Instructions) {
      var op=ins.Operand;
      if(op is Instruction)ins.Operand=map[(Instruction)op];
      else if(op is Instruction[])ins.Operand=((Instruction[])op).Select(i=>map[i]).ToArray();
      else if(op is VariableDefinition)ins.Operand=body.Variables[((VariableDefinition)op).Index];
      else if(op is ParameterDefinition)ins.Operand=dst.Parameters[((ParameterDefinition)op).Index];
      else if(op is MethodReference) {
       var r=(MethodReference)op;
       ins.Operand=oldMethods.ContainsKey(r.FullName)?oldMethods[r.FullName]:old.ImportReference(r);
      } else if(op is FieldReference) {
       var r=(FieldReference)op;
       ins.Operand=oldFields.ContainsKey(r.FullName)?oldFields[r.FullName]:old.ImportReference(r);
      } else if(op is TypeReference) {
       var r=(TypeReference)op;
       ins.Operand=oldTypes.ContainsKey(r.FullName)?oldTypes[r.FullName]:old.ImportReference(r);
      }
     }
     foreach(var eh in src.Body.ExceptionHandlers)body.ExceptionHandlers.Add(new ExceptionHandler(eh.HandlerType) {
      TryStart=map[eh.TryStart],TryEnd=eh.TryEnd==null?null:map[eh.TryEnd],
      HandlerStart=map[eh.HandlerStart],HandlerEnd=eh.HandlerEnd==null?null:map[eh.HandlerEnd],
      FilterStart=eh.FilterStart==null?null:map[eh.FilterStart],CatchType=eh.CatchType==null?null:old.ImportReference(eh.CatchType)
     });
     count++;
    }
    if(count!=14)throw new InvalidDataException("Expected 14 changed method bodies, actual="+count);
    old.Write(a[2]);
    using(var check=ModuleDefinition.ReadModule(a[2])) {
     var actual=check.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
     if(actual.Count!=oldMethods.Count)throw new InvalidDataException("Method count changed");
     foreach(var m in oldMethods.Values)if(Body(m)!=Body(actual[m.FullName]))throw new InvalidDataException("Reopen IL mismatch "+m.FullName);
     for(int i=0;i<old.Resources.Count;i++) {
      var x=old.Resources[i] as EmbeddedResource;var y=check.Resources[i] as EmbeddedResource;
      if(x!=null && !x.GetResourceData().SequenceEqual(y.GetResourceData()))throw new InvalidDataException("Resource changed");
     }
     if(check.AssemblyReferences.Any(r=>r.Name=="VaM.Memory"))throw new InvalidDataException("Self reference");
    }
    Console.WriteLine("MUTATION_PATCH_PASS changedMethods="+count+" methods="+oldMethods.Count+" resources="+old.Resources.Count);
   }
   return 0;
  }catch(Exception e){Console.Error.WriteLine(e);return 1;}
 }
}
