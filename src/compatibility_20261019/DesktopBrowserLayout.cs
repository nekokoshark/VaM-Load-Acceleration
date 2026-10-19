using UnityEngine;

namespace Quest3TriggerUI
{
    // Keep the existing world-space ray/input path, but fit desktop dialogs
    // to the actual camera viewport instead of the VR HUD's physical size.
    internal static class DesktopBrowserLayout
    {
        private static Transform editor;
        private static Vector3 editorScale, editorPosition;
        private static Quaternion editorRotation;
        private static readonly Vector3[] corners = new Vector3[4];
        internal static bool IsDesktop()
        {
            SuperController sc = SuperController.singleton;
            return sc != null && !sc.isOVR && !sc.isOpenVR;
        }

        internal static bool Apply(Canvas canvas, bool compact)
        {
            if (canvas == null || !IsDesktop())
                return false;
            return Fit(canvas.transform as RectTransform, compact ? 0.30f : 0.02f, 0.02f, 0.98f, 0.98f, true);
        }

        internal static bool Fit(RectTransform panel, float left, float bottom, float rightEdge, float top, bool detach)
        {
            return panel != null && FitBounds(panel, panel.rect, left, bottom, rightEdge, top, detach);
        }

        private static bool FitBounds(RectTransform panel, Rect rect, float left, float bottom, float rightEdge, float top, bool detach)
        {
            if (!IsDesktop() || panel == null || (!detach && !panel.gameObject.activeInHierarchy))
                return false;
            Camera camera = SuperController.singleton.lookCamera;
            if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                return false;
            float distance = Mathf.Max(1.15f, camera.nearClipPlane + 0.05f);
            if (distance >= camera.farClipPlane)
                return false;
            // Reserve the left dock in picker/compact mode. Both axes remain
            // inside the viewport, including narrow windows and 4:3 screens.
            Vector3 lower = camera.ViewportToWorldPoint(new Vector3(left, bottom, distance));
            Vector3 upper = camera.ViewportToWorldPoint(new Vector3(rightEdge, top, distance));
            Vector3 right = camera.transform.right;
            Vector3 up = camera.transform.up;
            Vector3 span = upper - lower;
            float width = Mathf.Abs(Vector3.Dot(span, right));
            float height = Mathf.Abs(Vector3.Dot(span, up));
            if (!(rect.width > 0f) || !(rect.height > 0f))
                return false;
            float scale = Mathf.Min(width / rect.width, height / rect.height);
            if (!(scale > 0f) || float.IsInfinity(scale))
                return false;
            // A scene-rooted canvas avoids inherited HUD mirror/nonuniform
            // scaling. Update only this panel; never move the native HUD.
            if (detach && panel.parent != null)
                panel.SetParent(null, false);
            Vector3 parentScale = panel.parent == null ? Vector3.one : panel.parent.lossyScale;
            if (Mathf.Abs(parentScale.x) < 1e-8f || Mathf.Abs(parentScale.y) < 1e-8f || Mathf.Abs(parentScale.z) < 1e-8f)
                return false;
            panel.rotation = camera.transform.rotation;
            panel.localScale = new Vector3(scale / parentScale.x, scale / parentScale.y, scale / parentScale.z);
            panel.position = (lower + upper) * 0.5f - panel.TransformVector(rect.center);
            Canvas canvas = panel.GetComponent<Canvas>();
            if (canvas != null) canvas.worldCamera = camera;
            return true;
        }

        internal static void FitLeft(RectTransform dock)
        { Fit(dock, 0.02f, 0.02f, 0.29f, 0.98f, false); }

        internal static void FitExpression(GameObject root)
        { if (root != null) FitLeft(root.transform as RectTransform); }

        internal static void FitEditor(Transform root, GameObject list)
        {
            if (!IsDesktop()) { RestoreEditor(); return; }
            if (root != null && list != null && list.activeInHierarchy)
            {
                if (editor != root)
                {
                    RestoreEditor(); editor = root;
                    editorScale = root.localScale; editorPosition = root.position; editorRotation = root.rotation;
                }
                // UIAssist's CanvasBase rect is only a default 100x100;
                // measure its immediate widgets, not scroll-content descendants.
                RectTransform panel = root as RectTransform;
                if (panel == null) return;
                float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
                float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
                for (int n = 0; n < root.childCount; n++)
                {
                    RectTransform child = root.GetChild(n) as RectTransform;
                    if (child == null || !child.gameObject.activeInHierarchy) continue;
                    child.GetWorldCorners(corners);
                    for (int i = 0; i < 4; i++)
                    {
                        Vector3 p = root.InverseTransformPoint(corners[i]);
                        minX = Mathf.Min(minX, p.x); maxX = Mathf.Max(maxX, p.x);
                        minY = Mathf.Min(minY, p.y); maxY = Mathf.Max(maxY, p.y);
                    }
                }
                Rect bounds = maxX > minX && maxY > minY
                    ? new Rect(minX, minY, maxX - minX, maxY - minY) : panel.rect;
                FitBounds(panel, bounds, 0.32f, 0.10f, 0.98f, 0.74f, false);
            }
        }

        internal static void RestoreEditor()
        {
            if (editor != null)
            {
                editor.localScale = editorScale; editor.position = editorPosition; editor.rotation = editorRotation;
            }
            editor = null;
        }

        internal static void FitBan(RectTransform dock)
        { Fit(dock, 0.32f, 0.80f, 0.98f, 0.88f, false); }

        internal static void FitLock(RectTransform dock)
        { Fit(dock, 0.32f, 0.90f, 0.98f, 0.98f, false); }

        internal static void FitOutfit(RectTransform option)
        { Fit(option, 0.73f, 0.02f, 0.98f, 0.08f, false); }

        internal static void FitToggle(UIDynamicButton button)
        { if (button != null) Fit(button.transform as RectTransform, 0.91f, 0.75f, 0.98f, 0.79f, false); }

        internal static void FitToolbar(GameObject root)
        {
            if (!IsDesktop() || root == null || !root.activeInHierarchy) return;
            RectTransform rect = root.transform as RectTransform;
            RectTransform parent = rect == null ? null : rect.parent as RectTransform;
            if (parent == null || rect.rect.width <= 0f || parent.rect.width <= 16f) return;
            float scale = Mathf.Min(1f, (parent.rect.width - 16f) / rect.rect.width);
            rect.localScale = new Vector3(scale, scale, 1f);
        }

        internal static bool MouseRay(out Ray ray)
        {
            ray = new Ray();
            if (!IsDesktop()) return false;
            Camera camera = SuperController.singleton.lookCamera;
            if (camera == null) return false;
            ray = camera.ScreenPointToRay(Input.mousePosition);
            return true;
        }

        internal static bool Surface(RectTransform content, out Vector2 local, out Vector3 world)
        {
            local = Vector2.zero; world = Vector3.zero;
            Ray ray;
            if (content == null || !MouseRay(out ray)) return false;
            Plane plane = new Plane(content.forward, content.position);
            float distance;
            if (!plane.Raycast(ray, out distance) || distance < 0f) return false;
            world = ray.GetPoint(distance);
            local = content.InverseTransformPoint(world);
            local.x += content.rect.width * 0.5f;
            return true;
        }
    }
}
