import tempfile
import unittest
from pathlib import Path
from .repair import RepairBudget


class RetryChecks(unittest.TestCase):
    def test_interruption_and_restart_cannot_reset_budget(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1] / "work/phase4") as temp:
            p = Path(temp); model = p / 'before.prt'; evidence = p / 'nx-error.json'
            # Test fixtures only: never exposed as actual NX evidence.
            model.write_bytes(b'offline fixture'); evidence.write_text('{}')
            for i in range(1, 4):
                budget = RepairBudget(p, 'sample-fault')
                self.assertEqual(budget.begin('reason', 'change', model, evidence), i)
            with self.assertRaises(RuntimeError):
                RepairBudget(p, 'sample-fault').begin('reason', 'change', model, evidence)
            budget.finish(3, {'offline': True}, model, evidence, False)
            with self.assertRaises(FileExistsError):
                budget.finish(3, {'offline': True}, model, evidence, True)

    def test_missing_evidence_does_not_consume_attempt(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).resolve().parents[1] / "work/phase4") as temp:
            p = Path(temp); b = RepairBudget(p, 'sample-fault')
            with self.assertRaises(ValueError):
                b.begin('reason', 'change', p / 'missing.prt', p / 'missing.json')
            self.assertFalse(list(b.directory.glob('attempt-*')))


if __name__ == '__main__':
    unittest.main()
