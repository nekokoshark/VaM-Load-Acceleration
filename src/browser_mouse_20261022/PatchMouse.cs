using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

class PatchMouse
{
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types)
    { foreach (var t in types) { yield return t; foreach (var n in Types(t.NestedTypes)) yield return n; } }
    static void Require(bool value, string name)
    { if (!value) throw new InvalidOperationException(name); }
    static TypeDefinition AddHelper(ModuleDefinition module, string path)
    {
        using (var helper = AssemblyDefinition.ReadAssembly(path))
        {
            var source = helper.MainModule.Types.Single(t => t.Name == "BrowserMouseInput");
            var type = new TypeDefinition(source.Namespace, source.Name, source.Attributes, module.ImportReference(source.BaseType));
            module.Types.Add(type);
            Require(source.Fields.Count == 0 && source.NestedTypes.Count == 0, "Helper shape");
            foreach (var sm in source.Methods)
            {
                var method = new MethodDefinition(sm.Name, sm.Attributes, module.ImportReference(sm.ReturnType));
                type.Methods.Add(method);
                foreach (var p in sm.Parameters) method.Parameters.Add(new ParameterDefinition(p.Name, p.Attributes, module.ImportReference(p.ParameterType)));
            }
            foreach (var sm in source.Methods)
            {
                var method = type.Methods.Single(m => m.Name == sm.Name);
                method.Body.InitLocals = sm.Body.InitLocals;
                method.Body.MaxStackSize = sm.Body.MaxStackSize;
                foreach (var v in sm.Body.Variables) method.Body.Variables.Add(new VariableDefinition(module.ImportReference(v.VariableType)));
                var map = new Dictionary<Instruction, Instruction>();
                foreach (var i in sm.Body.Instructions)
                { var n = Instruction.Create(OpCodes.Nop); n.OpCode = i.OpCode; map.Add(i, n); method.Body.Instructions.Add(n); }
                foreach (var i in sm.Body.Instructions)
                {
                    object o = i.Operand;
                    if (o is Instruction) o = map[(Instruction)o];
                    else if (o is Instruction[]) o = ((Instruction[])o).Select(x => map[x]).ToArray();
                    else if (o is VariableDefinition) o = method.Body.Variables[sm.Body.Variables.IndexOf((VariableDefinition)o)];
                    else if (o is ParameterDefinition) o = method.Parameters[sm.Parameters.IndexOf((ParameterDefinition)o)];
                    else if (o is MethodReference)
                    { var r = (MethodReference)o; o = r.DeclaringType.FullName == source.FullName ? type.Methods.Single(m => m.Name == r.Name) : module.ImportReference(r); }
                    else if (o is FieldReference) o = module.ImportReference((FieldReference)o);
                    else if (o is TypeReference) o = module.ImportReference((TypeReference)o);
                    map[i].Operand = o;
                }
                Require(sm.Body.ExceptionHandlers.Count == 0, "Helper EH");
            }
            return type;
        }
    }
    static MethodDefinition Method(TypeDefinition t, string name, int count)
    { return t.Methods.Single(m => m.Name == name && m.Parameters.Count == count); }
    static void Prefix(MethodDefinition m, IEnumerable<Instruction> code)
    {
        var il = m.Body.GetILProcessor(); var first = m.Body.Instructions[0];
        foreach (var i in code) il.InsertBefore(first, i);
        m.Body.MaxStackSize = Math.Max(8, m.Body.MaxStackSize);
    }
    static void Expand(MethodDefinition m)
    {
        foreach (var i in m.Body.Instructions)
        {
            if (i.OpCode.OperandType != OperandType.ShortInlineBrTarget) continue;
            string name = i.OpCode.Name.Substring(0, i.OpCode.Name.Length - 2);
            i.OpCode = (OpCode)typeof(OpCodes).GetFields().Single(f => f.FieldType == typeof(OpCode) && ((OpCode)f.GetValue(null)).Name == name).GetValue(null);
        }
    }
    static HashSet<string> Patch(ModuleDefinition module, string helperPath)
    {
        var helper = AddHelper(module, helperPath);
        var pointer = Types(module.Types).Single(t => t.Name == "VrPointerPresentation");
        var browser = Types(module.Types).Single(t => t.Name == "VrPresetBrowser");
        var changed = new HashSet<string>();
        var init = pointer.Methods.Single(m => m.IsConstructor && m.IsStatic);
        foreach (string spec in new[] {"CurrentLookTarget:CurrentLookLeftRef:CurrentLookMouseRef:currentLookMouse", "CurrentCursor:CursorLeftRef:CursorMouseRef:cursorMouse"})
        {
            string[] parts = spec.Split(':');
            var originalField = pointer.Fields.Single(f => f.Name == parts[1]);
            var mouse = new FieldDefinition(parts[2], originalField.Attributes, originalField.FieldType);
            pointer.Fields.Add(mouse);
            var getter = init.Body.Instructions.Single(i => i.OpCode == OpCodes.Stsfld && ((FieldReference)i.Operand).Name == parts[1]).Previous.Operand as MethodReference;
            Require(getter != null && getter.Name == "LookField", "Cached field getter");
            Prefix(init, new[] {Instruction.Create(OpCodes.Ldstr, parts[3]), Instruction.Create(OpCodes.Call, getter), Instruction.Create(OpCodes.Stsfld, mouse)});
            var method = Method(pointer, parts[0], 1);
            var left = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldsfld && ((FieldReference)i.Operand).Name == parts[1]);
            var join = method.Body.Instructions.First(i => i.Offset > left.Offset && i.OpCode.Code.ToString().StartsWith("Stloc"));
            // Enter immediately before the existing right/left selection,
            // after its module-null guard (and any frozen desktop prefix).
            var select = left.Previous.Previous;
            Require(select.OpCode.Code == Code.Ldarg_0, "Pointer selection shape");
            var il = method.Body.GetILProcessor();
            foreach (var i in new[] {Instruction.Create(OpCodes.Call, helper.Methods.Single(m => m.Name == "IsDesktop")), Instruction.Create(OpCodes.Brfalse, select), Instruction.Create(OpCodes.Ldsfld, mouse), Instruction.Create(OpCodes.Br, join)}) il.InsertBefore(select, i);
            // The original null guard branches to select; route that edge
            // through the new selector too.
            var first = select.Previous.Previous.Previous.Previous;
            foreach (var i in method.Body.Instructions)
                if (ReferenceEquals(i.Operand, select) && i != first.Next) i.Operand = first;
            Expand(method); changed.Add(method.FullName);
        }
        changed.Add(init.FullName);
        foreach (string spec in new[] {"TryGetPointerRay:MouseRay", "SurfacePoint:Surface"})
        {
            string[] parts = spec.Split(':');
            var m = parts[0] == "SurfacePoint" ? Method(browser, parts[0], 3) : Method(pointer, parts[0], 1);
            var code = new List<Instruction> {Instruction.Create(OpCodes.Call, helper.Methods.Single(x => x.Name == "IsDesktop")), Instruction.Create(OpCodes.Brfalse, m.Body.Instructions[0])};
            if (parts[0] == "SurfacePoint")
            {
                code.Add(Instruction.Create(OpCodes.Ldarg_0)); code.Add(Instruction.Create(OpCodes.Ldfld, browser.Fields.Single(f => f.Name == "_gridContent")));
                code.Add(Instruction.Create(OpCodes.Ldarg_2)); code.Add(Instruction.Create(OpCodes.Ldarg_3));
            }
            else code.Add(Instruction.Create(OpCodes.Ldarg_0));
            code.Add(Instruction.Create(OpCodes.Call, helper.Methods.Single(x => x.Name == parts[1]))); code.Add(Instruction.Create(OpCodes.Ret));
            Prefix(m, code); changed.Add(m.FullName);
        }
        // Use one browser fit implementation for both build lines. Preserve
        // the r2 editor/dock layout fixes without calling Apply twice per tick.
        foreach (string name in new[] {"Recenter", "Tick"})
        {
            var m = Method(browser, name, 0);
            if (name == "Tick" && module.Types.Any(t => t.Name == "DesktopBrowserLayout"))
            {
                var old = m.Body.Instructions.Take(9).ToArray();
                Require(old[7].OpCode == OpCodes.Call && ((MethodReference)old[7].Operand).Name == "Apply" && old[8].OpCode == OpCodes.Pop, "Frozen desktop tick prefix");
                foreach (var i in old) m.Body.Instructions.Remove(i);
            }
            var code = new List<Instruction> {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, browser.Fields.Single(f => f.Name == "_canvas")),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, browser.Fields.Single(f => f.Name == "_pickMode")),
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, browser.Fields.Single(f => f.Name == "_compact")),
                Instruction.Create(OpCodes.Or), Instruction.Create(OpCodes.Call, helper.Methods.Single(x => x.Name == "FitBrowser"))};
            if (name == "Recenter") { code.Add(Instruction.Create(OpCodes.Brfalse, m.Body.Instructions[0])); code.Add(Instruction.Create(OpCodes.Ret)); }
            else code.Add(Instruction.Create(OpCodes.Pop));
            Prefix(m, code); changed.Add(m.FullName);
        }
        return changed;
    }
    static string Fingerprint(MethodDefinition method)
    {
        if (!method.HasBody) return "no-body";
        Func<object, string> operand = o => o == null ? "" : o is Instruction ? "@" + method.Body.Instructions.IndexOf((Instruction)o) : o is Instruction[] ? string.Join(",", ((Instruction[])o).Select(i => "@" + method.Body.Instructions.IndexOf(i)).ToArray()) : o.ToString();
        return string.Join("\n", method.Body.Instructions.Select(i => i.OpCode.Name + " " + operand(i.Operand)).ToArray()) + "\nEH:" + string.Join(";", method.Body.ExceptionHandlers.Select(e => e.HandlerType + ":" + operand(e.TryStart) + ":" + operand(e.TryEnd) + ":" + operand(e.HandlerStart) + ":" + operand(e.HandlerEnd)).ToArray());
    }
    static void Verify(string before, string after, HashSet<string> changed)
    {
        using (var a = AssemblyDefinition.ReadAssembly(before))
        using (var b = AssemblyDefinition.ReadAssembly(after))
        {
            Require(a.Name.FullName == b.Name.FullName && a.MainModule.Mvid == b.MainModule.Mvid, "Identity/MVID");
            var am = Types(a.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            var bm = Types(b.MainModule.Types).SelectMany(t => t.Methods).ToDictionary(m => m.FullName);
            int actual = 0;
            foreach (var pair in am)
            { bool differs = Fingerprint(pair.Value) != Fingerprint(bm[pair.Key]); Require(differs == changed.Contains(pair.Key), "Unexpected method delta " + pair.Key); if (differs) actual++; }
            Require(actual == changed.Count && bm.Count == am.Count + 4, "Method counts");
            Require(a.MainModule.Resources.Count == b.MainModule.Resources.Count, "Resources count");
            foreach (var r in a.MainModule.Resources.Cast<EmbeddedResource>()) Require(r.GetResourceData().SequenceEqual(b.MainModule.Resources.Cast<EmbeddedResource>().Single(x => x.Name == r.Name).GetResourceData()), "Resource " + r.Name);
            Console.WriteLine("CONTRACT_PASS changedMethods=" + actual + " unchangedMethods=" + (am.Count - actual) + " resources=" + a.MainModule.Resources.Count + " nativeTakeoverAndCallbacks=UNCHANGED identityAndMvid=UNCHANGED");
        }
    }
    static int Main(string[] args)
    {
        try
        {
            HashSet<string> changed;
            using (var a = AssemblyDefinition.ReadAssembly(args[0])) { changed = Patch(a.MainModule, args[1]); a.Write(args[2]); }
            Verify(args[0], args[2], changed);
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 70; }
    }
}
