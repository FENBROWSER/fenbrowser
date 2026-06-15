import json, re, sys
from collections import Counter

path = sys.argv[1]
typ = sys.argv[2] if len(sys.argv) > 2 else None
d = json.load(open(path))
fails = d.get('failures', [])
print('total', d.get('total'), 'passed', d.get('passed'), 'failures', len(fails))
sub = Counter()
msg = Counter()
sep = re.compile(r'[/\\]+')
selected = []
for f in fails:
    p = f.get('relativePath', '') or f.get('file', '')
    parts = sep.split(p)
    t = '?'
    if 'Temporal' in parts:
        i = parts.index('Temporal')
        t = parts[i+1] if i+1 < len(parts) else '?'
    sub[t] += 1
    det = (f.get('details') or '').strip().replace('\n', ' ')[:100]
    msg[det] += 1
    if typ and t == typ:
        selected.append((p, det))

print('--- by type ---')
for k, v in sub.most_common():
    print(v, k)
print('--- top messages ---')
for k, v in msg.most_common(20):
    print(v, '|', k)

if typ:
    print('--- %s sample (first 40) ---' % typ)
    for p, det in selected[:40]:
        short = sep.split(p)
        idx = short.index('Temporal') if 'Temporal' in short else 0
        print('/'.join(short[idx:]), '::', det)
