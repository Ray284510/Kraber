"""Resolve exact occurrence reference names returned by the running NX session."""


async def joint_references(client, component, name):
    result = await client.call('nx_component_datums ' + component)
    refs = {}
    for field, suffix, prefix, key in [
        ('prototype_points', '_P', 'point:', 'point'),
        ('prototype_curves', '_A', 'curve:', 'axis'),
    ]:
        rows = [row for row in result[field]
                if row['name'].casefold() == (name + suffix).casefold()]
        if len(rows) != 1 or not rows[0]['occurrence_available']:
            raise RuntimeError(f'Unique live {key} occurrence unavailable: {component}/{name}')
        refs[key] = prefix + rows[0]['name']
    return refs
