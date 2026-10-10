using System;
using System.IO;
using Quest3TriggerUI;

internal static class Bc7CompatCli
{
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length < 5) return 2;
            int w = int.Parse(args[2]), h = int.Parse(args[3]), mips = int.Parse(args[4]);
            int tw, th, tm; long bytes;
            if (args[0] == "plan" && args.Length == 6)
            {
                if (!Bc7CacheCompatibility.Plan(w,h,mips,long.Parse(args[5]),out tw,out th,out tm,out bytes)) return 3;
                Console.WriteLine(tw + " " + th + " " + tm + " " + bytes); return 0;
            }
            if (args[0] == "metadata" && args.Length == 6)
            {
                File.WriteAllText(args[5], Bc7CacheCompatibility.Metadata(File.ReadAllText(args[1]),w,h),new System.Text.UTF8Encoding(false));
                return 0;
            }
            if (args[0] == "input" && args.Length == 7)
            {
                TextureCacheStreamIO.WriteInput(args[1],args[6],w,h,mips,int.Parse(args[5]),null); return 0;
            }
            if (args[0] == "extract" && args.Length == 6)
            {
                if (!Bc7CacheCompatibility.Plan(w,h,mips,long.MaxValue,out tw,out th,out tm,out bytes) ||
                    tw != w || th != h || tm != mips) return 3;
                TextureCacheStreamIO.ExtractBc7(args[1],args[5],w,h,mips,bytes,null); return 0;
            }
            bool ok;
            if (args[0] == "source" && args.Length == 6)
                ok = Bc7CacheCompatibility.Source(args[1], w, h, mips, int.Parse(args[5]), null);
            else if (args[0] == "decoded" && args.Length == 5)
                ok = Bc7CacheCompatibility.Decoded(args[1], w, h, mips, null);
            else return 2;
            Console.WriteLine(ok ? "BC7_COMPAT_ACCEPT" : "BC7_COMPAT_KEEP_NATIVE");
            return ok ? 0 : 3;
        }
        catch (Exception e) { Console.Error.WriteLine("BC7_COMPAT_ERROR: " + e.Message); return 2; }
    }
}
