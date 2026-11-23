"""Copy independently captured native messages into regression fixtures."""
import json
import pathlib
import shutil

project = pathlib.Path(__file__).resolve().parent.parent
trace = project / 'artifacts/native-capture/trace'
output = project / 'tests/Vhui.Recovery.Tests/Fixtures'
output.mkdir(parents=True, exist_ok=True)
messages = [json.loads(line) for line in (trace / 'trace.jsonl').read_text().splitlines()]
for name, direction, opcode in [
    ('native-capture-request.bin', 'client', 0x19),
    ('native-capture-reply.bin', 'server', 0x27),
    ('native-control-request.bin', 'client', 0x0d),
    ('native-control-reply.bin', 'server', 0x0e),
    ('native-device-info.bin', 'server', 0x10),
]:
    row = next(row for row in messages if row.get('Direction') == direction and row.get('Type') == opcode)
    shutil.copyfile(str(trace / row['File']), str(output / name))
    print(name)
(output / 'README.md').write_text(
    '# Native interoperability fixtures\n\n'
    'Captured from VirtualHere Server 4.8.8 and Client 6.0.3, Trust QHD Webcam '
    '(145F:02E7), via the development trace proxy. Files contain decoded 33-byte '
    'message headers and payloads, without the outer transport header.\n'
    'The native server executed the USB operations. These are not generated '
    'by the managed USB implementation under test.\n', encoding='utf-8')
