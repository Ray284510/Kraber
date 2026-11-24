"""Large durable history must not overflow the official CLI event-line guard."""
import hashlib
import json
from pathlib import Path
import tempfile
import unittest

from .stage_mcp import ROOT, checkpoint_receipt


class CheckpointReceiptTests(unittest.TestCase):
    def test_large_history_is_retained_but_not_transmitted(self):
        with tempfile.TemporaryDirectory(dir=ROOT / 'tmp') as folder:
            path = Path(folder) / 'stage.complete.json'
            full = {'stage': 'bounded', 'operations': [
                {'id': 'op-' + str(i), 'state': 'succeeded', 'evidence': 'x' * 100}
                for i in range(2000)], 'unresolved': [],
                'nx_session': {'process_id': 42, 'any_modified': False}}
            raw = json.dumps(full, indent=2).encode()
            path.write_bytes(raw)
            receipt = checkpoint_receipt(path)
            self.assertEqual(receipt['operation_count'], 2000)
            self.assertEqual(receipt['sha256'], hashlib.sha256(raw).hexdigest())
            self.assertFalse(receipt['geometry_pass'])
            self.assertFalse(receipt['geometry_gate'])
            self.assertEqual(receipt['geometry_status'],'NOT_EVALUATED_CHECKPOINT_ONLY')
            self.assertEqual(path.read_bytes(), raw)
            self.assertEqual(checkpoint_receipt(path), receipt)
            event = {'type': 'item.completed', 'result': json.dumps(receipt)}
            self.assertLess(len(json.dumps(event).encode()), 8192)


if __name__ == '__main__':
    unittest.main()
