using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;

namespace VaM.Memory {
 // Windows system CNG, not an additional shipped DLL. Both handles and the
 // bounded IO buffer belong to this call and are released before it returns.
 public static class CacheSha256 {
  [DllImport("bcrypt.dll",CharSet=CharSet.Unicode)]private static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm,string name,string implementation,int flags);
  [DllImport("bcrypt.dll")]private static extern int BCryptCreateHash(IntPtr algorithm,out IntPtr hash,IntPtr buffer,int length,IntPtr secret,int secretLength,int flags);
  [DllImport("bcrypt.dll")]private static extern int BCryptHashData(IntPtr hash,IntPtr data,int length,int flags);
  [DllImport("bcrypt.dll")]private static extern int BCryptFinishHash(IntPtr hash,byte[] output,int length,int flags);
  [DllImport("bcrypt.dll")]private static extern int BCryptDestroyHash(IntPtr hash);
  [DllImport("bcrypt.dll")]private static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm,int flags);
  public static long NativeCalls,FallbackCalls,LiveHandles;
  private const int BufferSize=65536,BufferSlots=4;
  private static readonly object BufferGate=new object();
  private static readonly byte[][] Buffers=new byte[BufferSlots][];
  private static int bufferCount,bufferGeneration;
  public static long BufferAllocations,BufferReuses,ActiveBuffers;
  public static long RetainedBufferBytes {get{lock(BufferGate)return bufferCount*(long)BufferSize;}}
  private static byte[] RentBuffer(out int generation) {
   lock(BufferGate) {
    generation=bufferGeneration;ActiveBuffers++;
    if(bufferCount!=0){byte[] value=Buffers[--bufferCount];Buffers[bufferCount]=null;BufferReuses++;return value;}
    BufferAllocations++;
   }
   try{return new byte[BufferSize];}
   catch{lock(BufferGate)ActiveBuffers--;throw;}
  }
  private static void ReturnBuffer(byte[] value,int generation) {
   // A stream may write bytes and then throw. Clear the whole buffer before
   // returning it; reentrant/concurrent readers always rent a different buffer.
   Array.Clear(value,0,value.Length);
   lock(BufferGate){ActiveBuffers--;if(generation==bufferGeneration && bufferCount<BufferSlots)Buffers[bufferCount++]=value;}
  }
  public static void ClearBuffers(){lock(BufferGate){bufferGeneration++;Array.Clear(Buffers,0,Buffers.Length);bufferCount=0;}}
  private static bool Open(out IntPtr algorithm,out IntPtr hash) {
   algorithm=hash=IntPtr.Zero;
   if(BCryptOpenAlgorithmProvider(out algorithm,"SHA256",null,0)!=0)return false;
   Interlocked.Increment(ref LiveHandles);
   if(BCryptCreateHash(algorithm,out hash,IntPtr.Zero,0,IntPtr.Zero,0,0)!=0)return false;
   Interlocked.Increment(ref LiveHandles);return true;
  }
  private static void Close(IntPtr algorithm,IntPtr hash) {
   if(hash!=IntPtr.Zero){BCryptDestroyHash(hash);Interlocked.Decrement(ref LiveHandles);}
   if(algorithm!=IntPtr.Zero){BCryptCloseAlgorithmProvider(algorithm,0);Interlocked.Decrement(ref LiveHandles);}
  }
  private static unsafe bool Feed(IntPtr hash,byte[] bytes,int offset,int count) {
   if(count==0)return true;fixed(byte* p=bytes)return BCryptHashData(hash,(IntPtr)(p+offset),count,0)==0;
  }
  public static byte[] Hash(byte[] bytes,int offset,int count) {
   if(bytes==null)throw new ArgumentNullException("bytes");if(offset<0 || count<0 || offset>bytes.Length-count)throw new ArgumentOutOfRangeException();
   IntPtr algorithm=IntPtr.Zero,hash=IntPtr.Zero;byte[] output=new byte[32];
   try {if(Open(out algorithm,out hash) && Feed(hash,bytes,offset,count) && BCryptFinishHash(hash,output,32,0)==0){Interlocked.Increment(ref NativeCalls);return output;}}
   catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
   finally{Close(algorithm,hash);}
   Interlocked.Increment(ref FallbackCalls);using(var sha=SHA256.Create())return sha.ComputeHash(bytes,offset,count);
  }
  public static byte[] Hash(Stream stream) {
   if(stream==null)throw new ArgumentNullException("stream");if(!stream.CanSeek)throw new ArgumentException("cache source stream must seek");
   long position=stream.Position;IntPtr algorithm=IntPtr.Zero,hash=IntPtr.Zero;byte[] output=new byte[32];bool success=false;
   byte[] buffer=null;int bufferEpoch=0;
   try {
    if(Open(out algorithm,out hash)) {
     buffer=RentBuffer(out bufferEpoch);int read;success=true;
     while((read=stream.Read(buffer,0,buffer.Length))>0)if(!Feed(hash,buffer,0,read)){success=false;break;}
     if(success && BCryptFinishHash(hash,output,32,0)==0){Interlocked.Increment(ref NativeCalls);return output;}
    }
   }catch(DllNotFoundException){}catch(EntryPointNotFoundException){}
   finally{Close(algorithm,hash);if(buffer!=null)ReturnBuffer(buffer,bufferEpoch);}
   stream.Position=position;Interlocked.Increment(ref FallbackCalls);using(var sha=SHA256.Create())return sha.ComputeHash(stream);
  }
 }
}
