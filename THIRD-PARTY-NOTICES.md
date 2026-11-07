# Third-party notices

Semantic Bridge for Power BI is released under the MIT License (see [LICENSE](LICENSE)). The published `sbridge` binaries, the `semantic-bridge` .NET tool package and the Docker image include the third-party components listed below.

Versions are the versions pinned in `Directory.Packages.props`. Copyright and licence details come from each package's nuspec file. Assembly names are the files that ship in the published output.

## Microsoft Analysis Services client libraries (proprietary EULA)

These libraries are not covered by the MIT License. They are provided under the Microsoft Software License Terms.

- **Microsoft.AnalysisServices.AdomdClient 19.118.3** (ADOMD.NET). Copyright © Microsoft Corporation. Licence: Microsoft Software License Terms, https://go.microsoft.com/fwlink/?linkid=852895. Copy: [licenses/Microsoft.AnalysisServices.AdomdClient-EULA.rtf](licenses/Microsoft.AnalysisServices.AdomdClient-EULA.rtf). Assembly: `Microsoft.AnalysisServices.AdomdClient.dll`.
- **Microsoft.AnalysisServices 19.118.3** (Analysis Management Objects, AMO, and TOM). Copyright © Microsoft Corporation. Licence: Microsoft Software License Terms, https://go.microsoft.com/fwlink/?linkid=852989. Copy: [licenses/Microsoft.AnalysisServices-EULA.rtf](licenses/Microsoft.AnalysisServices-EULA.rtf). Assemblies: `Microsoft.AnalysisServices.dll`, `Microsoft.AnalysisServices.Core.dll`, `Microsoft.AnalysisServices.Tabular.dll`, `Microsoft.AnalysisServices.Tabular.Json.dll`.
- **Assemblies shared by both packages**: `Microsoft.AnalysisServices.Runtime.Core.dll` and `Microsoft.AnalysisServices.Runtime.Windows.dll`. Same licence as above.

### What the Microsoft licence terms mean for end users

The Microsoft Analysis Services client libraries are not covered by this project's MIT licence. They are provided under the Microsoft Software License Terms, and copies are in `licenses/`. By using the `sbridge` binaries, the `semantic-bridge` .NET tool package or the Docker image, you agree to those terms.

## Microsoft Identity Client (MSAL) and related packages (MIT)

Copyright © Microsoft Corporation. Licence: MIT, https://licenses.nuget.org/MIT.

- **Microsoft.Identity.Client 4.92.0**. Assembly: `Microsoft.Identity.Client.dll`.
- **Microsoft.Identity.Client.Broker 4.83.3**. Assembly: `Microsoft.Identity.Client.Broker.dll`. The project excludes the native `msalruntime` library from published output (`src/SemanticBridge/SemanticBridge.csproj`).
- **Microsoft.Identity.Client.NativeInterop 0.20.4**. Assembly: `Microsoft.Identity.Client.NativeInterop.dll`.
- **Microsoft.IdentityModel.Abstractions 8.14.0**. Assembly: `Microsoft.IdentityModel.Abstractions.dll`.

## Model Context Protocol C# SDK (Apache License 2.0)

- **ModelContextProtocol 2.3.1**, **ModelContextProtocol.Core 2.3.1** and **ModelContextProtocol.AspNetCore 2.3.1**. Copyright © Model Context Protocol a Series of LF Projects, LLC. Licence: Apache License 2.0, https://licenses.nuget.org/Apache-2.0.

## Other packages

- **System.CommandLine 2.0.14** (MIT). Copyright © Microsoft Corporation. Licence: https://licenses.nuget.org/MIT.
- **YamlDotNet 18.1.0** (MIT). Copyright © Antoine Aubry and contributors. Licence: https://licenses.nuget.org/MIT.
- **Microsoft.Extensions.AI.Abstractions 10.11.0** (MIT, transitive dependency of the MCP SDK). Copyright © Microsoft Corporation. Licence: https://licenses.nuget.org/MIT.

## Microsoft .NET runtime and ASP.NET Core (MIT)

The self-contained binaries include parts of the .NET runtime and the ASP.NET Core shared framework, version 10.0.14. Copyright © .NET Foundation and Contributors. Licence: MIT. The licence files are in the runtime packs, `Microsoft.NETCore.App.Runtime.*` and `Microsoft.AspNetCore.App.Runtime.*`.

## MIT and Apache licence texts

The MIT licence requires that copies include the copyright notice and the permission notice. The full MIT text is at https://licenses.nuget.org/MIT. The full Apache License 2.0 text is at https://licenses.nuget.org/Apache-2.0.
