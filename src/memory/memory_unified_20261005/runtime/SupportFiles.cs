using System;
using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.GZip;

namespace VaM.Memory
{
    // Inert helper files live outside the plugin scan tree. Only this DLL activates patches.
    public static class SupportFiles
    {
        public static string Directory { get; private set; }
        public static string Extract(string cacheRoot)
        {
            string folder = Path.GetFullPath(Path.Combine(cacheRoot, BuildInfo.SupportVersion));
            System.IO.Directory.CreateDirectory(folder);
            string marker = Path.Combine(folder, "complete.txt");
            if (File.Exists(marker) && File.ReadAllText(marker) == BuildInfo.SupportVersion && Complete(folder))
            { Directory = folder; return folder; }
            using (Stream resource = typeof(SupportFiles).Assembly.GetManifestResourceStream("VaM.Memory.Support"))
            using (GZipInputStream zip = new GZipInputStream(resource))
            using (BinaryReader reader = new BinaryReader(zip, Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                for (int i = 0; i < count; i++)
                {
                    int nameLength = reader.ReadInt32();
                    string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
                    int bytes = reader.ReadInt32();
                    string target = Path.GetFullPath(Path.Combine(folder, name));
                    if (!target.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException("Support entry path");
                    bool write = !File.Exists(marker) || !File.Exists(target) || new FileInfo(target).Length != bytes;
                    Stream output = null;
                    try
                    {
                        if (write)
                        {
                            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target));
                            output = File.Create(target + ".partial");
                        }
                        byte[] buffer = new byte[Math.Min(65536, Math.Max(1, bytes))];
                        for (int left = bytes; left > 0; )
                        {
                            int n = reader.Read(buffer, 0, Math.Min(left, buffer.Length));
                            if (n <= 0) throw new EndOfStreamException();
                            if (output != null) output.Write(buffer, 0, n);
                            left -= n;
                        }
                    }
                    finally { if (output != null) output.Dispose(); }
                    if (write)
                    {
                        if (File.Exists(target)) File.Delete(target);
                        File.Move(target + ".partial", target);
                    }
                }
            }
            File.WriteAllText(marker, BuildInfo.SupportVersion);
            Directory = folder;
            return folder;
        }
        private static bool Complete(string folder)
        {
            using (Stream resource = typeof(SupportFiles).Assembly.GetManifestResourceStream("VaM.Memory.SupportIndex"))
            using (StreamReader reader = new StreamReader(resource, Encoding.UTF8))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    int split = line.LastIndexOf('|');
                    string path = Path.Combine(folder, line.Substring(0, split));
                    if (!File.Exists(path) || new FileInfo(path).Length != long.Parse(line.Substring(split + 1))) return false;
                }
                return true;
            }
        }
    }
}
