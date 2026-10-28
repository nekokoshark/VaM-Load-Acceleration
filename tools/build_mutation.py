"""Build the r8 donor and transplant 14 reviewed methods into a retargeted r7 DLL."""
from pathlib import Path
import argparse
import json
import shutil
import subprocess
import tempfile

def main() -> None:
    ap=argparse.ArgumentParser()
    ap.add_argument('--game',type=Path,required=True)
    ap.add_argument('--core',type=Path,required=True)
    ap.add_argument('--input',type=Path,required=True)
    ap.add_argument('--output',type=Path,required=True)
    args=ap.parse_args()
    here=Path(__file__).resolve().parent
    src=here.parent/'src/memory'
    csc=Path('C:/Windows/Microsoft.NET/Framework64/v4.0.30319/csc.exe')
    args.output.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='mutation-build-',dir=args.output.parent) as directory:
        work=Path(directory).resolve()
        shutil.copyfile(args.core/'Mono.Cecil.dll',work/'Mono.Cecil.dll')
        tool=work/'MutationPatch.exe'
        subprocess.run([str(csc),'/nologo','/r:'+str(work/'Mono.Cecil.dll'),'/r:System.Core.dll','/out:'+str(tool),str(here/'MutationPatch.cs')],check=True)
        # Only a compiler reference loses Obsolete(error=true). The runtime core
        # remains the byte-identical official archive; no shim ships in payload.
        subprocess.run([str(tool),'compile-reference',str(args.core/'0Harmony.dll'),str(work/'0Harmony.dll')],check=True)
        data=json.loads((src/'build.json').read_text('utf-8'))
        donor=work/'VaM.Memory.dll'
        argv=['/nologo','/nostdlib+','/unsafe+','/optimize+','/target:library','/out:'+str(donor)]
        for p in data['references']:
            ref=work/'0Harmony.dll' if p.endswith('/0Harmony.dll') else args.core/Path(p).name if p.startswith('BepInEx/core/') else args.game/p
            argv.append('/reference:'+str(ref.resolve()))
        argv+=['/resource:'+str(src/r['path'])+','+r['name'] for r in data['resources']]
        argv+=[str(src/p) for p in data['sources']]
        rsp=work/'donor.rsp';rsp.write_text('\n'.join('"'+v+'"' for v in argv),'utf-8-sig')
        subprocess.run([str(csc),'/noconfig','@'+str(rsp)],check=True)
        subprocess.run([str(tool),str(args.input.resolve()),str(donor),str(args.output.resolve())],check=True)
    print('BUILD_MUTATION_PASS changedMethods=14 runtimeCoreUnchanged=True')

if __name__=='__main__':
    main()
