using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;

// Load private tool dependencies before loading helpers that reference Harmony.
// This assembly deliberately references only the target Mono's base libraries.
public static class CollectorProbe {
    static readonly string Root = Path.GetDirectoryName(typeof(CollectorProbe).Assembly.Location);
    static readonly Dictionary<string, string> Paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    static bool ready;

    static string Hash(string path) {
        using (var stream = File.OpenRead(path))
        using (var hash = SHA256.Create())
            return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    static Assembly Resolve(object sender, ResolveEventArgs args) {
        string path;
        return Paths.TryGetValue(new AssemblyName(args.Name).Name, out path) ? Assembly.LoadFrom(path) : null;
    }

    public static string CheckDependencies() {
        try { return LoadDependencies(); }
        catch (Exception error) {
            Console.Error.WriteLine("WORKER_DEPENDENCY_EXCEPTION " + error);
            throw;
        }
    }

    static string LoadDependencies() {
        if (!ready) {
            foreach (string row in File.ReadAllLines(Path.Combine(Root, "worker-dependencies.txt"))) {
                string[] fields = row.Split('|');
                if (fields.Length != 2 || Path.GetFileName(fields[0]) != fields[0])
                    throw new InvalidDataException("Invalid private dependency row");
                string path = Path.Combine(Root, fields[0]);
                if (Hash(path) != fields[1]) throw new InvalidDataException("WORKER_DEPENDENCY_HASH: " + fields[0]);
                Paths.Add(AssemblyName.GetAssemblyName(path).Name, path);
            }
            AppDomain.CurrentDomain.AssemblyResolve += Resolve;
            // Cecil is loaded first even when another directory has the same assembly identity.
            Assembly cecil = Assembly.LoadFrom(Paths["Mono.Cecil"]);
            foreach (var pair in Paths) {
                Assembly assembly = Assembly.LoadFrom(pair.Value);
                if (!String.Equals(Path.GetFullPath(assembly.Location), Path.GetFullPath(pair.Value), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("WORKER_DEPENDENCY_LOCATION: " + pair.Key + " -> " + assembly.Location);
            }
            if (cecil.GetType("Mono.Cecil.DefaultReflectionImporter", false) == null)
                throw new TypeLoadException("Missing Mono.Cecil.DefaultReflectionImporter in " + cecil.Location);
            ready = true;
        }
        return "WORKER_DEPENDENCIES_PASS files=" + Paths.Count + " cecil=" + Assembly.LoadFrom(Paths["Mono.Cecil"]).GetName().Version + " source=private-worker";
    }

    static string Call(string method) {
        try {
            Console.WriteLine(CheckDependencies());
            string helper = File.ReadAllText(Path.Combine(Root, "worker-helper.txt")).Trim();
            if (Path.GetFileName(helper) != helper) throw new InvalidDataException("Invalid worker helper");
            Assembly assembly = Assembly.LoadFrom(Path.Combine(Root, helper));
            MethodInfo action = assembly.GetType("CollectorProbe", true).GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (action == null) throw new MissingMethodException(helper, method);
            return (string)action.Invoke(null, null);
        } catch (Exception error) {
            Console.Error.WriteLine("WORKER_ACTION_EXCEPTION " + method + " " + error);
            throw;
        }
    }

    public static string InitializePortable() { return Call("InitializePortable"); }
    public static string SourceCaches() { return Call("SourceCaches"); }
    public static string Offline() { return Call("Offline"); }
    public static string Drop() { return Call("Drop"); }
    public static string PortableTrees() { return Call("PortableTrees"); }
    public static string Run() { return Call("Run"); }
}
