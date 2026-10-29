# Desktop rendering library notices

The included rendering libraries use the components below.

## Windows

`MiniUI.Engine.dll` is a C# Native AOT library that includes parts of the
.NET 9 runtime. `MiniUI.Bridge.dll` contains P/Invoke declarations for the
Windows PowerShell host. Rendering, layout, scripting, and events are handled
by the rendering library.

Windows system libraries are loaded from the operating system and are not
included: user32, gdi32, kernel32, shell32, ole32, imm32, d2d1, dwrite,
winhttp, and psapi. Windows PowerShell 5.1 provides the host process.

## .NET runtime

Parts of the .NET runtime are statically linked into `MiniUI.Engine.dll` and
Xfer's `ui/MiniUI.App.dll`. The following license applies:

```
The MIT License (MIT)

Copyright (c) .NET Foundation and Contributors

All rights reserved.

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

The .NET runtime's `THIRD-PARTY-NOTICES.TXT` also applies to statically linked
components. Distributions should include the SDK's original notices as
`THIRD-PARTY-NOTICES.dotnet.txt`.

## Linux text libraries

These libraries are supplied by the operating system and are not included in
this repository.

| Library | Use | License |
| --- | --- | --- |
| HarfBuzz | Text shaping | Old MIT |
| FreeType | Font loading and glyph rasterization | FTL or GPLv2 |
| fontconfig | Font discovery and fallback | MIT-style |

## Chromium hand cursors

The embedded `hand_grab.cur` and `hand_grabbing.cur` resources come from
[Chromium ui/resources/cursors](https://chromium.googlesource.com/chromium/src/+/main/ui/resources/cursors/).
They are used for the CSS `grab` and `grabbing` cursors.

```
// Copyright 2015 The Chromium Authors
//
// Redistribution and use in source and binary forms, with or without
// modification, are permitted provided that the following conditions are
// met:
//
//    * Redistributions of source code must retain the above copyright
// notice, this list of conditions and the following disclaimer.
//    * Redistributions in binary form must reproduce the above
// copyright notice, this list of conditions and the following disclaimer
// in the documentation and/or other materials provided with the
// distribution.
//    * Neither the name of Google LLC nor the names of its
// contributors may be used to endorse or promote products derived from
// this software without specific prior written permission.
//
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS
// "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT
// LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR
// A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT
// OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
// SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT
// LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY
// THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
// (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
// OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

```
