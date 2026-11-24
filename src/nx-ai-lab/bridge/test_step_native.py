from lab_paths import lab_path
import os,subprocess,json,datetime
from pathlib import Path
root=lab_path('');run=json.loads((root/'logs/current-final-validation.json').read_text(encoding='utf-8-sig'));log=Path(run['logs']);model=Path(run['models'])
env=dict(os.environ);nx=os.environ['NX_INSTALL_DIR']
env.update(UGII_BASE_DIR=nx,UGII_ROOT_DIR=nx+r'\UGII',UGII_TMP_DIR=str(root/'tmp'),TEMP=str(root/'tmp'),TMP=str(root/'tmp'),STEP214UG_DIR=nx+'\\STEP214UG\\',ROSE=nx+'\\STEP214UG\\',ROSE_DB=nx+'\\STEP214UG\\')
env['PATH']=nx+'\\NXBIN;'+env['PATH']
settings=(Path(nx)/'STEP214UG/step214ug.def').read_text().replace('CNV_MODE = On-Line','CNV_MODE = Off-Line')
for name in ('step214ugnullnx120_mm.prt','step214ugnullnx120_in.prt'):settings=settings.replace(name,str(Path(nx)/'STEP214UG'/name))
defs=log/'step-import-explicit.def';defs.write_text(settings)
out=model/'native_round2_base.prt';assert not out.exists()
cmd=[nx+r'\NXBIN\step214ug.exe',str(model/'joint_base.step'),'o='+str(out),'d='+str(defs),'l='+str(log/'native-round2.log')]
result=subprocess.run(cmd,env=env,cwd=str(root/'tmp'),capture_output=True,timeout=50,creationflags=subprocess.CREATE_NO_WINDOW)
(log/'native-round2-console.txt').write_bytes(result.stdout+result.stderr)
record={'time':datetime.datetime.now().astimezone().isoformat(),'argv':cmd,'exit_code':result.returncode,'output_exists':out.exists()}
(log/'native-round2-result.json').write_text(json.dumps(record,indent=2));print(json.dumps(record))
