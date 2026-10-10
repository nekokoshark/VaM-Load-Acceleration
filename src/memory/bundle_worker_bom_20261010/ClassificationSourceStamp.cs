using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using HarmonyLib;
using MVR.FileManagement;
using ICSharpCode.SharpZipLib.Zip;

namespace VaM.Memory {
    // Only scalar identities escape this observer. The existing ZIP stream lock is retained.
    internal sealed class ClassificationSourceStamp {
        internal string NamespaceKey, RowKey, Signature;
        private static readonly FieldInfo Zip = AccessTools.Field(typeof(VarPackage), "_zipFile");
        private static readonly FieldInfo Disposed = AccessTools.Field(typeof(ZipFile), "isDisposed_");
        private static readonly FieldInfo StreamField = AccessTools.Field(typeof(ZipFile), "baseStream_");
        private static readonly MethodInfo Locate = AccessTools.Method(typeof(ZipFile), "LocateEntry");
        private static readonly byte[] Buffer = new byte[65536];
        internal static string Hash(byte[] bytes) {
            using(var hash=SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(bytes)).Replace("-", "");
        }
        internal static string RegionKey(HashSet<int> vertices) {
            int[] values=new int[vertices.Count];vertices.CopyTo(values);Array.Sort(values);
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream)) {
                writer.Write(values.Length);foreach(int value in values)writer.Write(value);
                return Hash(stream.ToArray());
            }
        }
        internal static ClassificationSourceStamp Capture(VarFileEntry entry,string algorithm,string region) {
            if(entry==null || entry.GetType()!=typeof(VarFileEntry) || entry.Simulated || entry.Package==null)return null;
            VarPackage package=entry.Package;
            if(package.GetType()!=typeof(VarPackage) || package.forceRefresh || package.invalid || package.IsSimulated ||
                string.IsNullOrEmpty(package.FullPath) || string.IsNullOrEmpty(package.Uid) ||
                string.IsNullOrEmpty(entry.InternalPath) || string.IsNullOrEmpty(entry.InternalSlashPath) ||
                entry.InternalPath.Length>4096 || entry.InternalSlashPath.Length>4096)return null;
            var zip=Zip.GetValue(package) as ZipFile; // Do not create a new lazy ZIP instance on the cold path.
            if(zip==null || (bool)Disposed.GetValue(zip) || zip.IsUpdating)return null;
            var raw=StreamField.GetValue(zip) as FileStream;
            string path=Path.GetFullPath(package.FullPath);
            if(raw==null || !string.Equals(Path.GetFullPath(raw.Name),path,StringComparison.OrdinalIgnoreCase))return null;
            var info=new FileInfo(path);
            if(!info.Exists || info.Length!=package.Size || info.LastWriteTime.Ticks!=package.LastWriteTime.Ticks)return null;
            ZipEntry item=zip.GetEntry(entry.InternalSlashPath);
            if(item==null || item.GetType()!=typeof(ZipEntry) || item.Size!=entry.Size || item.Size<4 ||
                item.Size>int.MaxValue || item.DateTime.Ticks!=entry.LastWriteTime.Ticks || item.CompressedSize<0 ||
                (item.Flags&1)!=0 || (item.CompressionMethod!=CompressionMethod.Stored && item.CompressionMethod!=CompressionMethod.Deflated))return null;
            string digest;
            lock(raw) {
                long previous=raw.Position;
                try {
                    long offset=(long)Locate.Invoke(zip,new object[]{item});
                    if(offset<0 || item.CompressedSize>raw.Length-offset)return null;
                    raw.Position=offset;
                    digest=WindowsReceiptDigest.Compute(raw,item.CompressedSize,Buffer);
                } finally {raw.Position=previous;}
            }
            var after=new FileInfo(path);
            if(!after.Exists || after.Length!=info.Length || after.LastWriteTimeUtc.Ticks!=info.LastWriteTimeUtc.Ticks)return null;
            string ns;
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8)) {
                writer.Write(algorithm);writer.Write(region);writer.Write(path);writer.Write(package.Uid);
                writer.Write(info.Length);writer.Write(info.LastWriteTimeUtc.Ticks);
                writer.Write(package.Size);writer.Write(package.LastWriteTime.Ticks);
                ns=Hash(stream.ToArray());
            }
            string signature;
            using(var stream=new MemoryStream())using(var writer=new BinaryWriter(stream,Encoding.UTF8)) {
                writer.Write(item.Name);writer.Write(item.Size);writer.Write(item.CompressedSize);writer.Write(item.Crc);
                writer.Write(item.DateTime.Ticks);writer.Write(item.Flags);writer.Write((int)item.CompressionMethod);writer.Write(digest);
                signature=Hash(stream.ToArray());
            }
            return new ClassificationSourceStamp {NamespaceKey=ns,RowKey=entry.InternalPath+"\0"+entry.InternalSlashPath,Signature=signature};
        }
        internal bool Equal(ClassificationSourceStamp other) {
            return other!=null && NamespaceKey==other.NamespaceKey && RowKey==other.RowKey && Signature==other.Signature;
        }
    }
    // Windows' existing SHA-256 implementation; no additional deployed library or payload buffer.
    internal static class WindowsReceiptDigest {
        private static IntPtr provider;
        private static bool attempted;
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)]
        private static extern int BCryptOpenAlgorithmProvider(out IntPtr handle,string algorithm,string implementation,int flags);
        [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)]
        private static extern int BCryptGetProperty(IntPtr handle,string name,byte[] data,int size,out int copied,int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCreateHash(IntPtr algorithm,out IntPtr hash,IntPtr buffer,int size,IntPtr secret,int secretSize,int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptHashData(IntPtr hash,byte[] bytes,int size,int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptFinishHash(IntPtr hash,byte[] bytes,int size,int flags);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptDestroyHash(IntPtr hash);
        [DllImport("bcrypt.dll")]
        private static extern int BCryptCloseAlgorithmProvider(IntPtr handle,int flags);
        private static void Check(int status) {if(status!=0)throw new Win32Exception(status,"receipt SHA-256");}
        private static bool Initialize() {
            if(attempted)return provider!=IntPtr.Zero;
            attempted=true;if(Environment.OSVersion.Platform!=PlatformID.Win32NT)return false;
            try {if(BCryptOpenAlgorithmProvider(out provider,"SHA256",null,0)!=0)provider=IntPtr.Zero;}
            catch(DllNotFoundException){provider=IntPtr.Zero;}
            catch(EntryPointNotFoundException){provider=IntPtr.Zero;}
            return provider!=IntPtr.Zero;
        }
        internal static string Compute(Stream source,long count,byte[] buffer) {
            if(!Initialize()) {
                using(var hash=SHA256.Create()) {
                    while(count>0){int n=source.Read(buffer,0,(int)Math.Min((long)buffer.Length,count));if(n==0)throw new EndOfStreamException();hash.TransformBlock(buffer,0,n,buffer,0);count-=n;}
                    hash.TransformFinalBlock(buffer,0,0);return BitConverter.ToString(hash.Hash).Replace("-", "");
                }
            }
            byte[] property=new byte[4];int copied;Check(BCryptGetProperty(provider,"ObjectLength",property,4,out copied,0));
            int length=BitConverter.ToInt32(property,0);if(copied!=4 || length<=0 || length>65536)throw new InvalidDataException("hash object bound");
            IntPtr storage=Marshal.AllocHGlobal(length),hashHandle=IntPtr.Zero;
            try {
                Check(BCryptCreateHash(provider,out hashHandle,storage,length,IntPtr.Zero,0,0));
                while(count>0){int n=source.Read(buffer,0,(int)Math.Min((long)buffer.Length,count));if(n==0)throw new EndOfStreamException();Check(BCryptHashData(hashHandle,buffer,n,0));count-=n;}
                byte[] digest=new byte[32];Check(BCryptFinishHash(hashHandle,digest,32,0));
                return BitConverter.ToString(digest).Replace("-", "");
            } finally {if(hashHandle!=IntPtr.Zero)BCryptDestroyHash(hashHandle);Marshal.FreeHGlobal(storage);}
        }
        internal static void Shutdown() {if(provider!=IntPtr.Zero)BCryptCloseAlgorithmProvider(provider,0);provider=IntPtr.Zero;attempted=false;}
    }

}
