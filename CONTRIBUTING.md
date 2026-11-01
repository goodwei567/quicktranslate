# Contributing

## Development

1. Install the .NET 8 SDK.
2. Restore and build the x64 target:

```powershell
dotnet restore .\QuickTranslate.csproj -r win-x64
dotnet build .\QuickTranslate.csproj -p:Platform=x64 -p:RuntimeIdentifier=win-x64
```

3. Run `bin\x64\Debug\net8.0-windows10.0.26100.0\win-x64\QuickTranslate.exe`.
4. Keep a local Magpie service running when manually verifying translation.

## Pull requests

- Keep changes focused and preserve the existing WinUI design language.
- Do not commit `bin`, `obj`, published artifacts, local settings, logs, or credentials.
- Update `README.md` or `CHANGELOG.md` when user-facing behavior changes.
- Verify that the project builds with zero warnings and errors.

## Security

Never open an issue containing API keys, access tokens, personal settings, or private selected text. Revoke any credential that was exposed publicly before reporting the incident.
