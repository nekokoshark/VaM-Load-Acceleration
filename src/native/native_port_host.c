/* Collector crossover in an isolated process. No fixed Mono addresses.
 * Managed actions run on attached, joined threads which actually exit.
 * This executable never opens the game process or loads UnityPlayer. */
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <psapi.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdint.h>

typedef void* (*Init)(const char*, const char*);
typedef void (*Dirs)(const char*, const char*);
typedef void (*PathFn)(const char*);
typedef void* (*Open)(void*, const char*);
typedef void* (*Image)(void*);
typedef void* (*Class)(void*, const char*, const char*);
typedef void* (*Method)(void*, const char*, int);
typedef void* (*Invoke)(void*, void*, void*, void**);
typedef void* (*Attach)(void*);
typedef void (*Detach)(void*);
typedef int (*Generation)(void);
typedef void (*Collect)(int);
typedef void (*InternalCall)(const char*, const void*);
typedef void (*DllMap)(void*, const char*, const char*, const char*, const char*);
typedef int (*Exec)(void*, void*, int, char**);
typedef const char* (*BuildInfo)(void);
typedef unsigned long long (*Counter)(void);

static HMODULE port_runtime;
static void *domain, *klass;
static Method method;
static Invoke invoke;
static Attach attach;
static Detach detach;
static Collect collect;
static int generation;
static Counter heap_size, used_size;

static void memory_point(const char* stage)
{
    PROCESS_MEMORY_COUNTERS_EX counters = {0};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&counters, sizeof(counters)))
        ExitProcess(87);
    printf("MEMORY stage=%s private=%llu workingSet=%llu heap=%llu used=%llu\n", stage,
           (unsigned long long)counters.PrivateUsage, (unsigned long long)counters.WorkingSetSize,
           heap_size(), used_size());
}

static FARPROC symbol(HMODULE module, const char* name)
{
    FARPROC value = GetProcAddress(module, name);
    if (!value) { fprintf(stderr, "MISSING_EXPORT %s\n", name); ExitProcess(71); }
    return value;
}

static void utf8(const WCHAR* source, char* destination, int capacity)
{
    if (!WideCharToMultiByte(CP_UTF8, 0, source, -1, destination, capacity, NULL, NULL))
        ExitProcess(72);
}

static int native_touch(void* pointer, int size, unsigned char value)
{
    unsigned char* bytes = (unsigned char*)pointer;
    int offset;
    if (!bytes || size < 1) return -1;
    for (offset = 0; offset < size; offset += 4096) bytes[offset] = value;
    bytes[size - 1] = value;
    return size;
}

typedef struct Request {
    char name[80];
    char output[4096];
} Request;

#include "backend_host_extra.inc"

static DWORD WINAPI run_action(void* input)
{
    Request* request = (Request*)input;
    void* thread = attach(domain);
    if (!thread) return 1;
    if (!strcmp(request->name, "GC")) {
        collect(generation);
        strcpy_s(request->output, sizeof(request->output), "GC_COMPLETED");
    } else {
        void *exception = NULL, *result;
        void* function = method(klass, request->name, 0);
        if (!function) { detach(thread); return 2; }
        result = invoke(function, NULL, NULL, &exception);

        if (exception || !result) {
            typedef void* (*ObjectClass)(void*);
            typedef const char* (*ClassName)(void*);
            const char* name = exception ? ((ClassName)symbol(port_runtime,"mono_class_get_name"))(
                ((ObjectClass)symbol(port_runtime,"mono_object_get_class"))(exception)) : "null-result";
            fprintf(stderr,"MANAGED_EXCEPTION action=%s class=%s\n",request->name,name);
            detach(thread); return 3;
        }
        printf("RETURN_IDENTITY action=%s object=%p vtable=%p length=%d\n",request->name,
            result,*(void**)result,*(int*)((char*)result+16)); fflush(stdout);

        {
            /* Both candidates are x64; checked string header for this host. */
            int length = *(int*)((char*)result + 16);
            int bytes;
            if (length < 0 || length >= 1024) { detach(thread); return 4; }
            bytes = WideCharToMultiByte(CP_UTF8, 0, (const WCHAR*)((char*)result + 20), length,
                       request->output, sizeof(request->output) - 1, NULL, NULL);
            if (!bytes) { detach(thread); return 5; }
            request->output[bytes] = 0;
        }
    }
    detach(thread);
    return 0;
}

