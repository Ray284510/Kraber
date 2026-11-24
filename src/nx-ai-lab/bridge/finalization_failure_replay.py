"""Record existing failed native results without solving or granting acceptance."""
import asyncio
import datetime
import json
from pathlib import Path
import subprocess
import sys

from phase4_client import Client, ROOT, LOG


async def main(family, frame_csv):
    if not family.startswith(('finalization_','research_')):
        raise ValueError('Only fresh finalization experiments may be replayed here')
    run = json.loads((LOG / f'current-{family}.json').read_text())
    frames = [int(v) for v in frame_csv.split(',')]
    if not frames or min(frames) < 0 or max(frames) > run['plan']['drive']['steps']:
        raise ValueError('Frames outside declared solve range')
    simulation = str(Path(run['models']) / 'mechanism.sim')
    recording = Path(run['logs']) / ('failed_native_replay_' + datetime.datetime.now().strftime('%H%M%S'))
    evidence = {'acceptance': False, 'scope': 'Failure evidence only; native stored frames, not a fresh solve or full-cycle validation', 'frames': []}
    async with Client() as c:
        status = await c.call('nx_status')
        if status.get('part') and status['part'] != simulation:
            raise RuntimeError('Another part is open; no implicit close')
        if not status.get('part'):
            evidence['open'] = await c.call('nx_open_part ' + simulation)
        await c.call('nx_motion_setup')
        await c.call('nx_application motion')
        await c.call('nx_view iso .75')
        await c.call(f'nx_motion_frame Cycle {frames[0]}')
        proc = subprocess.Popen([sys.executable, str(ROOT / 'bridge/record_phase2.py'), str(recording), '8'], creationflags=subprocess.CREATE_NO_WINDOW)
        try:
            for _ in range(100):
                if (recording / 'ready.json').exists():
                    break
                if proc.poll() is not None:
                    raise RuntimeError('Recorder exited before first actual frame')
                await asyncio.sleep(.1)
            else:
                raise RuntimeError('Actual recorder not ready')
            for frame in frames:
                evidence['frames'].append(await c.call(f'nx_motion_frame Cycle {frame}'))
                await asyncio.sleep(5.2)
        finally:
            if recording.exists():
                (recording / 'stop').touch()
            proc.wait(timeout=15)
            await c.call('nx_motion_frame Cycle finish')
            await c.call('nx_save_part')
            if recording.exists():
                (recording / 'native-failure-replay.json').write_text(json.dumps(evidence, indent=2))
        print(json.dumps({'recording': str(recording), 'acceptance': False}))


if __name__ == '__main__':
    asyncio.run(main(*sys.argv[1:]))
