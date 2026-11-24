"""Validate a dimensioned mechanism plan before creating any NX file."""
import json
import math
from pathlib import Path
from jsonschema import Draft202012Validator


def validate_plan(plan):
    json.dumps(plan, allow_nan=False)
    schema = json.loads(Path(__file__).with_name('mechanism.schema.json').read_text(encoding='utf-8'))
    Draft202012Validator(schema).validate(plan)
    if plan['units'] != {'length': 'mm', 'angle': 'degree', 'time': 's'}:
        raise ValueError('This bridge plan requires explicit mm/degree/s units')
    drive = plan['drive']
    if not 0 < drive['duration_s'] <= 3600 or not isinstance(drive['steps'], int) or not 1 <= drive['steps'] <= 10000:
        raise ValueError('Bounded duration and native step count required')
    names = set()
    for joint in plan['joints']:
        name = joint.get('name')
        if not name or name in names or joint['first'] == joint['second']:
            raise ValueError('Unique joint names and distinct connected links required')
        names.add(name)
        if not all(isinstance(x, (int, float)) and math.isfinite(x) for x in joint['origin'] + joint['axis']):
            raise ValueError('Finite joint coordinates and axes required')
        if math.sqrt(sum(x*x for x in joint['axis'])) < 1e-9:
            raise ValueError('Nonzero joint axis required')
    return plan
