"""Shared project-root resolution for NX AI Studio host tools."""
import os
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
if os.environ.get('NX_AI_LAB_ROOT') and Path(os.environ['NX_AI_LAB_ROOT']).resolve() != ROOT:
    raise RuntimeError('NX_AI_LAB_ROOT does not match this checkout; refusing mixed-root execution')

# Keep runtime scratch data with the selected checkout; no global Windows changes.
os.environ['NX_AI_LAB_ROOT'] = str(ROOT)
for name in ('TEMP', 'TMP', 'TMPDIR'):
    os.environ[name] = str(ROOT/'tmp')
import tempfile
tempfile.tempdir = str(ROOT/'tmp')

def lab_path(relative=''):
    candidate = ROOT / relative
    if not candidate.resolve().is_relative_to(ROOT):
        raise ValueError('Project path escapes its checkout')
    return candidate
