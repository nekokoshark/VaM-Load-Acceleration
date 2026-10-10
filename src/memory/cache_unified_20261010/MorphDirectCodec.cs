using System;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;

namespace VaM.Memory {
 public static partial class MorphSnapshotState {
  // One scratch buffer per reader/writer, never one per field or a static row root.
  private sealed class BulkMetadataReader:BinaryReader {
   internal readonly byte[] Scratch=new byte[8192];
   internal BulkMetadataReader(Stream stream):base(stream,Encoding.UTF8){}
  }
  private sealed class BulkMetadataWriter:BinaryWriter {
   internal readonly byte[] Scratch=new byte[8192];
   internal BulkMetadataWriter(Stream stream):base(stream,Encoding.UTF8){}
  }
  private static Action<BinaryReader,object> decodeRow;
  private static Action<object,DAZMorph> importCopy;
  public static long BulkTextReads,DirectRows,DirectImports;
  private static void PrepareDirectRows() {
   var m=new DynamicMethod("MorphPureRowDecode",typeof(void),new[]{typeof(BinaryReader),typeof(object)},typeof(MorphSnapshotState),true);
   var il=m.GetILGenerator();
   foreach(var field in SnapshotFields) {
    il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Castclass,SnapshotType);il.Emit(OpCodes.Ldarg_0);
    MethodInfo read;
    if(field.FieldType==typeof(string))read=typeof(MorphSnapshotState).GetMethod("ReadText",BindingFlags.NonPublic|BindingFlags.Static);
    else if(field.FieldType==typeof(float))read=typeof(MorphSnapshotState).GetMethod("ReadFloat",BindingFlags.NonPublic|BindingFlags.Static);
    else if(field.FieldType==typeof(bool))read=typeof(BinaryReader).GetMethod("ReadBoolean");
    else if(field.FieldType==typeof(int))read=typeof(BinaryReader).GetMethod("ReadInt32");
    else if(field.FieldType==typeof(DAZMorphFormula[]))read=typeof(MorphSnapshotState).GetMethod("ReadPureFormulas",BindingFlags.NonPublic|BindingFlags.Static);
    else throw new InvalidDataException("unverified pure field "+field.Name);
    if(read==null || read.ReturnType!=field.FieldType)throw new MissingMethodException("pure field reader "+field.Name);
    il.Emit(read.IsStatic?OpCodes.Call:OpCodes.Callvirt,read);il.Emit(OpCodes.Stfld,field);
   }
   il.Emit(OpCodes.Ret);decodeRow=(Action<BinaryReader,object>)m.CreateDelegate(typeof(Action<BinaryReader,object>));
  }
  private static DAZMorphFormula[] ReadPureFormulas(BinaryReader reader) {
   int size=reader.ReadInt32();if(size<-1 || size>65536)throw new InvalidDataException("metadata formulas");
   DAZMorphFormula[] formulas=size==-1?null:new DAZMorphFormula[size];
   if(formulas!=null)for(int n=0;n<size;n++)formulas[n]=new DAZMorphFormula {targetType=(DAZMorphFormulaTargetType)reader.ReadInt32(),target=ReadText(reader),multiplier=ReadFloat(reader)};
   return formulas;
  }
  private static void PrepareDirectImport() {
   var m=new DynamicMethod("MorphPureImportCopy",typeof(void),new[]{typeof(object),typeof(DAZMorph)},typeof(MorphSnapshotState),true);
   var il=m.GetILGenerator();
   for(int i=0;i<ImportMorphFields.Length;i++) {
    var source=ImportSnapshotFields[i];var target=ImportMorphFields[i];
    if(source.FieldType!=target.FieldType)throw new InvalidDataException("verified import type "+target.Name);
    il.Emit(OpCodes.Ldarg_1);il.Emit(OpCodes.Ldarg_0);il.Emit(OpCodes.Castclass,SnapshotType);il.Emit(OpCodes.Ldfld,source);
    if(target.FieldType==typeof(DAZMorphFormula[])) {
     il.Emit(OpCodes.Ldc_I4_1);
     il.Emit(OpCodes.Call,typeof(MorphSnapshotState).GetMethod("CopyImportFormulas",BindingFlags.NonPublic|BindingFlags.Static));
    }
    il.Emit(OpCodes.Stfld,target);
   }
   il.Emit(OpCodes.Ret);importCopy=(Action<object,DAZMorph>)m.CreateDelegate(typeof(Action<object,DAZMorph>));
  }
 }
}
