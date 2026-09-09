# Contributing

Requirements: Windows 10/11 x64, .NET 8 SDK and Visual Studio 2022 or another .NET-capable editor.

Before submitting a change run:

```powershell
dotnet restore DevBox.sln
dotnet build DevBox.sln -c Release
dotnet test DevBox.sln -c Release
```

Keep Windows/process/filesystem logic in `DevBox.Core`. UI code must call abstractions rather than launching or killing processes directly. Tests must use temporary directories and must not modify the real hosts file, certificate store or user services.
