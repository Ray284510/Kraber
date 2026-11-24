"""Experimental, pure offline receipt helpers. No NX, IO or release decisions.

An ordered path inventory proves recorded child order, not geometric closure.
Nearest recorded samples are deliberately not called curve projections.
"""
from math import dist, isfinite
from fractions import Fraction
from itertools import product


def edge_use_orientation(loop_to_fin, fin_to_edge):
    """NX12 uf_brep_types.h: both slots describe EDGE traversal in the Loop.

    Fin is always forward relative to its Loop. If both slots are non-NONE,
    they must agree. These are duplicate constraints, not signs to multiply.
    Geometry sense and UV parameter direction are independent evidence.
    """
    raw = dict(loop_to_fin=loop_to_fin, fin_to_edge=fin_to_edge)
    if any(x not in (-1, 0, 1) for x in raw.values()):
        return dict(raw=raw, status='UNKNOWN', traversal=None)
    values = {x for x in raw.values() if x != 0}
    if len(values) == 2:
        return dict(raw=raw, status='INCONSISTENT_PUBLIC_CONTRACT', traversal=None)
    return dict(raw=raw, status='AGREEMENT_NOT_MULTIPLICATION' if values else 'UNKNOWN',
                traversal=next(iter(values), None))


def ordered_loop_inventory(records):
    """Validate complete recorded child paths; retain recorded order and uses.

    Schema is the P03/P04 native `topology_use` receipt. This adapter supports
    loop->fin->edge paths only. Unsupported singular/direct-edge loops stop.
    Repeated edge IDs in distinct uses are permitted; repeated use paths aren't.
    """
    rows = [r for r in records if r.get('kind') == 'topology_use']
    paths = {}
    for r in rows:
        path = r['path']
        key = tuple((p['id'], p['child_index']) for p in path)
        if key in paths:
            raise ValueError('Duplicate topology use path')
        paths[key] = r
    for key, r in paths.items():
        if len(key) > 1:
            if key[:-1] not in paths or r['path'][:-1] != paths[key[:-1]]['path']:
                raise ValueError('Missing or inconsistent parent path')
        children = [v for k, v in paths.items() if len(k) == len(key)+1 and k[:-1] == key]
        if sorted(c['path'][-1]['child_index'] for c in children) != list(range(r['children'])):
            raise ValueError('Incomplete or duplicate child indices')
    loops = []
    for key, row in paths.items():
        if row['path'][-1]['type'] != 6:
            continue
        fin_rows = sorted((r for k, r in paths.items() if len(k) == len(key)+1 and k[:-1] == key),
                          key=lambda r: r['path'][-1]['child_index'])
        uses = []
        for f in fin_rows:
            if f['path'][-1]['type'] != 7 or f['children'] != 1:
                raise ValueError('Unsupported non-Fin/singular Loop')
            es = [r for r in rows if r['path'][:-1] == f['path']]
            if len(es) != 1 or es[0]['path'][-1]['type'] != 8:
                raise ValueError('Missing Fin Edge')
            e = es[0]
            direction = edge_use_orientation(f['path'][-1]['orientation'], e['path'][-1]['orientation'])
            if direction['status'] == 'INCONSISTENT_PUBLIC_CONTRACT':
                raise ValueError('Conflicting traversal orientations')
            uses.append(dict(path=e['path'], fin_id=f['path'][-1]['id'], edge_id=e['path'][-1]['id'],
                             edge_tag=e['path'][-1]['tag'], child_index=f['path'][-1]['child_index'],
                             traversal_orientation=direction['traversal'],
                             raw_orientations=direction['raw'], orientation_composition=direction['status'],
                             geometric_sense=None, native_use_interval=None))
        loops.append(dict(path=row['path'], face_tag=row['face'], loop_id=row['path'][-1]['id'],
                          ordered_uses=uses, loop_type=None, geometric_closure='NOT_PROVEN'))
    return loops


def periodic_uv_residual(a, b, periods):
    """Nearest lattice shifts for supplied decimal values, retaining exact ties.

    Unique results keep the P06 shape. Ambiguous results have shifts=None and
    all tied shift vectors. This does not bound uncertainty of native inputs.
    """
    if len(a) != len(b) or len(a) != len(periods):
        raise ValueError('Chart dimension mismatch')
    choices = []
    for x, y, p in zip(a, b, periods):
        if not all(isfinite(v) for v in (x, y, p)) or p < 0:
            raise ValueError('Invalid chart value/period')
        delta = Fraction(str(x))-Fraction(str(y));period = Fraction(str(p))
        if not period:
            choices.append([(0, delta)])
            continue
        lower = delta//period
        trial = [(k, delta-k*period) for k in (lower, lower+1)]
        minimum = min(abs(d) for _,d in trial)
        choices.append([(k,d) for k,d in trial if abs(d)==minimum])
    candidates = [dict(shifts=[k for k,d in items], residual=float(sum(d*d for k,d in items))**.5)
                  for items in product(*choices)]
    if len(candidates)==1:
        return candidates[0]
    return dict(residual=candidates[0]['residual'], shifts=None,
                status='AMBIGUOUS_PERIODIC_SHIFT', candidates=candidates)


def nearest_recorded_samples(xyz, samples, tie_mm=1e-12):
    """Finite target-cloud lookup, NEVER a curve nearest-point calculation."""
    if not samples:
        return dict(status='NO_RECORDED_TARGET', candidates=[], curve_parameter_map=None)
    distances = [(dist(xyz, s['xyz']), s) for s in samples]
    d = min(x[0] for x in distances)
    return dict(status='NEAREST_RECORDED_SAMPLE_ONLY', distance_to_recorded_sample_mm=d,
                candidates=[dict(distance_mm=a, sample=b) for a, b in distances if a <= d+tie_mm],
                curve_parameter_map=None, global_minimum_proven=False)
