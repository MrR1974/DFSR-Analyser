# Third-party notices

DFSR Analyser is published under the MIT License (see `LICENSE`). The release zip also carries these components, each under its own licence. Their licence texts are in `app\licenses` in the release zip, which is installed as `licenses` in the program folder.

| Component | Used for | Licence |
|---|---|---|
| [.NET runtime and Windows Desktop runtime](https://github.com/dotnet/runtime) (incl. System.Diagnostics.EventLog, System.DirectoryServices.Protocols) | Runtime, event log and LDAP access | MIT |
| [DuckDB](https://duckdb.org) and [DuckDB.NET](https://github.com/Giorgi/DuckDB.NET) | The case database | MIT |
| [Apache Arrow for .NET](https://github.com/apache/arrow-dotnet) | Used by DuckDB.NET | Apache License 2.0 |
| [ScottPlot](https://scottplot.net) | Charts | MIT |
| [SkiaSharp](https://github.com/mono/SkiaSharp) and [HarfBuzzSharp](https://github.com/mono/SkiaSharp) | Chart rendering (ScottPlot) | MIT (Skia: BSD-3-Clause; HarfBuzz: Old MIT) |
| [OpenTK](https://opentk.net) and [GLWpfControl](https://github.com/opentk/GLWpfControl) | Used by ScottPlot.WPF | MIT |
| [GLFW](https://www.glfw.org) (OpenTK.redist.glfw) | Used by OpenTK | zlib |
| [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) | App view models | MIT |

DFSR Analyser's reading of the debug logs draws on Microsoft's *Understanding DFSR debug logging* series (AskDS blog, Ned Pyle, 2009). See `DFSR-Debug-Log-Reference.md` in the source repository.
