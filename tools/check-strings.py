#!/usr/bin/env python3
"""Print every line of an expect file whose string is NOT in a binary. Exit 1 if any is missing.

    tools/check-strings.py <binary> <expect.txt>

Lines starting with '#' and blank lines are skipped. A "w:" prefix means UTF-16LE (a wide string
literal). Used by build-runtime.sh: a patch that never reached the compiler still builds, so each
patch set lists strings only its changes put into the DLL.
"""
import sys

data = open(sys.argv[1], 'rb').read()
missing = 0
for line in open(sys.argv[2], encoding='utf-8'):
    line = line.rstrip('\n')
    if not line or line.startswith('#'):
        continue
    needle = line[2:].encode('utf-16le') if line.startswith('w:') else line.encode()
    if data.find(needle) < 0:
        print(line)
        missing += 1
sys.exit(1 if missing else 0)
