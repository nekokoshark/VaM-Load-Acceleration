using UnityEngine;

namespace Quest3TriggerUI
{
    internal static class BrowserMouseInput
    {
        internal static bool IsDesktop()
        {
            SuperController sc = SuperController.singleton;
            return sc != null && !sc.isOVR && !sc.isOpenVR;
        }

        internal static bool FitBrowser(Canvas canvas, bool compact)
        {
            if (canvas == null || !IsDesktop()) return false;
            Camera camera = SuperController.singleton.lookCamera;
            if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                return false;
            RectTransform panel = canvas.transform as RectTransform;
            if (panel == null || panel.rect.width <= 0f || panel.rect.height <= 0f)
                return false;
            float distance = Mathf.Max(1.15f, camera.nearClipPlane + 0.05f);
            if (distance >= camera.farClipPlane) return false;
            Vector3 lower = camera.ViewportToWorldPoint(new Vector3(compact ? 0.30f : 0.02f, 0.02f, distance));
            Vector3 upper = camera.ViewportToWorldPoint(new Vector3(0.98f, 0.98f, distance));
            Vector3 span = upper - lower;
            float scale = Mathf.Min(Mathf.Abs(Vector3.Dot(span, camera.transform.right)) / panel.rect.width,
                Mathf.Abs(Vector3.Dot(span, camera.transform.up)) / panel.rect.height);
            if (!(scale > 0f) || float.IsInfinity(scale)) return false;
            if (panel.parent != null) panel.SetParent(null, false);
            panel.rotation = camera.transform.rotation;
            panel.localScale = Vector3.one * scale;
            panel.position = (lower + upper) * 0.5f - panel.TransformVector(panel.rect.center);
            canvas.worldCamera = camera;
            return true;
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

        internal static bool Surface(RectTransform content,
            out Vector2 local, out Vector3 world)
        {
            local = Vector2.zero;
            world = Vector3.zero;
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
