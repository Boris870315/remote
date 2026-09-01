# Remote

Cross-platform desktop application foundation for Windows and macOS.

## Technology

- C# 14 and .NET 10
- Avalonia 12 desktop UI
- MVVM with CommunityToolkit.Mvvm
- Layered application and protocol projects
- xUnit tests
- Rider and VS Code configuration
- GitHub Actions build and test workflow

## Projects

- `src/Remote.Desktop`: Avalonia UI and presentation state
- `src/Remote.Application`: business logic and use cases
- `src/Remote.Protocols`: protocol contracts and implementations
- `tests/Remote.Application.Tests`: automated tests
- `grill-me`: existing npm workspace

## Run

```bash
dotnet restore Remote.slnx
dotnet run --project src/Remote.Desktop
```

## Verify

```bash
dotnet build Remote.slnx
dotnet test Remote.slnx
npm run check
```

Open `Remote.slnx` in JetBrains Rider. In VS Code, install the recommended C# and Avalonia extensions, then use the included launch task.
