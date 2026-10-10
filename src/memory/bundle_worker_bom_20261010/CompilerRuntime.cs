using System;
using System.IO;
using BepInEx;
using VaM.CompilerIsolation;

namespace VaM.Memory
{
    internal static class CompilerRuntime
    {
        internal static void Extract(string resource, string target)
        {
            byte[] bytes;
            using (Stream s = typeof(CompilerRuntime).Assembly.GetManifestResourceStream(resource))
            using (var data = new MemoryStream())
            {
                byte[] buffer = new byte[32768]; int n;
                while ((n = s.Read(buffer, 0, buffer.Length)) != 0) data.Write(buffer, 0, n);
                bytes = data.ToArray();
            }
            if (File.Exists(target))
            {
                byte[] existing = File.ReadAllBytes(target); bool same = existing.Length == bytes.Length;
                for (int i = 0; same && i < bytes.Length; i++) if (existing[i] != bytes[i]) same = false;
                if (same) return;
            }
            File.WriteAllBytes(target, bytes);
        }
        internal static void Install(bool cache, int budgetMiB, Action<string> log)
        {
            string support = SupportFiles.Directory;
            if (support == null) support = SupportFiles.Extract(Path.Combine(Paths.BepInExRootPath, "cache/VaMMemory/support"));
            string directory = Path.Combine(Paths.BepInExRootPath, "cache/VaMMemory/compiler/1.3.0");
            Directory.CreateDirectory(directory);
            string worker = Path.Combine(directory, "VaM.CompilerWorker.exe"), runner = Path.Combine(directory, "worker_runner.py");
            Extract("VaM.Memory.CompilerWorker", worker); Extract("VaM.Memory.CompilerRunner", runner);
            var service = new CompilerService(Path.Combine(support, "python/python.exe"), runner, worker,
                MemoryCachePaths.DirectoryFor("CompilerBytecode"),
                Path.Combine(Paths.GameRootPath, "VaM_Data/Managed"));
            service.Log = log; service.UseCache = cache;
            service.WorkerBudget = Math.Max(128, Math.Min(512, budgetMiB)) * 1024L * 1024L;
            CompilerHooks.Install(service);
            log("[compiler-worker] installed routes=original-McsMarshal sources+files=True cachedBytecode=" + cache + " independentStatics=True workerSoftBudgetMiB=" + (service.WorkerBudget >> 20) + " noGameGC=True");
        }
        internal static void Stop() { CompilerHooks.Remove(); }
    }
}
