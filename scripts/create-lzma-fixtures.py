"""Independent Python/liblzma reference vectors for the managed decoder."""
import base64
import hashlib
import json
import lzma
import pathlib
import random

root = pathlib.Path(__file__).resolve().parent.parent
random_source = random.Random(20261030)
random_bytes = bytes(random_source.randrange(256) for _ in range(20000))
cases = [
    ('Empty stream', b'', 3, 0, 2),
    ('UTF-8 text and repeated matches', ('VirtualHere USB: \u0442\u0435\u0441\u0442\n' * 500).encode('utf-8'), 3, 0, 2),
    ('All byte values', bytes(range(256)) * 30, 0, 0, 0),
    ('Overlapping long matches', b'A' * 100000, 4, 0, 4),
    ('Literal position contexts', bytes(range(128)) * 50, 0, 4, 0),
    ('Combined literal contexts', random_bytes[:2048] * 4, 2, 2, 1),
    ('Long-distance dictionary matches', random_bytes + random_bytes[:10000] + random_bytes, 3, 0, 2),
]
fixtures = []
for name, data, lc, lp, pb in cases:
    compressed = lzma.compress(data, format=lzma.FORMAT_RAW,
        filters=[dict(id=lzma.FILTER_LZMA1, dict_size=1 << 24, lc=lc, lp=lp, pb=pb)])
    fixtures.append(dict(Name=name, Lc=lc, Lp=lp, Pb=pb, OutputLength=len(data),
        Sha256=hashlib.sha256(data).hexdigest().upper(),
        CompressedBase64=base64.b64encode(compressed).decode('ascii')))
(root / 'tests/Vhui.Recovery.Tests/lzma-fixtures.json').write_text(
    json.dumps(fixtures, indent=2) + '\n', encoding='utf-8')
print('Generated %d independent LZMA vectors.' % len(fixtures))