int wmain(int argc, WCHAR** argv)
{
    char dll[32768], search[32768], lib[32768], config[32768], version[256], helper[32768];
    char command[80];
    HMODULE runtime;
    void *assembly, *image;
    BuildInfo info;
    Generation moving;
    union { int (*function)(void*, int, unsigned char); const void* address; } callback;
    SetErrorMode(SEM_FAILCRITICALERRORS | SEM_NOGPFAULTERRORBOX | SEM_NOOPENFILEERRORBOX);
    if (argc < 7) return 64;
    utf8(argv[1], dll, sizeof(dll)); utf8(argv[2], lib, sizeof(lib));
    utf8(argv[3], config, sizeof(config)); utf8(argv[4], version, sizeof(version));
    utf8(argv[5], helper, sizeof(helper)); utf8(argv[6], search, sizeof(search));
    runtime = LoadLibraryExW(argv[1], NULL, LOAD_WITH_ALTERED_SEARCH_PATH);
    port_runtime = runtime;
    if (!runtime) { fprintf(stderr, "LOAD_LIBRARY_ERROR %lu\n", GetLastError()); return 65; }
    info = (BuildInfo)GetProcAddress(runtime, "mono_get_runtime_build_info");
    printf("NATIVE_BUILD=%s\n", info ? info() : "not-exposed"); fflush(stdout);
    ((PathFn)symbol(runtime, "mono_set_assemblies_path"))(search);
    ((Dirs)symbol(runtime, "mono_set_dirs"))(lib, config);
    domain = ((Init)symbol(runtime, "mono_jit_init_version"))("CollectorCrossover", version);
    if (!domain) return 66;
    ((DllMap)symbol(runtime, "mono_dllmap_insert"))(NULL, "mono", NULL, dll, NULL);
    generation = ((Generation)symbol(runtime, "mono_gc_max_generation"))();
    moving = (Generation)GetProcAddress(runtime, "mono_gc_is_moving");
    heap_size = (Counter)symbol(runtime, "mono_gc_get_heap_size");
    used_size = (Counter)symbol(runtime, "mono_gc_get_used_size");
    printf("COLLECTOR maxGeneration=%d moving=%d requestedRuntime=%s\n",
           generation, moving ? moving() : -1, version); fflush(stdout);
    assembly = ((Open)symbol(runtime, "mono_domain_assembly_open"))(domain, helper);
    if (!assembly) return 67;
    if (argc > 7) {
        char** arguments;
        int count = argc - 6, i, result;
        arguments = (char**)calloc(count, sizeof(char*));
        if (!arguments) return 68;
        arguments[0] = helper;
        for (i = 1; i < count; ++i) {
            arguments[i] = (char*)calloc(32768, 1);
            if (!arguments[i]) return 68;
            utf8(argv[i + 6], arguments[i], 32768);
        }
        result = ((Exec)symbol(runtime, "mono_jit_exec"))(domain, assembly, count, arguments);
        for (i = 1; i < count; ++i) free(arguments[i]);
        free(arguments);
        return result;
    }
    image = ((Image)symbol(runtime, "mono_assembly_get_image"))(assembly);
    klass = ((Class)symbol(runtime, "mono_class_from_name"))(image, "", "CollectorProbe");
    if (!klass) return 69;
    /* Windows x64 uses the same representation for registered code addresses. */
    callback.function = native_touch;
    ((InternalCall)symbol(runtime, "mono_add_internal_call"))("CollectorProbe::NativeTouch", callback.address);
    backend_calls();
    method = (Method)symbol(runtime, "mono_class_get_method_from_name");
    invoke = (Invoke)symbol(runtime, "mono_runtime_invoke");
    attach = (Attach)symbol(runtime, "mono_thread_attach");
    detach = (Detach)symbol(runtime, "mono_thread_detach");
    collect = (Collect)symbol(runtime, "mono_gc_collect");
    detach(attach(domain));
    puts("READY"); fflush(stdout);
    while (fgets(command, sizeof(command), stdin)) {
        Request request = {0};
        DWORD status = 99;
        HANDLE worker;
        command[strcspn(command, "\r\n")] = 0;
        if (!strcmp(command, "QUIT")) break;
        if (!strcmp(command, "CLEANUP")) {
            void* main_thread=attach(domain);
            ((void (*)(void*))symbol(runtime,"mono_thread_set_main"))(main_thread);
            ((void (*)(void*))symbol(runtime,"mono_unity_jit_cleanup"))(domain);
            puts("UNITY_CLEANUP_PASS originalExport=True"); fflush(stdout);
            break;
        }
        if (strlen(command) >= sizeof(request.name)) return 80;
        strcpy_s(request.name, sizeof(request.name), command);
        worker = CreateThread(NULL, 0, run_action, &request, 0, NULL);
        if (!worker) return 81;
        if (WaitForSingleObject(worker, INFINITE) != WAIT_OBJECT_0) return 82;
        if (!GetExitCodeThread(worker, &status)) return 83;
        if (!CloseHandle(worker)) return 84;
        if (status) { fprintf(stderr, "ACTION_FAILED %s %lu\n", command, status); return 85; }
        puts(request.output); fflush(stdout);
        memory_point(command); fflush(stdout);
        if (!strncmp(request.output, "FAIL ", 5)) return 86;
    }
    return 0;
}
