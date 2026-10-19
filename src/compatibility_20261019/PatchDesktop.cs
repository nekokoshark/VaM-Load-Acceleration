using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

public class PatchDesktop
{
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    { foreach (var t in types) { yield return t; foreach (var n in Types(t.NestedTypes)) yield return n; } }
    static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    static TypeDefinition AddHelper(ModuleDefinition module, string path)
    {
        using (var helper = AssemblyDefinition.ReadAssembly(path))
        {
            var source = helper.MainModule.Types.Single(t => t.Name == "DesktopBrowserLayout");
            var type = new TypeDefinition(source.Namespace, source.Name, source.Attributes, module.ImportReference(source.BaseType));
            module.Types.Add(type);
            foreach (var f in source.Fields) type.Fields.Add(new FieldDefinition(f.Name, f.Attributes, module.ImportReference(f.FieldType)));
            foreach (var sm in source.Methods)
            {
                var method = new MethodDefinition(sm.Name, sm.Attributes, module.ImportReference(sm.ReturnType));
                type.Methods.Add(method);
                foreach (var p in sm.Parameters) method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType)));
            }
            foreach (var sm in source.Methods)
            {
                var method = type.Methods.Single(m => m.Name == sm.Name);
                method.Body.InitLocals = sm.Body.InitLocals; method.Body.MaxStackSize = sm.Body.MaxStackSize;
                foreach (var v in sm.Body.Variables) method.Body.Variables.Add(new VariableDefinition(module.ImportReference(v.VariableType)));
                var map = new Dictionary<Instruction, Instruction>();
                foreach (var i in sm.Body.Instructions) { var n = Instruction.Create(OpCodes.Nop); n.OpCode = i.OpCode; map.Add(i, n); method.Body.Instructions.Add(n); }
                foreach (var i in sm.Body.Instructions)
                {
                    object o = i.Operand;
                if (o is Instruction) o = map[(Instruction)o];
                    else if (o is Instruction[]) o = ((Instruction[])o).Select(x => map[x]).ToArray();
                    else if (o is VariableDefinition) o = method.Body.Variables[sm.Body.Variables.IndexOf((VariableDefinition)o)];
                    else if (o is ParameterDefinition) o = method.Parameters[sm.Parameters.IndexOf((ParameterDefinition)o)];
                    else if (o is MethodReference) { var r = (MethodReference)o; o = r.DeclaringType.FullName == source.FullName ? type.Methods.Single(m => m.Name == r.Name) : module.ImportReference(r); }
                    else if (o is FieldReference) { var r = (FieldReference)o; o = r.DeclaringType.FullName == source.FullName ? type.Fields.Single(f => f.Name == r.Name) : module.ImportReference(r); }
                    else if (o is TypeReference) o = module.ImportReference((TypeReference)o);
                    map[i].Operand = o;
                }
                Require(sm.Body.ExceptionHandlers.Count == 0, "Unexpected helper handlers");
            }
            return type;
        }
    }
    static MethodDefinition Method(ModuleDefinition m, string type, string name, int args = -1)
    {
        var matches = Types(m.Types).Where(t => t.Name == type).SelectMany(t => t.Methods).Where(x => x.Name == name && (args < 0 || x.Parameters.Count == args)).ToArray();
        Require(matches.Length == 1, "Method identity: " + type + "." + name + " count=" + matches.Length);
        return matches[0];
    }
    static Instruction Field(MethodDefinition m, string field)
    {
        var f = m.DeclaringType.Fields.Single(x => x.Name == field);
        return Instruction.Create(f.IsStatic ? OpCodes.Ldsfld : OpCodes.Ldfld, f);
    }
    static void Prefix(MethodDefinition m, List<Instruction> code)
    {
        var il = m.Body.GetILProcessor(); var first = m.Body.Instructions[0];
        foreach (var i in code) il.InsertBefore(first, i);
        m.Body.MaxStackSize = Math.Max(12, m.Body.MaxStackSize);
    }
    static void Returns(MethodDefinition m, Func<List<Instruction>> code)
    {
        var il = m.Body.GetILProcessor();
        foreach (var ret in m.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray())
        {
            var extra = code(); var first = extra[0];
            foreach (var i in m.Body.Instructions.ToArray())
            {
                if (ReferenceEquals(i.Operand, ret)) i.Operand = first;
                var branches = i.Operand as Instruction[];
                if (branches != null) for (int n = 0; n < branches.Length; n++) if (branches[n] == ret) branches[n] = first;
            }
            foreach (var h in m.Body.ExceptionHandlers)
            {
                if (h.TryEnd == ret) h.TryEnd = first;
                if (h.HandlerEnd == ret) h.HandlerEnd = first;
            }
            foreach (var i in extra) il.InsertBefore(ret, i);
        }
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            string name = i.OpCode.Name.Substring(0, i.OpCode.Name.Length - 2);
            i.OpCode = (OpCode)typeof(OpCodes).GetFields().Single(f => f.FieldType == typeof(OpCode) && ((OpCode)f.GetValue(null)).Name == name).GetValue(null);
        }
        m.Body.MaxStackSize = Math.Max(12, m.Body.MaxStackSize);
    }
    static List<Instruction> BrowserArgs(MethodDefinition m)
    { return new List<Instruction> { Instruction.Create(OpCodes.Ldarg_0), Field(m,"_canvas"), Instruction.Create(OpCodes.Ldarg_0), Field(m,"_pickMode"), Instruction.Create(OpCodes.Ldarg_0), Field(m,"_compact"), Instruction.Create(OpCodes.Or) }; }
    static HashSet<string> Patch(ModuleDefinition module, string helperPath)
    {
        var helper = AddHelper(module, helperPath);
        Func<string, Instruction> call = name => Instruction.Create(OpCodes.Call, helper.Methods.Single(m => m.Name == name));
        var changed = new HashSet<string>();
        var recenter = Method(module,"VrPresetBrowser","Recenter");
        var p = BrowserArgs(recenter); p.Add(call("Apply")); p.Add(Instruction.Create(OpCodes.Brfalse,recenter.Body.Instructions[0])); p.Add(Instruction.Create(OpCodes.Ret)); Prefix(recenter,p); changed.Add(recenter.FullName);
        var tick = Method(module,"VrPresetBrowser","Tick"); p = BrowserArgs(tick); p.Add(call("Apply")); p.Add(Instruction.Create(OpCodes.Pop)); Prefix(tick,p); changed.Add(tick.FullName);
        var active = Method(module,"Quest3TriggerUIPlugin","get_InputRuntimeActive");
        Prefix(active,new List<Instruction>{call("IsDesktop"),Instruction.Create(OpCodes.Brfalse,active.Body.Instructions[0]),Instruction.Create(OpCodes.Ldc_I4_0),Instruction.Create(OpCodes.Ret)}); changed.Add(active.FullName);
        foreach (string name in new[]{"CurrentLookTarget","CurrentCursor"})
        {
            var m = Method(module,"VrPointerPresentation",name,1);
            Prefix(m,new List<Instruction>{call("IsDesktop"),Instruction.Create(OpCodes.Brfalse,m.Body.Instructions[0]),Instruction.Create(OpCodes.Ldc_I4_0),Instruction.Create(OpCodes.Starg,m.Parameters[0])}); changed.Add(m.FullName);
        }
        var ray = Method(module,"VrPointerPresentation","TryGetPointerRay");
        Prefix(ray,new List<Instruction>{call("IsDesktop"),Instruction.Create(OpCodes.Brfalse,ray.Body.Instructions[0]),Instruction.Create(OpCodes.Ldarg_0),call("MouseRay"),Instruction.Create(OpCodes.Ret)}); changed.Add(ray.FullName);
        var surface = Method(module,"VrPresetBrowser","SurfacePoint");
        Prefix(surface,new List<Instruction>{call("IsDesktop"),Instruction.Create(OpCodes.Brfalse,surface.Body.Instructions[0]),Instruction.Create(OpCodes.Ldarg_0),Field(surface,"_gridContent"),Instruction.Create(OpCodes.Ldarg_2),Instruction.Create(OpCodes.Ldarg_3),call("Surface"),Instruction.Create(OpCodes.Ret)}); changed.Add(surface.FullName);
        var presentation = Method(module,"UiAssistHudLink","ApplyPanelPresentation");
        Prefix(presentation,new List<Instruction>{Field(presentation,"_panelRoot"),Field(presentation,"_presetList"),call("FitEditor")}); changed.Add(presentation.FullName);
        Returns(presentation,()=>new List<Instruction>{Field(presentation,"_editorVisibilityButton"),call("FitToggle")});
        var clear = Method(module,"UiAssistHudLink","ClearPanelPresentation"); Prefix(clear,new List<Instruction>{call("RestoreEditor")}); changed.Add(clear.FullName);
        foreach (string entry in new[]{"TickFavoritesBar:_favDock:FitLeft","TickPresetDock:_pdDock:FitLeft","TickSceneDock:_sdDock:FitLeft","TickBanBar:_banDock:FitBan","TickLockBar:_lockDock:FitLock"})
        {
            string[] parts=entry.Split(':'); var m=Method(module,"UiAssistHudLink",parts[0]);
            Returns(m,()=>new List<Instruction>{Field(m,parts[1]),call(parts[2])}); changed.Add(m.FullName);
            if(parts[0]=="TickLockBar") Returns(m,()=>new List<Instruction>{Field(m,"_outfitLockRect"),call("FitOutfit")});
        }
        var expression = Method(module,"ExpressionBrowserPanel","BindEditorDock");
        Returns(expression,()=>new List<Instruction>{Instruction.Create(OpCodes.Ldarg_0),Field(expression,"root"),call("FitExpression")}); changed.Add(expression.FullName);
        var toolbar = Method(module,"PresetBrowserFileTools","Tick");
        Returns(toolbar,()=>new List<Instruction>{Instruction.Create(OpCodes.Ldarg_0),Field(toolbar,"_root"),call("FitToolbar")}); changed.Add(toolbar.FullName);
        return changed;
    }
    public static string Body(MethodDefinition m)
    {
        if (!m.HasBody) return "NO_BODY";
        Func<Instruction,string> index = i => i == null ? "end" : m.Body.Instructions.IndexOf(i).ToString();
        Func<object,string> operand = o => o == null ? "" : o is Instruction ? "target:"+index((Instruction)o) : o is Instruction[] ? string.Join(",",((Instruction[])o).Select(index).ToArray()) : o.ToString();
        return string.Join("\n",m.Body.Instructions.Select(i=>i.OpCode+" "+operand(i.Operand)).ToArray()) + "\nV:" + string.Join(",",m.Body.Variables.Select(v=>v.VariableType.FullName).ToArray()) + "\nE:" + string.Join(",",m.Body.ExceptionHandlers.Select(h=>h.HandlerType+"|"+h.CatchType+"|"+index(h.TryStart)+"|"+index(h.TryEnd)+"|"+index(h.HandlerStart)+"|"+index(h.HandlerEnd)+"|"+index(h.FilterStart)).ToArray());
    }
    static void Verify(string baseline, string modified, string helper)
    {
        using(var a=AssemblyDefinition.ReadAssembly(baseline))
        using(var expected=AssemblyDefinition.ReadAssembly(baseline))
        using(var b=AssemblyDefinition.ReadAssembly(modified))
        {
            var changes=Patch(expected.MainModule,helper); int untouched=0, total=0;
            var original=Types(a.MainModule.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
            var actual=Types(b.MainModule.Types).ToDictionary(t=>t.FullName);
            Require(a.Name.FullName==b.Name.FullName,"Assembly identity changed");
            foreach(var t in Types(expected.MainModule.Types))
            {
                var u=actual[t.FullName]; Require(t.Attributes==u.Attributes,"Type metadata changed");
                Require(t.Fields.Select(f=>f.FullName+f.Attributes).SequenceEqual(u.Fields.Select(f=>f.FullName+f.Attributes)),"Fields changed");
                Require(t.Methods.Count==u.Methods.Count,"Methods changed");
                foreach(var m in t.Methods)
                {
                    var n=u.Methods.Single(x=>x.FullName==m.FullName);
                    Require(m.Attributes==n.Attributes && Body(m)==Body(n),"IL mismatch: "+m.FullName);
                    if(original.ContainsKey(m.FullName)) { total++; if(!changes.Contains(m.FullName)){Require(Body(original[m.FullName])==Body(n),"Unrelated method changed");untouched++;} }
                }
            }
            Require(actual.Count==Types(a.MainModule.Types).Count()+1,"Unexpected added types");
            Require(a.MainModule.Resources.Count==b.MainModule.Resources.Count,"Resource count changed");
            for(int i=0;i<a.MainModule.Resources.Count;i++)
            {
                var x=(EmbeddedResource)a.MainModule.Resources[i];var y=(EmbeddedResource)b.MainModule.Resources[i];
                Require(x.Name==y.Name && x.GetResourceData().SequenceEqual(y.GetResourceData()),"Resource bytes changed");
            }
            Require(!b.MainModule.AssemblyReferences.Any(r=>r.Name=="DesktopHelper"),"External helper dependency");
            Console.WriteLine("CONTRACT_PASS originalMethods="+total+" unchangedMethods="+untouched+" desktopHooks="+changes.Count+" resources="+a.MainModule.Resources.Count+" externalHelper=0");
        }
    }
    static int Main(string[] args)
    {
        try
        {
            if(args[0]=="patch") using(var a=AssemblyDefinition.ReadAssembly(args[1])) { var c=Patch(a.MainModule,args[2]);a.Write(args[3]);Console.WriteLine("PATCH_PASS desktopHooks="+c.Count+" originalIdentityPreserved=1"); }
            else if(args[0]=="verify") Verify(args[1],args[2],args[3]);
            else if(args[0]=="inspect") using(var a=AssemblyDefinition.ReadAssembly(args[1]))
                foreach(var t in Types(a.MainModule.Types)) foreach(var m in t.Methods.Where(x=>x.HasBody && (args.Length==2 || (t.Name+"."+x.Name).Contains(args[2])))) Console.WriteLine(m.FullName+"\n"+Body(m));
            else throw new ArgumentException("Unknown action");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 70;}
    }
}
