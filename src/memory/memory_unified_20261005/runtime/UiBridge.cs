using System;
using System.Collections.Generic;
using UnityEngine;

namespace VaM.Memory
{
    // The UI is optional. BCL delegates avoid a hard assembly dependency in either direction.
    public static class UiBridge
    {
        private static object owner;
        private static Func<bool> sceneActive, vrActivity;
        private static Action reportPd;
        public static void Attach(object token, Func<bool> scene, Func<bool> input, Action report)
        { owner = token; sceneActive = scene; vrActivity = input; reportPd = report; }
        public static void Detach(object token)
        {
            if (!ReferenceEquals(owner, token)) return;
            owner = null; sceneActive = null; vrActivity = null; reportPd = null;
            Quest3TriggerUI.TextureCacheEstimate.Probe = null;
        }
        internal static bool SceneActive { get { return sceneActive != null && sceneActive(); } }
        internal static bool VrActivity { get { return vrActivity != null && vrActivity(); } }
        internal static void ReportPd() { if (reportPd != null) reportPd(); }
    }
}

namespace Quest3TriggerUI
{
    internal static class Quest3TriggerUIPlugin
    {
        internal static BepInEx.Logging.ManualLogSource Log;
        internal static VaM.Memory.MemoryPlugin Instance;
    }
    internal static class SceneLoadAccelerator
    {
        internal static bool SceneLoadActive { get { return VaM.Memory.UiBridge.SceneActive; } }
    }
    internal static class UiAssistHudLink
    {
        internal static void ReportPdMemory() { VaM.Memory.UiBridge.ReportPd(); }
    }
    internal static class GenBridge
    {
        // No hot generations in the cold service; only the completed UUA token uses this map.
        private static readonly Dictionary<string, object> store = new Dictionary<string, object>();
        internal static void Publish(string key, object value) { lock (store) store[key] = value; }
        internal static object Take(string key) { lock (store) { object value; store.TryGetValue(key, out value); return value; } }
    }
    internal static class OpenVrInputBridge
    {
        internal static bool TryGetInput(out float trigger, out float rightGrip, out float leftGrip, out float button, out Vector2 right)
        { trigger = VaM.Memory.UiBridge.VrActivity ? 1f : 0f; rightGrip = leftGrip = button = 0f; right = Vector2.zero; return true; }
        internal static bool TryGetSticks(out Vector2 right, out Vector2 left)
        { right = left = Vector2.zero; return false; }
    }
}
