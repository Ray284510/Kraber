"""Read exact manifest-listed research files; no shell, credentials or arbitrary paths."""
import hashlib
import json
from pathlib import Path


def read_resource(folder,resource,start_line=1,limit=120,needle=''):
    manifest=json.loads((Path(folder)/'research-resources.json').read_text(encoding='utf-8'))
    if resource not in manifest:raise ValueError('Resource is not in this task manifest')
    if not 1<=start_line or not 1<=limit<=180 or len(needle)>160:
        raise ValueError('Invalid bounded read')
    entry=manifest[resource];path=Path(entry['path'])
    raw=path.read_bytes()
    if hashlib.sha256(raw).hexdigest()!=entry['sha256']:
        raise ValueError('Resource changed; coordinator must refresh the handoff')
    lines=raw.decode('utf-8-sig',errors='replace').splitlines()
    if needle:
        matches=[i+1 for i,line in enumerate(lines) if needle.casefold() in line.casefold()]
        return {'resource':resource,'path':str(path),'sha256':entry['sha256'],
                'matches':[{'line':i,'text':lines[i-1][:1400]} for i in matches[:50]],
                'total_matches':len(matches),'provenance':'Actual local file text, not runtime validation'}
    chosen=lines[start_line-1:start_line-1+limit]
    text='\n'.join(f'{start_line+i}: {line}' for i,line in enumerate(chosen))
    if len(text)>30000:raise ValueError('Request fewer lines')
    return {'resource':resource,'path':str(path),'sha256':entry['sha256'],
            'total_lines':len(lines),'text':text,'provenance':'Actual local file text, not runtime validation'}
