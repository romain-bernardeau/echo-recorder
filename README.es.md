<p align="center"><a href="README.md">English</a> · <a href="README.fr.md">Français</a> · <b>Español</b></p>

# Echo

Echo graba sus reuniones de Teams (o presenciales) en Windows, transcribe quién dijo qué y redacta
un acta lista para enviar. Ningún bot se une a la reunión: Echo capta el audio del PC y lee los
nombres de los participantes directamente en la ventana de Teams.

![Echo, pantalla de inicio](assets/screenshots/home-en.png)

## Qué hace Echo

- **Grabación** de dos pistas (su micrófono y el audio de los demás participantes), iniciada a
  mano, con un atajo de teclado o al detectar una reunión de Teams. Un pequeño widget permanece en
  pantalla durante la reunión: notas con marca de tiempo, capturas de pantalla, marcadores.
- **Transcripción** con Mistral (Voxtral) o Cloudflare Workers AI (Whisper), con los oradores
  identificados gracias a los nombres leídos en Teams.
- **Acta** (.eml lista para abrir en Outlook, .docx, .md) y un resumen breve, con plantillas de
  acta personalizables y un vocabulario profesional. Las reuniones largas se resumen por partes
  automáticamente.
- **Importación** de un archivo de audio o de una transcripción de Teams (.docx / .vtt).
- **Búsqueda** en todas las reuniones, misiones, gestión del almacenamiento, actualizaciones
  automáticas.

![Echo, acta generada](assets/screenshots/minutes-en.png)

![Echo, widget de grabación](assets/screenshots/widget-en.png)

## Privacidad

Las grabaciones permanecen en su PC. Solo el audio que se va a transcribir y el texto que se va a
resumir se envían al proveedor de IA que usted elija, con **su propia clave API**, guardada cifrada
(DPAPI de Windows). Avise a los participantes de que la reunión se está grabando.

## Instalación

La publicación de versiones en [Releases](../../releases), con un instalador descargable, está en
preparación. Echo se instala en su perfil de usuario (`%LOCALAPPDATA%\Programs\Echo`), sin
derechos de administrador, y se actualiza solo. En el primer inicio, pegue su clave API en
Ajustes > Proveedores: los modelos marcados como **Free** funcionan con una clave gratuita.

Requisitos: Windows 10 u 11 con WebView2, Microsoft Teams (nueva versión). La interfaz está
disponible en francés e inglés.

## Compilar

```powershell
dotnet build Echo.sln -c Release
dotnet test tests/Echo.Tests/Echo.Tests.csproj
```

.NET 8 SDK, WPF + WebView2 (interfaz en `ui/`), NAudio (captura WASAPI), FlaUI (lectura de Teams).
Ver `docs/ARCHITECTURE.md`. `ffmpeg.exe` debe estar en `bin/` junto al exe
(`tools/get-ffmpeg.ps1` lo descarga).

## Comentarios

Los errores y las ideas son bienvenidos en [Issues](../../issues).

## Licencia

Copyright (C) 2026 Echo contributors.

Echo es software libre bajo la licencia GNU GPL v3 o posterior (SPDX: `GPL-3.0-or-later`), ver
`LICENSE`. Componentes de terceros: `THIRD-PARTY-NOTICES.md`.
