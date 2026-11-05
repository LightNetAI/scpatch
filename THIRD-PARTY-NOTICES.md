# Third-party notices

## dnlib

`bin/dnlib.dll` is redistributed unmodified.

- Project: https://github.com/0xd4d/dnlib
- License: MIT
- Copyright (c) 2012-2023 0xd4d

```
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

If you prefer not to use the bundled copy, drop any dnlib build into `bin/` and
`build.sh` will link against it.

## ScreenConnect

This repository contains **no** ConnectWise code or binaries. `scpatch` only
rewrites a file that you supply, on a machine you administer, and the helper
assembly it deploys (`ScreenConnect.SqlFixer.dll`) is original code from this
project. ScreenConnect itself remains governed by your ConnectWise licence.
