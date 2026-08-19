# Third-Party Notices

Monitor Switcher does not depend on or distribute a separate monitor-control application. Its source projects contain no NuGet `PackageReference`; monitor detection and topology changes use Windows system APIs.

## Microsoft .NET 8

Self-contained release archives include components of the Microsoft .NET runtime. The runtime package is licensed under the MIT License and includes components covered by its own third-party notices.

The complete notice distributed with this project is `DOTNET-RUNTIME-THIRD-PARTY-NOTICES.txt`. It is copied from `Microsoft.NETCore.App.Runtime.win-x64` 8.0.30 (`THIRD-PARTY-NOTICES.TXT`, SHA-256 `B60B2912DA28EAA6518593C9E2EFB5334EE062D3C42E80D8FDFA806B3DC52977`). Self-contained builds also resolve `Microsoft.WindowsDesktop.App.Runtime.win-x64` 8.0.30 under the same MIT licence (`LICENSE`, SHA-256 `A89886665765362EB77E0F8E26602C924520041D1711B2EEDC136434FE4D01AB`). Release automation verifies both runtime-pack versions, the complete Core runtime notice and the Windows Desktop licence provenance; a runtime-pack update therefore requires refreshing this documentation.

- Source and licence: https://github.com/dotnet/runtime
- Upstream .NET 8 third-party notices: https://github.com/dotnet/runtime/blob/release/8.0/THIRD-PARTY-NOTICES.TXT

Copyright (c) .NET Foundation and Contributors

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

Windows system libraries are supplied by Windows and are not redistributed as application components.
