"""Offline guard tests only; no credentials, API requests or CAD execution."""
import json
import sqlite3
import tempfile
import unittest
from pathlib import Path
from .reviewed_holds import allows_distinct_stage


class ReviewedHoldsTests(unittest.TestCase):
    def setUp(self):
        root = Path(__file__).resolve().parents[1]/'tmp'
        root.mkdir(exist_ok=True)
        self.tmp = tempfile.TemporaryDirectory(dir=root)
        self.addCleanup(self.tmp.cleanup)
        self.folder = Path(self.tmp.name)
        (self.folder/'stages').mkdir()
        (self.folder/'stages/lost.json').write_text(json.dumps({'allowed_commands': []}))
        self.db = sqlite3.connect(':memory:'); self.db.row_factory = sqlite3.Row
        self.addCleanup(self.db.close)
        self.db.executescript('CREATE TABLE api_requests(id,task,stage,payload,state); CREATE TABLE readonly_loss_dispositions(id,decision); CREATE TABLE response_status_audits(id,result); CREATE TABLE spend(id,state,amount);')
        self.db.execute('INSERT INTO api_requests VALUES(?,?,?,?,?)', ('r',self.folder.name,'lost','{"background":false}','unrecoverable_held'))
        d = dict(nx_mutations_possible=False,consumer_exited=True,same_request_replay_forbidden=True,provider_terminal_status='unknown',maximum_cost_retained_micro_usd=800000)
        self.db.execute('INSERT INTO readonly_loss_dispositions VALUES(?,?)', ('r',json.dumps(d)))
        self.db.execute('INSERT INTO response_status_audits VALUES(?,?)', ('r','{"http_status":404}'))
        self.db.execute('INSERT INTO spend VALUES(?,?,?)', ('r','reserved',800000))
        self.spec = {'id':'new_stage','reviewed_readonly_billing_holds':['r']}

    def test_explicit_disposition_allows_distinct_stage_and_does_not_settle(self):
        self.assertTrue(allows_distinct_stage(self.db,self.folder,self.spec))
        self.assertEqual(self.db.execute('SELECT state FROM api_requests').fetchone()[0],'unrecoverable_held')
        self.assertEqual(tuple(self.db.execute('SELECT state,amount FROM spend').fetchone()),('reserved',800000))

    def test_no_implicit_opt_in(self):
        self.assertFalse(allows_distinct_stage(self.db,self.folder,{'id':'new'}))

    def test_same_stage_is_blocked(self):
        self.spec['id']='lost'
        with self.assertRaises(ValueError): allows_distinct_stage(self.db,self.folder,self.spec)

    def test_release_or_reduction_of_unknown_cost_blocks(self):
        self.db.execute('UPDATE spend SET amount=1')
        with self.assertRaises(ValueError): allows_distinct_stage(self.db,self.folder,self.spec)

    def test_lost_mutating_stage_blocks(self):
        (self.folder/'stages/lost.json').write_text('{"allowed_commands":["nx_create_part"]}')
        with self.assertRaises(ValueError): allows_distinct_stage(self.db,self.folder,self.spec)

    def test_new_unreviewed_hold_blocks(self):
        self.db.execute('INSERT INTO api_requests VALUES(?,?,?,?,?)', ('other',self.folder.name,'other','{"background":false}','unrecoverable_held'))
        with self.assertRaises(ValueError): allows_distinct_stage(self.db,self.folder,self.spec)

if __name__ == '__main__': unittest.main()
