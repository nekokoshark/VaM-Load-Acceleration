using System;
using Quest3TriggerUI;

// Deterministic projection/input fixture; this is not a Unity rendering test.
namespace UnityEngine
{
    public class Component { public Transform transform; public GameObject gameObject; public T GetComponent<T>() where T:class { return gameObject == null ? null : gameObject.GetComponent<T>(); } }
    public class GameObject
    {
        public Transform transform; public bool activeInHierarchy=true; public Canvas canvas;
        public T GetComponent<T>() where T:class { return canvas as T; }
    }
    public struct Vector2 { public float x,y; public Vector2(float a,float b){x=a;y=b;} public static Vector2 zero {get{return new Vector2();}} public static implicit operator Vector3(Vector2 v){return new Vector3(v.x,v.y,0);} public static implicit operator Vector2(Vector3 v){return new Vector2(v.x,v.y);} }
    public struct Vector3
    {
        public float x,y,z; public Vector3(float a,float b,float c){x=a;y=b;z=c;}
        public static Vector3 one {get{return new Vector3(1,1,1);}} public static Vector3 zero {get{return new Vector3();}}
        public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
        public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
        public static Vector3 operator *(Vector3 a,float b){return new Vector3(a.x*b,a.y*b,a.z*b);}
        public static float Dot(Vector3 a,Vector3 b){return a.x*b.x+a.y*b.y+a.z*b.z;}
    }
    public struct Quaternion
    {
        public float yaw;
        public static Vector3 operator *(Quaternion q,Vector3 v){double r=q.yaw*Math.PI/180;return new Vector3((float)(Math.Cos(r)*v.x+Math.Sin(r)*v.z),v.y,(float)(-Math.Sin(r)*v.x+Math.Cos(r)*v.z));}
    }
    public struct Rect
    {
        public float x,y,width,height;
        public Rect(float a,float b,float w,float h){x=a;y=b;width=w;height=h;}
        public Vector2 center {get{return new Vector2(x+width*.5f,y+height*.5f);}}
    }
    public class Transform:Component
    {
        public readonly System.Collections.Generic.List<Transform> children=new System.Collections.Generic.List<Transform>();
        public int childCount{get{return children.Count;}} public Transform GetChild(int i){return children[i];}
        Transform _parent; Vector3 _position; Quaternion _rotation;
        public Vector3 localScale=Vector3.one;
        public Transform parent {get{return _parent;}set{if(_parent!=null)_parent.children.Remove(this);_parent=value;if(_parent!=null)_parent.children.Add(this);}}
        public Vector3 position {get{return parent==null?_position:parent.position+parent.TransformVector(_position);}set{_position=parent==null?value:parent.InverseTransformPoint(value);}}
        public Quaternion rotation {get{return new Quaternion{yaw=_rotation.yaw+(parent==null?0:parent.rotation.yaw)};}set{_rotation=new Quaternion{yaw=value.yaw-(parent==null?0:parent.rotation.yaw)};}}
        public Transform(){transform=this;gameObject=new GameObject();gameObject.transform=this;}
        public Vector3 lossyScale {get{Vector3 p=parent==null?Vector3.one:parent.lossyScale;return new Vector3(p.x*localScale.x,p.y*localScale.y,p.z*localScale.z);}}
        public Vector3 right{get{return rotation*new Vector3(1,0,0);}} public Vector3 up{get{return rotation*new Vector3(0,1,0);}} public Vector3 forward{get{return rotation*new Vector3(0,0,1);}}
        public void SetParent(Transform p,bool keep){Vector3 old=position;Quaternion q=rotation;parent=p;if(keep){position=old;rotation=q;}}
        public Vector3 TransformVector(Vector3 v){Vector3 s=lossyScale;return rotation*new Vector3(v.x*s.x,v.y*s.y,v.z*s.z);}
        public Vector3 InverseTransformPoint(Vector3 v){v=new Quaternion{yaw=-rotation.yaw}*(v-position);Vector3 s=lossyScale;return new Vector3(v.x/s.x,v.y/s.y,v.z/s.z);}
    }
    public class RectTransform:Transform {public Rect rect;public void GetWorldCorners(Vector3[] c){c[0]=position+TransformVector(new Vector3(rect.x,rect.y,0));c[1]=position+TransformVector(new Vector3(rect.x,rect.y+rect.height,0));c[2]=position+TransformVector(new Vector3(rect.x+rect.width,rect.y+rect.height,0));c[3]=position+TransformVector(new Vector3(rect.x+rect.width,rect.y,0));}}
    public class Canvas:Component {public Camera worldCamera; public Canvas(RectTransform p){transform=p;gameObject=p.gameObject;gameObject.canvas=this;}}
    public class Camera:Component
    {
        public int pixelWidth,pixelHeight;public float nearClipPlane=.1f,farClipPlane=1000,fov=60;public bool orthographic;
        public Camera(){transform=new Transform();gameObject=transform.gameObject;}
        float Half(float z){return orthographic?1.5f:(float)Math.Tan(fov*Math.PI/360)*z;}
        public Vector3 ViewportToWorldPoint(Vector3 v){float h=Half(v.z);return transform.position+transform.rotation*new Vector3((v.x-.5f)*2*h*pixelWidth/pixelHeight,(v.y-.5f)*2*h,v.z);}
        public Vector3 WorldToViewportPoint(Vector3 v){v=new Quaternion{yaw=-transform.rotation.yaw}*(v-transform.position);float h=Half(v.z);return new Vector3(.5f+v.x/(2*h*pixelWidth/pixelHeight),.5f+v.y/(2*h),v.z);}
        public Ray ScreenPointToRay(Vector3 p){Vector3 v=ViewportToWorldPoint(new Vector3(p.x/pixelWidth,p.y/pixelHeight,1));return new Ray(transform.position,v-transform.position);}
    }
    public struct Ray {public Vector3 origin,direction;public Ray(Vector3 a,Vector3 b){origin=a;direction=b;}public Vector3 GetPoint(float t){return origin+direction*t;}}
    public struct Plane {Vector3 n,p;public Plane(Vector3 normal,Vector3 point){n=normal;p=point;}public bool Raycast(Ray r,out float d){float v=Vector3.Dot(n,r.direction);d=v==0?-1:Vector3.Dot(n,p-r.origin)/v;return d>=0;}}
    public static class Input {public static Vector3 mousePosition;}
    public static class Mathf {public static float Abs(float f){return Math.Abs(f);}public static float Min(float a,float b){return Math.Min(a,b);}public static float Max(float a,float b){return Math.Max(a,b);}}
}
public class SuperController {public static SuperController singleton;public bool isOVR,isOpenVR;public UnityEngine.Camera lookCamera;}
public class UIDynamicButton:UnityEngine.Component {}

