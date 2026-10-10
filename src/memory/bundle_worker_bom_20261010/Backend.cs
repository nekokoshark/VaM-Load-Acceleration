using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using SimpleJSON;

namespace VamBundleDiskCache
{
    internal sealed class CacheJob
    {
        internal string Package, Entry, Path, Error;
        internal long Size;
        internal bool Hit, Converted;
        internal volatile bool Complete;
    }

    internal static class Backend
    {
        private static string python, script, cache;
        private static readonly object Gate = new object();
        private static readonly Queue<CacheJob> Queue = new Queue<CacheJob>();
        private static readonly AutoResetEvent Wake = new AutoResetEvent(false);
        private static Thread worker;
        private static Process child;
        private static volatile bool stopping;
        internal static bool Available { get { return !stopping && python != null; } }

        internal static void Configure(string executable, string tool, string directory)
        {
            if (worker != null) throw new InvalidOperationException("cache worker already started");
            if (!File.Exists(executable) || !File.Exists(tool)) throw new FileNotFoundException("cache runtime/tool missing");
            python = System.IO.Path.GetFullPath(executable);
            script = System.IO.Path.GetFullPath(tool);
            cache = System.IO.Path.GetFullPath(directory).TrimEnd(System.IO.Path.DirectorySeparatorChar);
            Directory.CreateDirectory(cache);
        }

        internal static CacheJob Enqueue(string package, string entry, long size)
        {
            var job = new CacheJob { Package = package, Entry = entry, Size = size };
            lock (Gate)
            {
                if (!Available) { job.Error = "cache worker stopped/unconfigured"; job.Complete = true; return job; }
                // Package.Path is game-cwd-relative; the converter runs beside its script.
                // Bind before queueing so later cwd changes cannot retarget this request.
                try { job.Package = System.IO.Path.GetFullPath(package); }
                catch (Exception error)
                { job.Error = error.GetType().Name + ": " + error.Message; job.Complete = true; return job; }
                Queue.Enqueue(job);
                if (worker == null)
                {
                    worker = new Thread(Work) { IsBackground = true, Name = "VaM immutable bundle cache" };
                    worker.Start();
                }
                Wake.Set();
            }
            return job;
        }

        // Windows argv quoting; no shell, interpolation, or command execution from paths.
        internal static string Quote(string argument)
        {
            var result = new StringBuilder("\"");
            int slashes = 0;
            foreach (char value in argument)
            {
                if (value == '\\') { slashes++; continue; }
                result.Append('\\', value == '"' ? slashes * 2 + 1 : slashes);
                slashes = 0;
                result.Append(value);
            }
            result.Append('\\', slashes * 2).Append('"');
            return result.ToString();
        }

        private static void Append(StringBuilder buffer, string line)
        {
            if (line == null) return;
            lock (buffer)
            {
                if (buffer.Length < 256 * 1024) buffer.AppendLine(line.Substring(0, Math.Min(line.Length, 256 * 1024 - buffer.Length)));
            }
        }

