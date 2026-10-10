typedef void* (*ObjectClass)(void*);
typedef uint32_t (*NewHandle)(void*,int);
typedef void* (*GetHandle)(uint32_t);
typedef void (*FreeHandle)(uint32_t);
typedef void (*LiveCallback)(void**,int,void*);
typedef void* (*LiveBegin)(void*,unsigned,LiveCallback,void*,void (*)(void),void (*)(void));
typedef void (*LiveRoot)(void*,void*);
typedef void (*LiveEnd)(void*);
static uintptr_t remembered[6000];
extern int keep_register_root(void*,Collect,int);
static int native_register_root(void* payload)
{
    void* interior=(char*)payload+32+123;
    Collect gc=(Collect)symbol(port_runtime,"mono_gc_collect");
    return keep_register_root(interior,gc,((Generation)symbol(port_runtime,"mono_gc_max_generation"))());
}
extern int wait_guard_root(void*,volatile LONG*,volatile LONG*);
typedef struct { void* payload; volatile LONG ready,release; int value; } GuardContext;
static DWORD WINAPI guard_thread(void* input)
{
    GuardContext* context=(GuardContext*)input;
    void* thread=attach(domain);
    if(!thread)return 1;
    context->value=wait_guard_root((char*)context->payload+32+123,&context->ready,&context->release);
    detach(thread);
    return 0;
}
static int native_guard_root(void* payload)
{
    GuardContext context={0};
    HANDLE thread;
    ULONGLONG deadline=GetTickCount64()+10000;
    DWORD status;
    context.payload=payload;
    thread=CreateThread(NULL,0,guard_thread,&context,0,NULL);
    if(!thread)return -1;
    while(!InterlockedCompareExchange(&context.ready,0,0)) {
        if(GetTickCount64()>deadline)ExitProcess(90);
        Sleep(1);
    }
    collect(generation);
    InterlockedExchange(&context.release,1);
    if(WaitForSingleObject(thread,10000)!=WAIT_OBJECT_0)ExitProcess(91);
    if(!GetExitCodeThread(thread,&status) || !CloseHandle(thread) || status)return -2;
    return context.value;
}
static int native_remember(void* array,int compare)
{
    int i,moved=0;
    void** values=(void**)((char*)array+32);
    for(i=0;i<6000;i++) {
        if(compare) moved+=remembered[i]!=~(uintptr_t)values[i];
        else remembered[i]=~(uintptr_t)values[i];
    }
    return moved;
}
typedef struct { void** expected; unsigned char seen[6000]; int count; int bad; int moving; } LiveContext;
static int starts,stops;
static void live_start(void) { ++starts; }
static void live_stop(void) { ++stops; }
static void live_callback(void** objects,int count,void* data)
{
    LiveContext* context=(LiveContext*)data;
    int i,j;
    for(i=0;i<count;i++) {
        for(j=0;j<6000;j++) if(context->expected[j]==objects[i])break;
        if(j==6000 || context->seen[j] || (context->moving && (*(uintptr_t*)objects[i]&3)))context->bad=1;
        else { context->seen[j]=1; ++context->count; }
    }
}
static int native_liveness(void* array,int count)
{
    NewHandle new_handle=(NewHandle)symbol(port_runtime,"mono_gchandle_new");
    FreeHandle free_handle=(FreeHandle)symbol(port_runtime,"mono_gchandle_free");
    LiveContext context={0};
    void** values=(void**)((char*)array+32);
    void* filter;
    void* state;
    uint32_t pin;
    int i;
    if(count!=6000)return -1;
    pin=new_handle(array,1);
    context.expected=values;
    context.moving=((Generation)symbol(port_runtime,"mono_gc_max_generation"))()>0;
    filter=((ObjectClass)symbol(port_runtime,"mono_object_get_class"))(values[0]);
    starts=stops=0;
    state=((LiveBegin)symbol(port_runtime,"mono_unity_liveness_calculation_begin"))(
        filter,1000,live_callback,&context,live_start,live_stop);
    ((LiveRoot)symbol(port_runtime,"mono_unity_liveness_calculation_from_root"))(array,state);
    ((LiveEnd)symbol(port_runtime,"mono_unity_liveness_calculation_end"))(state);
    free_handle(pin);
    for(i=0;i<count;i++)if((*(uintptr_t*)values[i]&3) || !context.seen[i])context.bad=1;
    if(starts!=stops || starts<2)context.bad=1;
    if(context.bad)return -2;
    memset(context.seen,0,sizeof(context.seen)); context.count=0;
    state=((LiveBegin)symbol(port_runtime,"mono_unity_liveness_calculation_begin"))(
        filter,1000,live_callback,&context,live_start,live_stop);
    ((void (*)(void*))symbol(port_runtime,"mono_unity_liveness_calculation_from_statics"))(state);
    ((LiveEnd)symbol(port_runtime,"mono_unity_liveness_calculation_end"))(state);
    return context.bad ? -2 : context.count;
}
typedef struct { uint64_t address; uint32_t size; unsigned char* bytes; } HeapSection;
typedef struct { uint32_t count; HeapSection* sections; } Heap;
typedef struct { uint32_t count; uint64_t* objects; } Handles;
typedef struct { uint32_t words[6]; } RuntimeInfo;
typedef struct { Heap heap; Heap stacks; struct { uint32_t count; void* types; } metadata; Handles handles; RuntimeInfo info; void* user; } Snapshot;
static int native_snapshot(void* payload)
{
    Snapshot* snapshot;
    uint32_t i,pin=((NewHandle)symbol(port_runtime,"mono_gchandle_new"))(payload,1);
    int found=0;
    uintptr_t address=(uintptr_t)payload+32;
    snapshot=((Snapshot* (*)(void))symbol(port_runtime,"mono_unity_capture_memory_snapshot"))();
    if(!snapshot || !snapshot->metadata.count || !snapshot->handles.count)return -1;
    for(i=0;i<snapshot->heap.count;i++) {
        HeapSection* section=&snapshot->heap.sections[i];
        if(address>=section->address && address+8388608<=section->address+section->size) {
            size_t offset=(size_t)(address-section->address),n;
            if(section->bytes[offset+8388607]!=77)break;
            for(n=0;n<8388608;n+=4096)if(section->bytes[offset+n]!=91)break;
            found=n==8388608;
        }
    }
    ((void (*)(Snapshot*))symbol(port_runtime,"mono_unity_free_captured_memory_snapshot"))(snapshot);
    ((FreeHandle)symbol(port_runtime,"mono_gchandle_free"))(pin);
    return found;
}
static void backend_calls(void)
{
    InternalCall add=(InternalCall)symbol(port_runtime,"mono_add_internal_call");
    add("CollectorProbe::NativeLiveness",(const void*)native_liveness);
    add("CollectorProbe::NativeSnapshot",(const void*)native_snapshot);
    add("CollectorProbe::NativeRemember",(const void*)native_remember);
    add("CollectorProbe::NativeRegisterRoot",(const void*)native_register_root);
    add("CollectorProbe::NativeGuardRoot",(const void*)native_guard_root);
}
