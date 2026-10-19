using System;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchCompiler
{
    static void Require(bool c,string s){if(!c)throw new InvalidOperationException(s);}
    static MethodDefinition MainMethod(ModuleDefinition m){return m.Types.Single(t=>t.Name=="CompilerWorker").Methods.Single(x=>x.Name=="Main");}
    static void PatchWorker(string input,string output)
    {
        using(var a=AssemblyDefinition.ReadAssembly(input))
        {
            var m=a.MainModule;var main=MainMethod(m);var il=main.Body.GetILProcessor();
            var original=main.Body.Instructions.Single(i=>i.Operand is MethodReference&&((MethodReference)i.Operand).FullName=="System.IO.Stream System.Console::OpenStandardOutput()");
            var core=m.TypeSystem.CoreLibrary;
            var env=new TypeReference("System","Environment",m,core);
            var intptr=new TypeReference("System","IntPtr",m,core,true);
            var fs=new TypeReference("System.IO","FileStream",m,core);
            var access=new TypeReference("System.IO","FileAccess",m,core,true);
            var get=new MethodReference("GetEnvironmentVariable",m.TypeSystem.String,env);get.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
            var parse=new MethodReference("Parse",m.TypeSystem.Int64,m.TypeSystem.Int64);parse.Parameters.Add(new ParameterDefinition(m.TypeSystem.String));
            var ptr=new MethodReference(".ctor",m.TypeSystem.Void,intptr){HasThis=true};ptr.Parameters.Add(new ParameterDefinition(m.TypeSystem.Int64));
            var ctor=new MethodReference(".ctor",m.TypeSystem.Void,fs){HasThis=true};
            ctor.Parameters.Add(new ParameterDefinition(intptr));ctor.Parameters.Add(new ParameterDefinition(access));ctor.Parameters.Add(new ParameterDefinition(m.TypeSystem.Boolean));
            original.OpCode=OpCodes.Ldstr;original.Operand="VAM_COMPILER_REPLY_HANDLE";
            var after=new[]{Instruction.Create(OpCodes.Call,get),Instruction.Create(OpCodes.Call,parse),Instruction.Create(OpCodes.Newobj,ptr),Instruction.Create(OpCodes.Ldc_I4_2),Instruction.Create(OpCodes.Ldc_I4_0),Instruction.Create(OpCodes.Newobj,ctor)};
            var cursor=original;foreach(var i in after){il.InsertAfter(cursor,i);cursor=i;}
            // Widen branch operands after changing offsets.
            foreach(var i in main.Body.Instructions)
                if(i.OpCode.OperandType==OperandType.ShortInlineBrTarget)
                {string name=i.OpCode.Name.Substring(0,i.OpCode.Name.Length-2);i.OpCode=(OpCode)typeof(OpCodes).GetFields().Single(f=>f.FieldType==typeof(OpCode)&&((OpCode)f.GetValue(null)).Name==name).GetValue(null);}
            main.Body.MaxStackSize=Math.Max(main.Body.MaxStackSize,8);
            a.Write(output);
            Console.WriteLine("WORKER_PATCH_PASS reply=dedicatedHandle originalMVID="+m.Mvid);
        }
    }
    static void Resource(string input,string worker,string runner,string output)
    {
        using(var a=AssemblyDefinition.ReadAssembly(input))
        {
            var m=a.MainModule;
            foreach(var row in new[]{new[]{"VaM.Memory.CompilerWorker",worker},new[]{"VaM.Memory.CompilerRunner",runner}})
            {
                var old=m.Resources.Single(r=>r.Name==row[0]);int index=m.Resources.IndexOf(old);
                m.Resources[index]=new EmbeddedResource(old.Name,old.Attributes,File.ReadAllBytes(row[1]));
            }
            a.Write(output);Console.WriteLine("MEMORY_PATCH_PASS resources=2 originalMVID="+m.Mvid+" methodChanges=0");
        }
    }
    static void Noise(string input,string helper,string output)
    {
        using(var a=AssemblyDefinition.ReadAssembly(input))using(var h=AssemblyDefinition.ReadAssembly(helper))
        {
            var main=MainMethod(a.MainModule);var call=main.Body.Instructions.Single(i=>i.Operand is MethodReference&&((MethodReference)i.Operand).Name=="Compile"&&((MethodReference)i.Operand).DeclaringType.Name=="CompilerWorker");
            var emit=h.MainModule.Types.Single(t=>t.Name=="ProtocolNoise").Methods.Single(m=>m.Name=="Emit");
            main.Body.GetILProcessor().InsertBefore(call,Instruction.Create(OpCodes.Call,a.MainModule.ImportReference(emit)));
            a.Write(output);Console.WriteLine("NOISE_FIXTURE_PASS beforeCompile=managed+win32+crt");
        }
    }
    static int Main(string[] args)
    {try{if(args[0]=="worker")PatchWorker(args[1],args[2]);else if(args[0]=="resources")Resource(args[1],args[2],args[3],args[4]);else if(args[0]=="noise")Noise(args[1],args[2],args[3]);else if(args[0]=="extract") {
        using(var a=AssemblyDefinition.ReadAssembly(args[1])) {
            File.WriteAllBytes(args[2],((EmbeddedResource)a.MainModule.Resources.Single(r=>r.Name=="VaM.Memory.CompilerWorker")).GetResourceData());
            File.WriteAllBytes(args[3],((EmbeddedResource)a.MainModule.Resources.Single(r=>r.Name=="VaM.Memory.CompilerRunner")).GetResourceData());
        }
        Console.WriteLine("COMPILER_EXTRACT_PASS resources=2");
    }else throw new ArgumentException();return 0;}catch(Exception e){Console.Error.WriteLine(e);return 70;}}
}
