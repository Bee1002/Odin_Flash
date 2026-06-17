# Odin Flash — instrucciones para asistentes

- WPF (.NET Framework 4.8) + Material Design 3. Flasher Samsung Download Mode (LOKE/Odin).
- **Core del protocolo:** `Lib/OdinFlash.Protocol/Library/` — no refactorizar sin prueba en hardware.
- **UI:** `window/Main.xaml.cs`, `Controls/Flash.xaml.cs`, `Controls/FlashField.xaml.cs`.
- **Build oficial:** `tools/Build-Production.ps1` (opciones `-Obfuscate`, `-Zip`, `-Installer`).
- **Iconos:** `Assets/source_icon.png` → `tools/Convert-PngToIcon.ps1`.
- Licencia: MIT (`LICENSE` en la raíz).
