using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using UnityEngine;

public class PointerHarness
{
    const BindingFlags All = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    static int checks, closed, callbackCount;
    static string navigated;
    public static bool Same(UnityEngine.Object a, UnityEngine.Object b) { return ReferenceEquals(a,b); }
    public static bool Different(UnityEngine.Object a, UnityEngine.Object b) { return !ReferenceEquals(a,b); }
    public static float Now() { return 10f; }
    public static void Closed(object browser) { closed++; }
    public static void Navigate(string path) { navigated = path; }
    static void Check(bool value, string name) { checks++; if (!value) throw new Exception(name); }
    static object Blank(Type t) { return FormatterServices.GetUninitializedObject(t); }
    static void Set(object instance, string field, object value)
    { instance.GetType().GetField(field, All).SetValue(instance, value); }
    static object Get(object instance, string field) { return instance.GetType().GetField(field, All).GetValue(instance); }
    static object Invoke(object instance, string name, params object[] args)
    { return instance.GetType().GetMethods(All).Single(m => m.Name == name && m.GetParameters().Length == args.Length).Invoke(instance,args); }
    static object Pointer(Type t, string name, bool right)
    { return t.GetMethod(name,All,null,new[]{typeof(bool)},null).Invoke(null,new object[]{right}); }
    static void Flag(SuperController sc, string name, bool value)
    { typeof(SuperController).GetField(name,All).SetValue(sc,value); }
    static int Main(string[] args)
    {
        try
        {
            bool modified = args[0] == "MODIFIED";
            var a = Assembly.Load(File.ReadAllBytes(args[1]));
            var pointer = a.GetTypes().Single(t => t.Name == "VrPointerPresentation");
            var browserType = a.GetTypes().Single(t => t.Name == "VrPresetBrowser");
            var sc = (SuperController)Blank(typeof(SuperController));
            typeof(SuperController).GetField("_singleton",All).SetValue(null,sc);
            var module = (LookInputModule)Blank(typeof(LookInputModule));
            typeof(LookInputModule).GetField("_singleton",All).SetValue(null,module);
            var mouse = (GameObject)Blank(typeof(GameObject));
            var left = (GameObject)Blank(typeof(GameObject));
            var right = (GameObject)Blank(typeof(GameObject));
            var cm = (RectTransform)Blank(typeof(RectTransform));
            var cl = (RectTransform)Blank(typeof(RectTransform));
            var cr = (RectTransform)Blank(typeof(RectTransform));
            Set(module,"currentLookMouse",mouse); Set(module,"currentLook",left); Set(module,"currentLookRight",right);
            Set(module,"cursorMouse",cm); Set(module,"cursor",cl); Set(module,"cursorRight",cr);
            int defects=0;
            foreach(bool r in new[]{false,true})
            {
                bool hit = ReferenceEquals(Pointer(pointer,"CurrentLookTarget",r),mouse);
                bool cursor = ReferenceEquals(Pointer(pointer,"CurrentCursor",r),cm);
                if (!hit) defects++; if(!cursor) defects++;
                Check(hit == modified,"desktop uses mouse target"); Check(cursor == modified,"desktop uses mouse cursor");
            }
            // The actual card/commit methods are retained; only engine-only
            // time, native-object equality, navigation and UI-close are seams.
            int selections=0;
            foreach(string category in new[]{"Appearance","Clothing","Hair","Skin"})
            foreach(string prefix in new[]{"", "Fixture.Package.1:/"})
            foreach(bool full in new[]{false,true})
            {
                string path=prefix+"Custom/Atom/Person/"+category+"/Preset_测试 name.vap";
                var browser=Blank(browserType);
                var itemsField=browserType.GetField("_items",All);
                var list=(IList)Activator.CreateInstance(itemsField.FieldType);
                var item=Blank(browserType.GetNestedType("GridItem",All));
                Set(item,"Path",path); Set(item,"Label","测试 name"); Set(item,"FileName","Preset_测试 name.vap"); list.Add(item); itemsField.SetValue(browser,list);
                var card=Blank(browserType.GetNestedType("CardView",All)); Set(card,"ItemIndex",0);
                object target=new object(), captured=target; int calls=0;
                Action<string,bool> cb=(p,c)=>{Check(p==path&&c&&ReferenceEquals(target,captured),"full callback exact path/target/closed");calls++;callbackCount++;};
                Action<string> simple=p=>{Check(p==path&&ReferenceEquals(target,captured),"simple callback exact path/target");calls++;callbackCount++;};
                Set(browser,full?"_cbFull":"_cb",full?(object)cb:simple);
                int before=closed;
                if(ReferenceEquals(Pointer(pointer,"CurrentLookTarget",false),mouse))
                    Invoke(browser,"OnCardClicked",card);
                Check(calls==(modified?1:0),"mouse release reaches callback once");
                Check(closed==before+(modified?1:0),"load closes once");
                if(modified) {Check(Get(browser,"_cb")==null&&Get(browser,"_cbFull")==null,"callback cleared before close");selections++;}
                // Folder cards use the exact same pointer gate.
                Set(item,"IsDir",true); navigated=null;
                if(ReferenceEquals(Pointer(pointer,"CurrentLookTarget",true),mouse)) Invoke(browser,"OnCardClicked",card);
                Check((navigated==path)==modified,"folder click navigates");
            }
            // Null mouse must not reuse a stale controller hit in desktop mode.
            Set(module,"currentLookMouse",null); Set(module,"cursorMouse",null);
            if(modified)foreach(bool r in new[]{false,true})
            {Check(Pointer(pointer,"CurrentLookTarget",r)==null,"no stale hand fallback");Check(Pointer(pointer,"CurrentCursor",r)==null,"no stale cursor fallback");}
            Set(module,"currentLookMouse",mouse);Set(module,"cursorMouse",cm);
            foreach(string mode in new[]{"isOVR","isOpenVR"})
            {
                Flag(sc,mode,true);
                foreach(bool r in new[]{false,true})
                { Check(ReferenceEquals(Pointer(pointer,"CurrentLookTarget",r),r?right:left),"VR hand target unchanged");Check(ReferenceEquals(Pointer(pointer,"CurrentCursor",r),r?cr:cl),"VR hand cursor unchanged"); }
                Flag(sc,mode,false);
            }
            typeof(LookInputModule).GetField("_singleton",All).SetValue(null,null);
            foreach(bool r in new[]{false,true})
            {Check(Pointer(pointer,"CurrentLookTarget",r)==null,"missing module target");Check(Pointer(pointer,"CurrentCursor",r)==null,"missing module cursor");}
            // JIT the uninstrumented shipping input/layout methods separately
            // in jit mode (the native engine is not run by this fixture).
            Console.WriteLine(args[0]+"_PASS checks="+checks+" desktopFieldDefects="+defects+" presetSelections="+selections+" folders="+(modified?16:0)+" vrHands=PASS callbacks="+callbackCount);
            return 0;
        }
        catch(Exception e) {Console.Error.WriteLine(e);return 70;}
    }
}
