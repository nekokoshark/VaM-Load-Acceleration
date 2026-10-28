using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using MVR.FileManagement;
using Quest3TriggerUI;

namespace VaM.Memory {
    // Compare the complete byte path, not its owner string alone.
    // Versioned UI namespaces may differ; every opcode, dependency, local and EH stays checked.
    internal static class ClassificationStreamCompatibility {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly;
        private static readonly ConstructorInfo Constructor = AccessTools.Constructor(typeof(VarFileEntryStream),new[]{typeof(VarFileEntry)});
        private static readonly Type Reference = typeof(MemoryPlugin).Assembly.GetType("Quest3TriggerUI.StreamReadAccel",true);
        private static readonly Type ReferenceBuffer = typeof(MemoryPlugin).Assembly.GetType("Quest3TriggerUI.EntryStreamBuffer",true);
        private static readonly WeakObservationIndex<Audit> Audits = new WeakObservationIndex<Audit>();
        private static string referenceBody, referenceBufferBody;
        private sealed class Audit { internal bool Matches; }

        private static List<MethodBase> Members(Type type) {
            var result = new List<MethodBase>();
            result.AddRange(type.GetMethods(Declared));
            result.AddRange(type.GetConstructors(Declared));
            if(type.TypeInitializer!=null && !result.Contains(type.TypeInitializer))result.Add(type.TypeInitializer);
            return result;
        }
        private static string Body(Type type) {
            var rows=new List<string>();
            rows.Add("base="+type.BaseType.AssemblyQualifiedName+";sealed="+type.IsSealed);
            foreach(FieldInfo field in type.GetFields(Declared)) {
                string fieldType=field.FieldType.AssemblyQualifiedName;
                rows.Add("field="+field.Name+":"+fieldType+":"+field.IsStatic+":"+field.IsInitOnly);
            }
            foreach(MethodBase member in Members(type)) {
                // UI builds replace registration calls with HarmonyCompat.Patch.
                // Registration is checked by the actual postfix and bound setter below;
                // it is not a byte-transforming call reachable from the postfix.
                if(type.Name=="StreamReadAccel" && member.Name=="Install")continue;
                rows.Add("method="+member.Name+":"+member.IsVirtual+":"+member.IsFinal+":"+StreamWrapperBodyAudit.Fingerprint(member));
            }
            foreach(Type nested in type.GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
                rows.Add("nested="+nested.Name+":"+Body(nested));
            rows.Sort(StringComparer.Ordinal);
            return string.Join("\n",rows.ToArray());
        }
        private static bool Unpatched(Type type) {
            foreach(MethodBase member in Members(type)) {
                var patches=Harmony.GetPatchInfo(member);
                if(patches!=null && (patches.Prefixes.Count!=0 || patches.Postfixes.Count!=0 ||
                    patches.Transpilers.Count!=0 || patches.Finalizers.Count!=0 || patches.ILManipulators.Count!=0))return false;
            }
            foreach(Type nested in type.GetNestedTypes(BindingFlags.Public|BindingFlags.NonPublic))
                if(!Unpatched(nested))return false;
            return true;
        }
        internal static bool Allows(MethodBase original,Patch patch) {
            if(original!=Constructor || patch.owner!="Quest3TriggerUI.stream-read-accel")return false;
            MethodInfo method=patch.PatchMethod;
            if(method==null || !method.IsStatic || method.Name!="AfterEntryStreamCreated" || method.ReturnType!=typeof(void))return false;
            ParameterInfo[] parameters=method.GetParameters();
            if(parameters.Length!=1 || parameters[0].ParameterType!=typeof(object) || parameters[0].Name!="__instance")return false;
            Type wrapper=method.DeclaringType;
            if(wrapper==null || wrapper.Name!="StreamReadAccel" || wrapper.Namespace==null ||
                !(wrapper.Namespace=="Quest3TriggerUI" || wrapper.Namespace.StartsWith("Quest3TriggerUI.v",StringComparison.Ordinal)))return false;
            Type buffer=wrapper.Assembly.GetType(wrapper.Namespace+".EntryStreamBuffer",false);
            if(buffer==null)return false;
            try {
                FieldInfo setter=wrapper.GetField("_setter",Declared);
                if(setter==null || setter.GetValue(null) as MethodInfo != AccessTools.PropertySetter(typeof(FileEntryStream),"Stream"))return false;
                Audit audit;
                if(!Audits.TryGetValue(wrapper,out audit)) {
                    if(Audits.Count>=32)Audits.Clear();
                    if(referenceBody==null)referenceBody=Body(Reference);
                    if(referenceBufferBody==null)referenceBufferBody=Body(ReferenceBuffer);
                    audit=new Audit {Matches=Body(wrapper)==referenceBody && Body(buffer)==referenceBufferBody};
                    Audits[wrapper]=audit;
                    if(ClassificationDiskReceipts.Log!=null)ClassificationDiskReceipts.Log(
                        "[classification-disk] stream-wrapper bodyMatch="+audit.Matches+" namespace="+wrapper.Namespace+" identity=byte-path+bound-setter weakKeys=True");
                }
                // Recheck Harmony changes on helpers even after an immutable IL identity was cached.
                return audit.Matches && Unpatched(wrapper) && Unpatched(buffer);
            } catch(Exception) {return false;}
        }
        internal static void Clear() {Audits.Clear();}
    }
}
