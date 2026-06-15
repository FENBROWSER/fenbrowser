import json, sys
path = sys.argv[1]
needle = sys.argv[2]
limit = int(sys.argv[3]) if len(sys.argv) > 3 else 30
d = json.load(open(path))
n = 0
for f in d.get('failures', []):
    det = (f.get('details') or '')
    if needle in det:
        print(f.get('relativePath'), '::', det.strip().replace('\n', ' ')[:120])
        n += 1
        if n >= limit:
            break
print('shown', n)
