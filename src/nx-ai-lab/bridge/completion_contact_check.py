"""Retain raw hits; only exact zero native intersection bodies can classify intended contact."""
async def check(c,frame,allowed):
    raw=await c.call(f'nx_motion_collision_check {frame}')
    result={'frame':frame,'raw':raw,'passed':False,'classification':'unverified'}
    hits=[p for p in raw['pairs'] if p['interferes']]
    if not hits:result.update(passed=True,classification='no_native_hit');return result
    volumes=await c.call(f'nx_motion_collision_volumes {frame}');result['native_intersections']=volumes
    expected={frozenset(p) for p in allowed}
    rows={frozenset([r['a'],r['b']]):r for r in volumes['intersections']}
    for h in hits:
        key=frozenset([h['a'],h['b']]);r=rows.get(key,{})
        if key not in expected or r.get('body_count')!=0 or r.get('volume_mm3')!=0 or 'error' in r:return result
    result.update(passed=True,classification='intended_contact_hit_zero_native_intersection_bodies')
    return result
