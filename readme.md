# AXAML Tools for Visual Studio

A community-maintained fork of the MIT-licensed [AvaloniaVS](https://github.com/AvaloniaUI/AvaloniaVS) extension (from its `ARCHIVE` branch). It is **not** an official Avalonia product.

What it adds to Visual Studio:
- XAML previewer for `.axaml` files
- XAML IntelliSense and error tagging
- Avalonia project templates and snippets

**Targets:** Visual Studio 2022 17.14+ and Visual Studio 2026 (18.x), x64 and Arm64; Avalonia 11.x and 12.x.

It has its own VSIX identity and package GUIDs, so it doesn't collide with the official extension. Don't enable both at once: they register editors for the same file types.

The fork collects no usage data. The original extension's analytics service has been removed.

## Building

Requirements: Visual Studio 2022 17.14+ or 2026 with the **Visual Studio extension development** workload, plus the .NET 8 and .NET 10 SDKs.

```powershell
msbuild AvaloniaVS.sln -restore -p:Configuration=Release
dotnet test tests/CompletionEngineTests
```

The VSIX ends up at `AvaloniaVS.Vsix/bin/Release/AvaloniaVS.Vsix.vsix`.

## Debugging

Set **AvaloniaVS.Vsix** as the startup project and press F5. That starts the [experimental instance](https://learn.microsoft.com/visualstudio/extensibility/the-experimental-instance) (`/rootsuffix Exp`). Open an Avalonia project there and set breakpoints in this solution.

For problems inside Avalonia's previewer host itself, see Avalonia's guide on debugging the previewer.

## Contributing

This repository is source-available for transparency and so that you can fork it. It is **not open to contributions**.

- Pull requests will be closed without review.
- If you want to change something, fork the repository and maintain your own copy under the terms of the license.

## License

MIT. See [license.md](license.md).
