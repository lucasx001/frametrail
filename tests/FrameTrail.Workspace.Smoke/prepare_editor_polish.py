import json
import sys
from pathlib import Path

root = Path(sys.argv[1])
project = json.loads((Path(__file__).parents[1] / 'FrameTrail.Core.Tests/fixtures/project-v1.json').read_text(encoding='utf-8-sig'))
time = lambda ticks: {'ticks': ticks, 'timescale': 10000000}
project.update(projectId='editor-polish', revision=1, sourceDuration=time(40000000), effects=[], markers=[])
project['media'] = [
    dict(id='screen', kind='screen', path='media/screen.mp4', duration=time(40000000), startOffset=time(0), width=640, height=360),
    dict(id='system', kind='systemAudio', path='media/system.wav', duration=time(40000000), startOffset=time(0), sampleRate=48000, channels=2),
    dict(id='pointer', kind='pointerEvents', path='events/pointer.jsonl', duration=time(40000000), startOffset=time(0)),
]
project['segments'] = [dict(id='clip', sourceStart=time(0), sourceDuration=time(40000000), rate=dict(numerator=1, denominator=1))]
project['canvas'].update(width=640, height=360, padding=0.05)
project['canvas']['camera']['enabled'] = False
project['export'].update(width=640, height=360, fps=dict(numerator=30, denominator=1), videoCodec='h264')
project.pop('editor', None)
(root / 'project.json').write_text(json.dumps(project), encoding='utf-8')
events = []
for i in range(241):
    ticks = i * 40000000 // 240
    x = 0.25 if ticks < 30000000 else 0.7
    events.append(dict(type='pointer', ticks=ticks, timescale=10000000, x=x, y=0.4, visible=True, inside=True, shape=0, kind=1 if i == 30 else 2 if i == 31 else 0))
(root / 'events/pointer.jsonl').write_text('\n'.join(map(json.dumps, events)), encoding='utf-8')
