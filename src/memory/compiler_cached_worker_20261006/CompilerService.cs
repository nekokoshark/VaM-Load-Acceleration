using System;
using System.Collections.Generic;
using System.CodeDom.Compiler;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Mono.CSharp;

namespace VaM.CompilerIsolation
{
    public sealed class CompilerService : IDisposable
    {
        public Action<string> Log;
        public long Requests, Hits, Workers;
        public long WorkerBudget = 256L * 1024 * 1024;
        public int WorkerCalls = 256;
        public bool UseCache = true;
        readonly string python, runner, executable, cache, managed;
        readonly object gate = new object();
        readonly Dictionary<string, Stamp> stamps = new Dictionary<string, Stamp>(StringComparer.OrdinalIgnoreCase);
        Process worker;
        BinaryReader reader; BinaryWriter writer;
        string environment;
        int calls;
        bool disposed;
        readonly StringBuilder faults = new StringBuilder();
        sealed class Stamp { public long Length, Ticks; public string Hash; }

        public CompilerService(string python, string runner, string executable, string cache, string managed)
        { this.python = python; this.runner = runner; this.executable = executable; this.cache = cache; this.managed = managed; }
        string FileIdentity(string path)
        {
            var f = new FileInfo(path); Stamp s;
            if (!stamps.TryGetValue(f.FullName, out s) || f.Length != s.Length || f.LastWriteTimeUtc.Ticks != s.Ticks)
            {
                using (var input = File.OpenRead(f.FullName))
                using (var sha = SHA256.Create())
                    s = new Stamp { Length = f.Length, Ticks = f.LastWriteTimeUtc.Ticks, Hash = BitConverter.ToString(sha.ComputeHash(input)) };
                if (stamps.Count >= 256) stamps.Clear();
                stamps[f.FullName] = s;
            }
            return f.FullName + "|" + s.Hash;
        }
        string ResolveReference(string name)
        {
            foreach (string folder in new[] { System.IO.Directory.GetCurrentDirectory(), managed })
            {
                string path = Path.IsPathRooted(name) ? name : Path.Combine(folder, name);
                if (File.Exists(path)) return path;
                if (!name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path + ".dll")) return path + ".dll";
            }
            return null;
        }
        public bool CanOffload(CompilerParameters parameters)
        {
            foreach (string reference in parameters.ReferencedAssemblies)
                if (ResolveReference(reference) == null) return false;
            return true;
        }
        void Fingerprint(CompileRequest r)
        {
            // Include the emitter, default runtime references, and all explicit references.
            foreach (string name in new[] { "Assembly-CSharp.dll", "mcs.dll", "mscorlib.dll", "System.dll", "System.Core.dll", "System.Xml.dll" })
                r.ReferenceIdentities.Add(FileIdentity(Path.Combine(managed, name)));
            foreach (string name in r.References)
            {
                string path = ResolveReference(name);
                if (path == null) { r.Cacheable = false; r.ReferenceIdentities.Add("unresolved:" + name); }
                else r.ReferenceIdentities.Add(FileIdentity(path));
            }
            r.ReferenceIdentities.Add("cache-schema=2;cecil=" + FileIdentity(Path.Combine(managed, "Mono.Cecil.dll")));
        }
        public CompileReply Compile(CompilerSettings settings)
        {
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("CompilerService");
                var request = CompileRequest.Capture(settings);
                request.Cacheable &= UseCache; Fingerprint(request); request.MakeKey();
                string nextEnvironment = string.Join("\n", request.ReferenceIdentities.ToArray());
                if (worker != null && environment != nextEnvironment) Stop();
                if (worker == null) { environment = nextEnvironment; Start(); }
                Process current = worker;
                using (var timeout = new Timer(delegate {
                    try { if (!current.HasExited) current.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
                }, null, 120000, Timeout.Infinite))
                {
                    try
                    {
                        request.Write(writer);
                        CompileReply result = CompileReply.Read(reader);
                        Requests++; if (result.Hit) Hits++;
                        calls++;
                        // Retire an exhausted worker before any logging callback can reenter.
                        if (calls >= WorkerCalls || result.PrivateBytes >= WorkerBudget) Stop();
                        if (Log != null && (Requests <= 3 || Requests % 32 == 0)) Log("[compiler-worker] requests=" + Requests + " hits=" + Hits + " workers=" + Workers + " privateMiB=" + (result.PrivateBytes >> 20));
                        return result;
                    }
                    catch (IOException e)
                    {
                        Stop();
                        var result = new CompileReply();
                        result.Errors.Add(new CompilerError("", -1, -1, "0", "Compiler worker terminated: " + e.Message));
                        return result;
                    }
                }
            }
        }
        static string Quote(string value)
        { if (value.IndexOf('"') >= 0 || value.EndsWith("\\")) throw new ArgumentException("Worker argument"); return "\"" + value + "\""; }
        void Start()
        {
            Directory.CreateDirectory(cache);
            var start = new ProcessStartInfo(python, Quote(runner) + " " + Quote(executable) + " " + Quote(cache) + " 268435456 " + WorkerBudget + " " + WorkerCalls);
            start.UseShellExecute = false; start.CreateNoWindow = true;
            start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
            start.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            start.EnvironmentVariables["VAM_COMPILER_GAME"] = new DirectoryInfo(managed).Parent.Parent.FullName;
            lock (faults) faults.Length = 0;
            worker = Process.Start(start); Workers++; calls = 0;
            worker.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e) { lock (faults) { if (e.Data != null && faults.Length < 4096) faults.AppendLine(e.Data); } };
            worker.BeginErrorReadLine();
            reader = new BinaryReader(worker.StandardOutput.BaseStream); writer = new BinaryWriter(worker.StandardInput.BaseStream);
            Process current = worker;
            using (var timeout = new Timer(delegate { try { if (!current.HasExited) current.Kill(); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } }, null, 15000, Timeout.Infinite))
            {
                try { if (reader.ReadInt32() != CompileRequest.Magic) throw new InvalidDataException("Compiler worker handshake"); }
                catch { Stop(); throw; }
            }
            if (Log != null) Log("WORKER_PID " + worker.Id);
        }
        void Stop()
        {
            if (worker == null) return;
            try { writer.Close(); } catch (IOException) { }
            if (!worker.WaitForExit(10000)) { worker.Kill(); worker.WaitForExit(); }
            reader.Close();
            string message = null;
            if (worker.ExitCode != 0) { lock (faults) message = "[compiler-worker] exit=" + worker.ExitCode + " " + faults.ToString(); }
            worker.Dispose(); worker = null; reader = null; writer = null; environment = null;
            if (message != null && Log != null) Log(message);
        }
        public void EndWorkerBatch() { lock (gate) Stop(); }
        public void Dispose() { lock (gate) { disposed = true; Stop(); stamps.Clear(); } }
    }
}