class LayoutTests
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception("FAIL "+name);}
    static UnityEngine.RectTransform Panel(float w,float h,float px=.5f,float py=.5f)
    {return new UnityEngine.RectTransform{rect=new UnityEngine.Rect(-px*w,-py*h,w,h)};}
    static bool Inside(UnityEngine.RectTransform panel,UnityEngine.Camera camera,float l,float b,float r,float t)
    {
        var rect=panel.rect;
        foreach(float x in new[]{rect.x,rect.x+rect.width})foreach(float y in new[]{rect.y,rect.y+rect.height})
        {
            var v=camera.WorldToViewportPoint(panel.position+panel.TransformVector(new UnityEngine.Vector3(x,y,0)));
            if(v.x<l-.0001f||v.x>r+.0001f||v.y<b-.0001f||v.y>t+.0001f||v.z<=camera.nearClipPlane)return false;
        }
        return true;
    }
    static int Baseline()
    {
        int cropped=0;
        foreach(int w in new[]{800,1024,1280,1366,1920,2560})
        {
            var camera=new UnityEngine.Camera{pixelWidth=w,pixelHeight=1080,fov=40};
            var panel=Panel(1900,1100);panel.localScale=UnityEngine.Vector3.one*.0009f;panel.position=new UnityEngine.Vector3(-.25f,0,1.15f);
            if(!Inside(panel,camera,0,0,1,1))cropped++;
        }
        Check(cropped>0,"original fixed world-size clips at desktop aspect ratios");
        return cropped;
    }
    static int Main(string[] args)
    {
        try
        {
            if(args[0]=="baseline"||args[0]=="rollback") {int c=Baseline();Console.WriteLine(args[0].ToUpper()+"_PASS projectionCropReproduced="+c+" fixedScale=0.0009 desktopBranch=0");return 0;}
            var sc=new SuperController();SuperController.singleton=sc;var camera=new UnityEngine.Camera();sc.lookCamera=camera;
            int layouts=0;
            foreach(int[] size in new[]{new[]{800,600},new[]{1024,768},new[]{1280,720},new[]{1366,768},new[]{1920,1080},new[]{2560,1440},new[]{3440,1440},new[]{3840,2160},new[]{720,1280}})
            foreach(float fov in new[]{35f,60f,100f})foreach(bool ortho in new[]{false,true})foreach(bool compact in new[]{false,true})
            {
                camera.pixelWidth=size[0];camera.pixelHeight=size[1];camera.fov=fov;camera.orthographic=ortho;camera.transform.position=new UnityEngine.Vector3(3,2,1);camera.transform.rotation=new UnityEngine.Quaternion{yaw=37};
                var panel=Panel(1900,1100);panel.parent=new UnityEngine.Transform{localScale=new UnityEngine.Vector3(-.001f,.002f,.001f)};var canvas=new UnityEngine.Canvas(panel);
                Check(BrowserMouseInput.FitBrowser(canvas,compact),"apply");Check(panel.parent==null,"detach mirrored HUD");Check(canvas.worldCamera==camera,"event camera");Check(Inside(panel,camera,compact?.30f:.02f,.02f,.98f,.98f),"whole browser visible");
                Check(BrowserMouseInput.FitBrowser(canvas,!compact),"resize while open");Check(Inside(panel,camera,!compact?.30f:.02f,.02f,.98f,.98f),"compact transition");
                foreach(float[] spec in new[]{new[]{900f,1100f,0f,1f},new[]{2200f,600f,.5f,0f},new[]{1900f,1100f,.5f,.5f}})
                {
                    var dock=Panel(spec[0],spec[1],spec[2],spec[3]);dock.parent=new UnityEngine.Transform{localScale=new UnityEngine.Vector3(-2,3,1)};
                    Check(DesktopBrowserLayout.Fit(dock,.02f,.02f,.29f,.98f,false),"fit dock");Check(Inside(dock,camera,.02f,.02f,.29f,.98f),"dock corners, noncentral pivot, mirrored parent");
                }
                layouts++;
            }
            camera.orthographic=false;camera.pixelWidth=1366;camera.pixelHeight=768;camera.fov=60;
            var content=Panel(1700,900,.5f,1);DesktopBrowserLayout.Fit(content,.02f,.02f,.98f,.98f,true);
            foreach(float[] point in new[]{new[]{.1f,.1f},new[]{.7f,.3f},new[]{.9f,.9f}})
            {
                UnityEngine.Input.mousePosition=new UnityEngine.Vector3(point[0]*camera.pixelWidth,point[1]*camera.pixelHeight,0);
                UnityEngine.Vector2 local;UnityEngine.Vector3 world;Check(BrowserMouseInput.Surface(content,out local,out world),"mouse surface ray");
                var projected=camera.WorldToViewportPoint(world);Check(Math.Abs(projected.x-point[0])<.0001&&Math.Abs(projected.y-point[1])<.0001,"mouse not center/gaze ray");
            }
            var toolbar=Panel(1220,92);toolbar.parent=Panel(650,500);DesktopBrowserLayout.FitToolbar(toolbar.gameObject);Check(Math.Abs(toolbar.localScale.x-634f/1220f)<.0001,"toolbar fits native width");
            var editor=Panel(2100,1000);editor.position=new UnityEngine.Vector3(8,9,10);editor.localScale=new UnityEngine.Vector3(.002f,.002f,.002f);
            DesktopBrowserLayout.FitEditor(editor,editor.gameObject);Check(Inside(editor,camera,.32f,.10f,.98f,.74f),"editor fit");DesktopBrowserLayout.RestoreEditor();Check(editor.position.x==8&&editor.localScale.x==.002f,"editor teardown restores native pose and scale");
            var tinyCanvas=Panel(100,100);var nativeList=Panel(2100,1000);nativeList.parent=tinyCanvas;nativeList.position=new UnityEngine.Vector3(500,200,0);
            DesktopBrowserLayout.FitEditor(tinyCanvas,nativeList.gameObject);Check(Inside(nativeList,camera,.32f,.10f,.98f,.74f),"CanvasBase default 100x100 uses direct widget bounds");DesktopBrowserLayout.RestoreEditor();
            var panelVr=Panel(1900,1100);var canvasVr=new UnityEngine.Canvas(panelVr);var parentVr=new UnityEngine.Transform();panelVr.parent=parentVr;
            sc.isOVR=true;Check(!BrowserMouseInput.FitBrowser(canvasVr,false)&&panelVr.parent==parentVr&&panelVr.localScale.x==1,"OVR untouched");sc.isOVR=false;sc.isOpenVR=true;Check(!BrowserMouseInput.FitBrowser(canvasVr,false)&&panelVr.parent==parentVr,"OpenVR untouched");sc.isOpenVR=false;
            camera.pixelWidth=0;Check(!BrowserMouseInput.FitBrowser(canvasVr,false),"minimized no invalid scaling");camera.pixelWidth=800;sc.lookCamera=null;Check(!BrowserMouseInput.FitBrowser(canvasVr,false),"no camera no placement mutation");
            Console.WriteLine("MODIFIED_PASS layouts="+layouts+" checks="+checks+" mouseRay=PASS editorRestore=PASS vrUnchanged=PASS");return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 70;}
    }
}
