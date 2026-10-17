import json
import hashlib
import subprocess
import sys
from pathlib import Path
import numpy as np

root = Path(sys.argv[1])
cases = json.loads((root / 'cursor-cases.json').read_text())
results = []
def pixels(name):
    file = root / (name + '.png')
    info = json.loads(subprocess.check_output(['ffprobe', '-v', 'error', '-show_entries', 'stream=width,height', '-of', 'json', str(file)]))['streams'][0]
    raw = subprocess.check_output(['ffmpeg', '-v', 'error', '-i', str(file), '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-threads', '1', '-'])
    return np.frombuffer(raw, np.uint8).reshape(info['height'], info['width'], 3).astype(np.int16)
for case in cases:
    assert case['ticks'] * 30 % 10000000 == 0, 'Compare matching output frame times'
    png = pixels(case['name']).astype(np.float32)
    frame = case['ticks'] * 30 // 10000000
    raw = subprocess.check_output(['ffmpeg', '-v', 'error', '-i', str(root / (case['name'] + '.mp4')), '-vf', f'select=eq(n\\,{frame})', '-frames:v', '1', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-threads', '1', '-'])
    decoded = np.frombuffer(raw, np.uint8).reshape(png.shape).astype(np.float32)
    error = float(np.abs(png - decoded).mean())
    assert error < 4, (case['name'], error)
    subprocess.run(['ffmpeg', '-v', 'error', '-i', str(root / (case['name'] + '.mp4')), '-f', 'null', '-'], check=True, capture_output=True)
    results.append(dict(name=case['name'], meanRgbError=error))
for a, b in [('recorded', 'dot'), ('recorded', 'crosshair'), ('recorded', 'ripple'), ('recorded', 'highlight'), ('offset', 'offset-base')]:
    assert np.max(np.abs(pixels(a) - pixels(b))) > 10, (a, b, 'no visible change')
assert np.array_equal(pixels('idle'), pixels('hidden')), 'idle cursor did not disappear'
groups = {}
for file in (root.parent / 'fixture.frametrailproject/cache').glob('editor-v2-*.png'):
    groups.setdefault(file.name.split('-')[2], []).append(file)
filmstrip = max(groups.values(), key=len)
assert len(filmstrip) >= 3 and len({hashlib.sha256(f.read_bytes()).hexdigest() for f in filmstrip}) >= 3, 'filmstrip repeated one representative frame'
(root / 'independent-media-verification.json').write_text(json.dumps(dict(success=True, cases=results, idleMatchesHidden=True, distinctFilmstripFrames=len(filmstrip)), indent=2))
print(json.dumps(dict(success=True, cases=len(results))))
