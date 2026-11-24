"""Persistent per-fault retry budget; an interrupted attempt still consumes its slot."""
import datetime
import json
import re
from pathlib import Path


class RepairBudget:
    def __init__(self, directory, fault, limit=3):
        if not re.fullmatch(r'[a-z0-9_-]+', fault) or not 1 <= limit <= 3:
            raise ValueError('Named fault and retry limit 1..3 required')
        self.directory = Path(directory) / fault
        self.directory.mkdir(parents=True, exist_ok=True)
        self.limit = limit

    def begin(self, diagnosis, change, before_model, failure_evidence):
        before_model, failure_evidence = Path(before_model), Path(failure_evidence)
        if not before_model.is_file() or not failure_evidence.is_file():
            raise ValueError('Preserved before-model and actual failure evidence required')
        for index in range(1, self.limit + 1):
            path = self.directory / f'attempt-{index}.json'
            try:
                with path.open('x', encoding='utf-8') as stream:
                    json.dump({'attempt': index, 'limit': self.limit,
                               'time': datetime.datetime.now().astimezone().isoformat(),
                               'diagnosis': diagnosis, 'planned_change': change,
                               'before_model': str(before_model.resolve()),
                               'failure_evidence': str(failure_evidence.resolve()),
                               'status': 'started_outcome_unconfirmed'}, stream, indent=2)
                return index
            except FileExistsError:
                continue
        raise RuntimeError('Repair limit reached; stop this fault and report evidence')

    def finish(self, index, actual_result, after_model, evidence, passed):
        attempt = self.directory / f'attempt-{index}.json'
        if not attempt.is_file():
            raise ValueError('Attempt was not started')
        after_model, evidence = Path(after_model), Path(evidence)
        if not after_model.is_file() or not evidence.is_file():
            raise ValueError('Actual after-model and result evidence required')
        with (self.directory / f'result-{index}.json').open('x', encoding='utf-8') as stream:
            json.dump({'attempt': index, 'time': datetime.datetime.now().astimezone().isoformat(),
                       'actual_result': actual_result, 'after_model': str(after_model.resolve()),
                       'evidence': str(evidence.resolve()), 'passed': bool(passed)}, stream, indent=2)