        private static readonly StringBuilder faults = new StringBuilder();
        private static int childCalls;
        internal static long WorkerStarts, ReceiptHits, ExtractedBytes;
        private static void EndChild(bool abort = false)
        {
            Process process;
            lock (Gate) { process = child; child = null; }
            if (process == null) return;
            try
            {
                if (abort && !process.HasExited) process.Kill();
                try { process.StandardInput.Close(); } catch (IOException) { }
                if (!process.WaitForExit(5000)) { process.Kill(); process.WaitForExit(5000); }
                if (process.HasExited) process.WaitForExit();
            }
            finally { process.Dispose(); childCalls = 0; }
        }
        private static void StartChild()
        {
            var info = new ProcessStartInfo(python, "-B " + Quote(script) + " serve") {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(script)
            };
            info.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
            var process = new Process { StartInfo = info };
            lock (faults) faults.Length = 0;
            process.ErrorDataReceived += (sender, args) => Append(faults, args.Data);
            lock (Gate)
            {
                if (stopping) { process.Dispose(); throw new OperationCanceledException("application exit"); }
                try { if (!process.Start()) throw new IOException("cache child did not start"); }
                catch { process.Dispose(); throw; }
                child = process; WorkerStarts++; childCalls = 0;
            }
            process.BeginErrorReadLine();
        }
        private static string ReadReply(Stream stream)
        {
            byte[] buffer = new byte[8192];
            using (var line = new MemoryStream())
            {
                int count;
                while ((count = stream.Read(buffer, 0, buffer.Length)) != 0)
                {
                    int end = Array.IndexOf(buffer, (byte)'\n', 0, count);
                    int length = end < 0 ? count : end;
                    if (line.Length + length > 256 * 1024) throw new IOException("cache worker reply bound");
                    line.Write(buffer, 0, length);
                    if (end >= 0)
                    {
                        if (end + 1 != count) throw new IOException("cache worker extra reply");
                        return Encoding.UTF8.GetString(line.ToArray());
                    }
                }
                return line.Length == 0 ? null : Encoding.UTF8.GetString(line.ToArray());
            }
        }
        private static void Execute(CacheJob job)
        {
            try
            {
                if (child == null || child.HasExited) { EndChild(); StartChild(); }
                var request = new JSONClass();
                request["package"] = job.Package; request["entry"] = job.Entry;
                request["size"] = job.Size.ToString(CultureInfo.InvariantCulture); request["cache"] = cache;
                child.StandardInput.WriteLine(request.ToString()); child.StandardInput.Flush();
                string line = ReadReply(child.StandardOutput.BaseStream);
                if (stopping) throw new OperationCanceledException("application exit");
                if (line == null || line.Length > 256 * 1024) throw new IOException("cache worker missing/bounded reply");
                var response = JSON.Parse(line);
                if (!response["ok"].AsBool) throw new IOException("cache worker: " + response["error"].Value);
                var result = response["result"];
                string path = System.IO.Path.GetFullPath(result["path"].Value);
                if (!string.Equals(System.IO.Path.GetDirectoryName(path), cache, StringComparison.OrdinalIgnoreCase)
                    || !path.EndsWith(".assetbundle", StringComparison.OrdinalIgnoreCase) || !File.Exists(path)
                    || result["output"]["compressionCodes"].Count == 0)
                    throw new IOException("invalid cache worker result");
                foreach (JSONNode code in result["output"]["compressionCodes"].Childs)
                    if (code.AsInt != 0 && code.AsInt != 2 && code.AsInt != 3)
                        throw new IOException("cache still contains whole-bundle compression");
                job.Path = path; job.Hit = result["hit"].AsBool; job.Converted = result["converted"].AsBool;
                if (result["compressedReceiptHit"].AsBool) ReceiptHits++;
                long extracted; if (long.TryParse(result["extractedBytes"].Value, out extracted)) ExtractedBytes += extracted;
                childCalls++; child.Refresh();
                long privateBytes;
                if (!long.TryParse(result["workerPrivateBytes"].Value, out privateBytes)) privateBytes = child.PrivateMemorySize64;
                if (childCalls >= 64 || privateBytes >= 256L * 1024 * 1024) EndChild();
            }
            catch { EndChild(true); throw; }
        }

        private static void Work()
        {
            while (true)
            {
                CacheJob job = null;
                lock (Gate)
                {
                    if (Queue.Count > 0) job = Queue.Dequeue();
                    else if (stopping) { EndChild(); return; }
                }
                if (job == null) { Wake.WaitOne(); continue; }
                try { Execute(job); }
                catch (Exception error) { lock (faults) job.Error = error.GetType().Name + ": " + error.Message + " " + faults.ToString(); }
                finally { job.Complete = true; }
            }
        }

        internal static void Stop()
        {
            lock (Gate)
            {
                stopping = true;
                while (Queue.Count > 0)
                {
                    var job = Queue.Dequeue();
                    job.Error = "application exit before cache preparation";
                    job.Complete = true;
                }
                if (child != null)
                    try { if (!child.HasExited) child.Kill(); }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception) { }
                Wake.Set();
            }
            if (worker != null && !worker.Join(5000)) throw new TimeoutException("cache child did not exit");
        }
    }
}
