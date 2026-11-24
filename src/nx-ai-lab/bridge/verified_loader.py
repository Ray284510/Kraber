"""Verify every loader replacement and pinned build input before generation."""
import ast,hashlib,json,os,sys,types
from pathlib import Path
ROOT=Path(__file__).resolve().parents[1]
MANIFEST=ROOT/'config/loader-build-lock.json'
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
class Rewrite(ast.NodeTransformer):
    def visit_Call(self,node):
        self.generic_visit(node)
        if isinstance(node.func,ast.Attribute) and node.func.attr=='replace':
            return ast.copy_location(ast.Call(ast.Name('checked_replace',ast.Load()),
                [ast.Constant(str(node.lineno)+':'+str(node.col_offset)+':'+str(node.end_col_offset)),node.func.value,*node.args],node.keywords),node)
        if isinstance(node.func,ast.Attribute) and isinstance(node.func.value,ast.Name) and node.func.value.id=='re' and node.func.attr=='sub':
            return ast.copy_location(ast.Call(ast.Name('checked_sub',ast.Load()),
                [ast.Constant(str(node.lineno)+':regex'),*node.args],node.keywords),node)
        return node
def generate(output,bootstrap=False):
    output=Path(output).resolve()
    if output in (ROOT,ROOT/'bridge') or not output.is_relative_to(ROOT/'tmp'):
        raise ValueError('Generation requires an isolated checkout tmp directory')
    output.mkdir(parents=True,exist_ok=True)
    manifest={} if bootstrap else json.loads(MANIFEST.read_text())
    if not bootstrap:
        for path,expected in manifest['dependencies'].items():
            if digest(Path(os.environ["UGII_BASE_DIR"])/"NXBIN"/"managed"/path)!=expected:raise ValueError('Dependency hash mismatch: '+path)
        if digest(Path(os.environ['WINDIR'])/manifest['compiler']['path'])!=manifest['compiler']['sha256']:
            raise ValueError('Compiler hash mismatch')
    seen={};inputs={}
    def resolve(rel=''):
        p=ROOT/rel
        if rel.replace('\\','/')=='bridge/NX12_Loader.cs':return output/'NX12_Loader.cs'
        if p.is_file():
            key=Path(rel).as_posix().replace('\\','/')
            inputs[key]=hashlib.sha256(p.read_text(encoding='utf-8-sig').encode('utf8')).hexdigest()
            if not bootstrap and manifest['inputs'].get(key)!=inputs[key]:
                raise ValueError('Build input hash mismatch: '+key)
        return p
    def count(site,n):
        seen[site]=n
        if not bootstrap and manifest['replacement_counts'].get(site)!=n:
            raise ValueError('Replacement count mismatch at '+site)
    def replace(site,value,old,new,*args):
        count(site,value.count(old))
        return value.replace(old,new,*args)
    def sub(site,pattern,repl,string,*args,**kwargs):
        import re
        count(site,len(list(re.finditer(pattern,string,kwargs.get('flags',0)))))
        return re.sub(pattern,repl,string,*args,**kwargs)
    module=types.ModuleType('lab_paths');module.lab_path=resolve
    previous=sys.modules.get('lab_paths')
    sys.modules['lab_paths']=module
    try:
        for relative in ('bridge/phase5_step_readonly_probe.cs', 'bridge/phase5_independent_nurbs_topology.cs', 'requirements-lock.txt',
                         'bridge/build.ps1', 'bridge/verified_loader.py', 'config/p13-context-v1.schema.json'):
            resolve(relative)
        source=resolve('bridge/adapt_loader.py')
        tree=ast.fix_missing_locations(Rewrite().visit(ast.parse(source.read_text(encoding='utf-8-sig'))))
        exec(compile(tree,str(source),'exec'),{'checked_replace':replace,'checked_sub':sub,'VERIFIED_BUILD':True})
    finally:
        if previous is None:sys.modules.pop('lab_paths',None)
        else:sys.modules['lab_paths']=previous
    result=dict(inputs=inputs,replacement_counts=seen,generated_sha256=digest(output/'NX12_Loader.cs'))
    if not bootstrap:
        if set(seen)!=set(manifest['replacement_counts']):raise ValueError('Replacement sites changed')
        if result['generated_sha256']!=manifest['generated_sha256']:raise ValueError('Generated source mismatch')
    (output/'generation-receipt.json').write_text(json.dumps(result,indent=2))
    return result
if __name__=='__main__':
    generate(sys.argv[1])
