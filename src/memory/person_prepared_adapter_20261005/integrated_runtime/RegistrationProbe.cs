// Capture only registrations removed by the audited SC.RemoveAtom body; compare object identity.
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace VaM.PersonPrepared.Runtime
{
    internal sealed class RegistrationProbe
    {
        private sealed class MapEntry { internal IDictionary Table; internal string Key; internal object Value; }
        private sealed class ListEntry { internal IList Table; internal object Value; }
        private readonly SuperController controller;
        private readonly Atom atom;
        private readonly string uid;
        private readonly List<MapEntry> maps = new List<MapEntry>();
        private readonly List<ListEntry> lists = new List<ListEntry>();
        private static readonly string[] SourceMaps = { "freeControllers", "forceProducers", "rhythmControllers", "audioSourceControls", "grabPoints", "forceReceivers", "linkableRigidbodies", "motionAnimationControls", "playerNavColliders" };
        private static readonly string[] TargetMaps = { "fcMap", "fpMap", "rcMap", "ascMap", "gpMap", "frMap", "rbMap", "macMap", "pncMap" };
        private static readonly string[] SourceLists = { "freeControllers", "animationPatterns", "animationSteps", "animators", "canvases" };
        private static readonly string[] TargetLists = { "allControllers", "allAnimationPatterns", "allAnimationSteps", "allAnimators", "allCanvases" };
        internal RegistrationProbe(SuperController sc, Atom instance)
        {
            controller = sc; atom = instance; uid = instance.uid;
            for (int i = 0; i < SourceMaps.Length; i++)
            {
                IDictionary table = (IDictionary)typeof(SuperController).GetField(TargetMaps[i], OriginalBackend.All).GetValue(sc);
                foreach (UnityEngine.Object item in Source(instance, SourceMaps[i]))
                    maps.Add(new MapEntry { Table = table, Key = uid + ":" + item.name, Value = item });
            }
            for (int i = 0; i < SourceLists.Length; i++)
            {
                IList table = (IList)typeof(SuperController).GetField(TargetLists[i], OriginalBackend.All).GetValue(sc);
                foreach (object item in Source(instance, SourceLists[i])) lists.Add(new ListEntry { Table = table, Value = item });
            }
        }
        private static IEnumerable Source(Atom atom, string property)
        { return (IEnumerable)typeof(Atom).GetProperty(property).GetValue(atom, null); }
        internal bool Cleared()
        {
            // Scene reload destroys the old SC. The receipt drops this last private old-SC root on return.
            if (controller == null) return true;
            var atoms = (Dictionary<string, Atom>)typeof(SuperController).GetField("atoms", OriginalBackend.All).GetValue(controller);
            Atom found; if (atoms.TryGetValue(uid, out found) && ReferenceEquals(found, atom)) return false;
            foreach (Atom item in (List<Atom>)typeof(SuperController).GetField("atomsList", OriginalBackend.All).GetValue(controller))
                if (ReferenceEquals(item, atom)) return false;
            foreach (MapEntry entry in maps) if (ReferenceEquals(entry.Table[entry.Key], entry.Value)) return false;
            foreach (ListEntry entry in lists) foreach (object value in entry.Table) if (ReferenceEquals(value, entry.Value)) return false;
            return true;
        }
        internal static void Validate()
        {
            for (int i = 0; i < SourceMaps.Length; i++) Check(SourceMaps[i], TargetMaps[i], typeof(IDictionary));
            for (int i = 0; i < SourceLists.Length; i++) Check(SourceLists[i], TargetLists[i], typeof(IList));
            CheckField("atoms", typeof(Dictionary<string, Atom>)); CheckField("atomsList", typeof(List<Atom>));
        }
        private static void Check(string source, string target, Type container)
        {
            PropertyInfo property = typeof(Atom).GetProperty(source);
            if (property == null || !typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) throw new MissingMemberException(source);
            CheckField(target, container);
        }
        private static void CheckField(string name, Type type)
        { FieldInfo field = typeof(SuperController).GetField(name, OriginalBackend.All); if (field == null || !type.IsAssignableFrom(field.FieldType)) throw new MissingFieldException(name); }
    }
}
