"""Completion gate for native BDF logs; completion is not physical acceptance."""
import math
import re


def native_dynamics_completion(text, duration_s):
    times = re.findall(r'^\s*Current time\s*=\s*([-+0-9.eEdD]+)', text, re.M)
    actual = float(times[-1].replace('D', 'E').replace('d', 'e')) if times else None
    aborted = 'run aborted' in text.lower()
    footer = 'Total elapsed CPU time:' in text
    return {'passed': bool(actual is not None and math.isfinite(actual)
                           and abs(actual - duration_s) <= 1e-8 and footer and not aborted),
            'requested_duration_s': duration_s, 'native_final_time_s': actual,
            'native_abort_reported': aborted, 'native_final_statistics_present': footer,
            'scope': 'Native BDF completion only; trajectory, contact physics and geometry require separate checks'}
