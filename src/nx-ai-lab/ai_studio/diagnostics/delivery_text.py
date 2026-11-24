"""Pure offline text/probe consistency checks. No filesystem or native dependencies."""
import json


def validate_text_bytes(name, data):
    text=data.decode('utf-8-sig',errors='strict')
    for index,c in enumerate(text):
        if (ord(c)<32 and ord(c) not in (9,10,13)) or ord(c)==127:
            raise ValueError(f'Unexpected ASCII control in {name} at character {index}: {ord(c)}')
    return text


def validate_probe_contract(expected, reference_json, readme, report, python_probe, launcher_probe):
    for name,value in [('expected',expected),('README',readme),('report',report),('python_probe',python_probe),('launcher_probe',launcher_probe)]:
        validate_text_bytes(name,value.encode('utf-8'))
    reference=json.loads(reference_json)
    path=reference.get('path')
    if not isinstance(path,str):raise ValueError('Missing Probe reference')
    validate_text_bytes('probe-reference decoded path',path.encode('utf-8'))
    if path!=expected or python_probe!=expected or launcher_probe!=expected or expected not in readme or expected not in report:
        raise ValueError('Probe path contract mismatch')
    return {'status':'PASS','exact_probe_path':expected,'no_controls':True}
