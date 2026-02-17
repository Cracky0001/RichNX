![RichNX](./windows-client/src/SwitchDcrpc.Wpf/RNX.png)

RichNX zeigt Nintendo-Switch-Aktivität als Discord Rich Presence.

## Komponenten
- `Sysmodule` (Switch): stellt Telemetrie über HTTP bereit
- `Windows-Client` (RichNX): pollt `/state` und setzt Discord RPC

## Schnellstart
1. Sysmodule nach Atmosphere kopieren:
- `sd:/atmosphere/contents/00FF0000A1B2C3D4/exefs.nsp`
2. Switch neu starten.
3. Windows-Client starten (`RichNX.exe`).
4. In RichNX `Switch IP` eintragen und `Start` klicken.

## HTTP API
- `GET /state`
- `GET /debug`

Beispiel `/state`:
```json
{
  "service": "RichNX",
  "firmware": "21.2.0",
  "active_program_id": "0x01006F8002326000",
  "active_game": "Animal Crossing New Horizons",
  "battery_percent": 78,
  "is_charging": true,
  "is_docked": true,
  "started_sec": 12,
  "last_update_sec": 20
}
```

## Windows-Client
Standardwerte:
- `Port`: `6029`
- `RPC Name`: `Playing on Switch`
- `Poll (ms)`: `2000`

Wichtige Features:
- Discord IPC (`discord-ipc-0..9`)
- Titelauflösung (lokal + TitleDB)
- Tray-Modus + Single-Instance
- optionaler GitHub-Button
- optionaler Batterie-Status im RPC

## Build
Sysmodule:
```powershell
make
```

Windows-Client Build:
```powershell
dotnet build .\windows-client\src\SwitchDcrpc.Wpf\SwitchDcrpc.Wpf.csproj -c Release
```

Windows-Client Publish (Standalone EXE):
```powershell
dotnet publish .\windows-client\src\SwitchDcrpc.Wpf\SwitchDcrpc.Wpf.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o .\dist\windows-client\standalone-win-x64
```

Ausgabe:
- `dist/windows-client/standalone-win-x64/RichNX.exe`
- `dist/sysmodule/atmosphere/contents/00FF0000A1B2C3D4/exefs.nsp`

## Troubleshooting
- Discord zeigt nichts: Discord Desktop neu starten.
- `/state` nicht erreichbar: IP/Port prüfen (`6029`), Endpoint im Browser testen.
- Autostart funktioniert nicht: `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` prüfen.

## Author
- Cracky
- https://github.com/Cracky0001/RichNX

## Lizenz
GPL-3.0
