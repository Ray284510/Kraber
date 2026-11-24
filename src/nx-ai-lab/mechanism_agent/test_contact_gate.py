"""Offline gate tests; these are not NX contact evidence."""
import unittest,sys
from pathlib import Path
sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'bridge'))
from completion_contact_check import check

class Stub:
    def __init__(self,pair,volume=0,count=0,error=False):self.pair=pair;self.volume=volume;self.count=count;self.error=error
    async def call(self,cmd):
        if 'volumes' not in cmd:return {'pairs':[{'a':self.pair[0],'b':self.pair[1],'interferes':True}],'interference_count':1}
        r={'a':self.pair[0],'b':self.pair[1],'volume_mm3':self.volume,'body_count':self.count}
        if self.error:r['error']='native query failed'
        return {'intersections':[r]}
class ContactGateTests(unittest.IsolatedAsyncioTestCase):
    async def test_tiny_positive_is_rejected(self):
        self.assertFalse((await check(Stub(('CAM','ROLLER'),1e-15,1),0,[['CAM','ROLLER']]))['passed'])
    async def test_zero_volume_body_and_query_error_rejected(self):
        self.assertFalse((await check(Stub(('CAM','ROLLER'),0,1),0,[['CAM','ROLLER']]))['passed'])
        self.assertFalse((await check(Stub(('CAM','ROLLER'),error=True),0,[['CAM','ROLLER']]))['passed'])
    async def test_only_declared_contact_can_be_classified(self):
        self.assertTrue((await check(Stub(('ROLLER','CAM')),0,[['CAM','ROLLER']]))['passed'])
        self.assertFalse((await check(Stub(('GROUND','ROLLER')),0,[['CAM','ROLLER']]))['passed'])
