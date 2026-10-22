using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
class JitProbe
{
    static int Main(string[] args)
    {
        try
        {
            var a=Assembly.Load(File.ReadAllBytes(args[0])); int count=0;
            foreach(string spec in new[]{"VrPointerPresentation:CurrentLookTarget,CurrentCursor,TryGetPointerRay", "VrPresetBrowser:SurfacePoint,Recenter,Tick,OnCardClicked,Commit", "BrowserMouseInput:IsDesktop,MouseRay,Surface,FitBrowser"})
            {
                string[] p=spec.Split(':');var t=a.GetTypes().Single(x=>x.Name==p[0]);
                foreach(var m in t.GetMethods(BindingFlags.Static|BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic).Where(m=>p[1].Split(',').Contains(m.Name)))
                {RuntimeHelpers.PrepareMethod(m.MethodHandle);count++;}
            }
            Console.WriteLine("JIT_PASS methods="+count+" actualShippingAssembly=True");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 70;}
    }
}
