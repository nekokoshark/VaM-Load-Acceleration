using System;
using System.Linq;
using System.Collections.Generic;
using Mono.Cecil;
class CompilerContract
{
    static IEnumerable<TypeDefinition> Types(IEnumerable<TypeDefinition> types){foreach(var t in types){yield return t;foreach(var n in Types(t.NestedTypes))yield return n;}}
    static void Check(bool c,string n){if(!c)throw new Exception(n);}
    static int Main(string[] args)
    {
        try
        {
            int methods=0,fields=0,resources=0;
            using(var a=AssemblyDefinition.ReadAssembly(args[0]))using(var b=AssemblyDefinition.ReadAssembly(args[1]))
            {
                Check(a.Name.FullName==b.Name.FullName&&a.MainModule.Mvid==b.MainModule.Mvid,"Memory identity/MVID");
                var aa=Types(a.MainModule.Types).ToArray();var bb=Types(b.MainModule.Types).ToDictionary(t=>t.FullName);
                Check(aa.Length==bb.Count,"Type count");
                foreach(var t in aa)
                {
                    var u=bb[t.FullName];Check(t.Attributes==u.Attributes,"Type metadata");
                    Check(t.Fields.Select(f=>f.FullName+f.Attributes).SequenceEqual(u.Fields.Select(f=>f.FullName+f.Attributes)),"Field metadata");fields+=t.Fields.Count;
                    Check(t.Methods.Count==u.Methods.Count,"Method count");
                    foreach(var m in t.Methods){var n=u.Methods.Single(x=>x.FullName==m.FullName);Check(m.Attributes==n.Attributes&&PatchDesktop.Body(m)==PatchDesktop.Body(n),m.FullName);methods++;}
                }
                Check(a.MainModule.AssemblyReferences.Select(x=>x.FullName).SequenceEqual(b.MainModule.AssemblyReferences.Select(x=>x.FullName)),"References");
                Check(a.MainModule.Resources.Count==b.MainModule.Resources.Count,"Resource count");
                foreach(var r in a.MainModule.Resources.Cast<EmbeddedResource>())
                {
                    var s=(EmbeddedResource)b.MainModule.Resources.Single(x=>x.Name==r.Name);
                    bool changed=!r.GetResourceData().SequenceEqual(s.GetResourceData());
                    bool expected=r.Name=="VaM.Memory.CompilerWorker"||r.Name=="VaM.Memory.CompilerRunner";
                    Check(changed==expected,"Resource "+r.Name);if(!expected)resources++;
                }
            }
            Console.WriteLine("COMPILER_CONTRACT_PASS memoryMethods="+methods+" fields="+fields+" unchangedResources="+resources+" changedResources=2 identity+MVID=preserved");
            using(var a=AssemblyDefinition.ReadAssembly(args[2]))using(var b=AssemblyDefinition.ReadAssembly(args[3]))
            {
                Check(a.Name.FullName==b.Name.FullName&&a.MainModule.Mvid==b.MainModule.Mvid,"Worker identity");
                var am=Types(a.MainModule.Types).SelectMany(t=>t.Methods).ToArray();var bm=Types(b.MainModule.Types).SelectMany(t=>t.Methods).ToDictionary(m=>m.FullName);
                Check(am.Length==bm.Count,"Worker method count");int changed=0;
                foreach(var m in am){if(PatchDesktop.Body(m)!=PatchDesktop.Body(bm[m.FullName])){changed++;Check(m.DeclaringType.Name=="CompilerWorker"&&m.Name=="Main","Worker unrelated method "+m.FullName);}}
                Check(changed==1,"Worker Main change only");
                Check(a.MainModule.AssemblyReferences.Select(x=>x.FullName).SequenceEqual(b.MainModule.AssemblyReferences.Select(x=>x.FullName)),"Worker refs");
                Console.WriteLine("WORKER_CONTRACT_PASS methods="+am.Length+" changedMethods=1 main=replyStreamOnly references=preserved");
            }
            return 0;
        }catch(Exception e){Console.WriteLine(e);return 70;}
    }
}
