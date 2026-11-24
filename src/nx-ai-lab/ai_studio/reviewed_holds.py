"""Permit a distinct scoped stage after reviewed, non-executing research losses.

Never release a reservation, settle an unknown response, or replay its request.
This is an explicit coordinator disposition, not automatic network retry.
"""
import json


def allows_distinct_stage(db, folder, spec):
    requested = spec.get('reviewed_readonly_billing_holds', [])
    if not requested:
        return False
    if len(requested) != len(set(requested)):
        raise ValueError('Duplicate hold identities')
    rows = db.execute("SELECT * FROM api_requests WHERE state='unrecoverable_held'").fetchall()
    if {r['id'] for r in rows} != set(requested):
        raise ValueError('The reviewed hold list must match actual held requests')
    for row in rows:
        if row['task'] != folder.name or row['stage'] == spec['id']:
            raise ValueError('Only a distinct stage in the reviewed task may continue')
        original = json.loads((folder/'stages'/(row['stage']+'.json')).read_text(encoding='utf-8'))
        if original['allowed_commands'] or json.loads(row['payload'])['background'] is not False:
            raise ValueError('Lost request was not an isolated synchronous research request')
        decision = db.execute('SELECT decision FROM readonly_loss_dispositions WHERE id=?', (row['id'],)).fetchone()
        audit = db.execute('SELECT result FROM response_status_audits WHERE id=?', (row['id'],)).fetchone()
        charge = db.execute('SELECT state,amount FROM spend WHERE id=?', (row['id'],)).fetchone()
        if not decision or not audit or not charge:
            raise ValueError('Missing independently reviewed disposition')
        d = json.loads(decision[0])
        if not (d['nx_mutations_possible'] is False and d['consumer_exited'] is True
                and d['same_request_replay_forbidden'] is True
                and d['provider_terminal_status'] == 'unknown'
                and json.loads(audit[0]).get('http_status') == 404
                and charge['state'] == 'reserved'
                and charge['amount'] >= d['maximum_cost_retained_micro_usd'] > 0):
            raise ValueError('The isolated loss or retained cost is not proven')
    return bool(rows)
