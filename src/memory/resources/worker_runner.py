"""Host only the compiler executable on the installed game's original Mono."""
import ctypes
from ctypes import wintypes
import msvcrt
import os
from pathlib import Path
import sys


def run_mono() -> int:
    root = Path(os.environ["VAM_COMPILER_GAME"])
    executable = Path(sys.argv[1]).resolve()
    managed = root / "VaM_Data/Managed"
    paths = ";".join(map(str, (executable.parent, managed, root / "BepInEx/core")))
    os.environ["MONO_PATH"] = paths
    mono = ctypes.CDLL(str(root / "Mono/EmbedRuntime/mono.dll"))
    mono.mono_set_assemblies_path.argtypes = [ctypes.c_char_p]
    mono.mono_set_assemblies_path(paths.encode("utf-8"))
    mono.mono_set_dirs.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    mono.mono_set_dirs(str(managed).encode("utf-8"), str(root / "Mono/etc").encode("utf-8"))
    mono.mono_jit_init_version.argtypes = [ctypes.c_char_p, ctypes.c_char_p]
    mono.mono_jit_init_version.restype = ctypes.c_void_p
    domain = mono.mono_jit_init_version(b"VaMCompilerWorker", b"v2.0.50727")
    mono.mono_domain_assembly_open.argtypes = [ctypes.c_void_p, ctypes.c_char_p]
    mono.mono_domain_assembly_open.restype = ctypes.c_void_p
    assembly = mono.mono_domain_assembly_open(domain, str(executable).encode("utf-8"))
    if not assembly:
        raise RuntimeError("Original Mono did not load compiler worker")
    argv = [str(executable), *sys.argv[2:]]
    native = (ctypes.c_char_p * len(argv))(*(x.encode("utf-8") for x in argv))
    mono.mono_jit_exec.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.POINTER(ctypes.c_char_p)]
    mono.mono_jit_exec.restype = ctypes.c_int
    return mono.mono_jit_exec(domain, assembly, len(argv), native)


def main() -> int:
    # Reserve the parent's binary pipe before loading Mono or its CRT.
    # All ordinary stdout (Python, managed Console, Win32 and CRT diagnostics)
    # goes to stderr; only the worker's explicit FileStream uses this handle.
    reply_fd = os.dup(sys.stdout.fileno())
    try:
        reply_handle = msvcrt.get_osfhandle(reply_fd)
        os.environ["VAM_COMPILER_REPLY_HANDLE"] = str(reply_handle)
        sys.stdout.flush()
        os.dup2(sys.stderr.fileno(), sys.stdout.fileno())
        kernel = ctypes.WinDLL("kernel32", use_last_error=True)
        kernel.SetStdHandle.argtypes = [wintypes.DWORD, wintypes.HANDLE]
        kernel.SetStdHandle.restype = wintypes.BOOL
        if not kernel.SetStdHandle(0xFFFFFFF5, msvcrt.get_osfhandle(sys.stderr.fileno())):
            raise ctypes.WinError(ctypes.get_last_error())
        return run_mono()
    finally:
        os.close(reply_fd)


if __name__ == "__main__":
    raise SystemExit(main())
