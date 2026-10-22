using System;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
class FixturePatch
{
    static int Main(string[] args)
    {
        using(var harness=AssemblyDefinition.ReadAssembly(args[1]))
        using(var a=AssemblyDefinition.ReadAssembly(args[0]))
        {
            var h=harness.MainModule.Types.Single(t=>t.Name=="PointerHarness");
            Func<string,MethodReference> hook=n=>a.MainModule.ImportReference(h.Methods.Single(m=>m.Name==n));
            foreach(var t in a.MainModule.Types.Where(t=>new[]{"VrPointerPresentation","BrowserMouseInput","DesktopBrowserLayout","VrPresetBrowser"}.Contains(t.Name)))
            foreach(var m in t.Methods.Where(m=>m.HasBody))
            {
                foreach(var i in m.Body.Instructions)
                {
                    var call=i.Operand as MethodReference;
                    if(call==null)continue;
                    if(call.DeclaringType.FullName=="UnityEngine.Object" && (call.Name=="op_Equality"||call.Name=="op_Inequality"))i.Operand=hook(call.Name=="op_Equality"?"Same":"Different");
                    if(call.DeclaringType.FullName=="UnityEngine.Time" && call.Name=="get_unscaledTime")i.Operand=hook("Now");
                }
                if(t.Name!="VrPresetBrowser")continue;
                if(m.Name=="Close"||m.Name=="NavTo"||m.Name=="RefreshGrid")
                {
                    m.Body=new MethodBody(m);
                    if(m.Name!="RefreshGrid")
                    {m.Body.Instructions.Add(Instruction.Create(m.Name=="Close"?OpCodes.Ldarg_0:OpCodes.Ldarg_1));m.Body.Instructions.Add(Instruction.Create(OpCodes.Call,hook(m.Name=="Close"?"Closed":"Navigate")));}
                    m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                }
            }
            a.Write(args[2]);
        }
        Console.WriteLine("FIXTURE_READY engineSeams=equality+clock+UIclose+navigation shippingFilesUnchanged=True");return 0;
    }
}
