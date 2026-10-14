<p align="center"><a href="README.md">English</a> · <a href="README.fr.md">Français</a> · <b>Español</b></p>

# Ayuda de Echo

Echo graba tus reuniones de Teams (o presenciales) en Windows, transcribe quién dijo qué y redacta
un acta lista para enviar. Ningún bot entra en la reunión: todo pasa en tu PC.

> **Por ahora solo Windows y Microsoft Teams.** La interfaz está en inglés y en francés. En esta
> página, los nombres de botones y menús se dan en inglés, tal como aparecen en la aplicación.

**Índice**

- [Primeros pasos](#getting-started)
- [Grabar y el widget](#recording)
- [Después de la reunión](#after-the-meeting)
- [Obtener una clave API](#api-keys): [Mistral](#mistral) · [Cloudflare](#cloudflare) · [Google Gemini](#gemini) · [OpenRouter](#openrouter)
- [Solución de problemas](#troubleshooting)
- [Todos los ajustes de un vistazo](#settings)

<a id="getting-started"></a>

## Primeros pasos

<a id="install"></a>

### Instalar Echo

1. Descarga **[Echo-Setup.exe](https://github.com/romainio/echo-recorder/releases/latest/download/Echo-Setup.exe)**
   y haz doble clic en él.
2. Si Windows muestra «Windows protegió su PC», pulsa **«Más información»** y luego **«Ejecutar de
   todas formas»**. Este mensaje aparece con cualquier aplicación que aún no tiene firma digital.
3. Pulsa **«Instalar»**. Echo se instala en tu perfil (sin derechos de administrador), añade un
   acceso directo al menú Inicio y se abre.

A partir de ahí, Echo se actualiza solo. Para reinstalar, vuelve a ejecutar `Echo-Setup.exe`: tus
ajustes, claves y reuniones se conservan. Para desinstalar: **Configuración de Windows >
Aplicaciones > Aplicaciones instaladas > Echo**.

### Tres pasos

1. **Añade una clave API.** Solo una vez. Echo necesita la clave de un proveedor de IA para
   transcribir y redactar las actas. Elige uno en [Obtener una clave API](#api-keys), pega la clave
   en **Settings > Providers** y pulsa **"Test"**.
2. **Empieza a grabar.** Pulsa **"New recording"**, arriba en la barra lateral. Echo capta el sonido
   de la reunión de Teams, o tu micrófono en una reunión presencial.
3. **Para y recibe el acta.** Al final, pulsa **"Stop & generate minutes"**. Echo transcribe y luego
   redacta el acta. La encuentras en la página de la reunión.

### La ventana principal

**Barra de título**

- **Search** (`Ctrl K`): escribe un nombre y la lista de reuniones se filtra mientras escribes.
- **Sol / luna**: cambia entre tema claro y oscuro. El ajuste completo, con "System", está en
  Settings > General.
- **El botón de cerrar** no sale de Echo. La aplicación sigue en el **área de notificación**, junto
  al reloj, y sigue vigilando tus reuniones. Pulsa su icono para volver a abrir la ventana.

**Barra lateral**

- **New recording** empieza una reunión. Durante la grabación, el mismo botón la detiene.
- **Import** recupera una transcripción de Teams (.docx o .vtt) **o un archivo de audio** (.mp3,
  .m4a, .wav, .mp4 y otros). Echo crea la página de la reunión, el acta y un resumen.
- **Folder** crea una carpeta de misión (un nombre, un color) para ordenar tus reuniones.
- **Home** y **All meetings** abren la vista general y la lista completa.
- **Folders** lista tus misiones. Un clic filtra la lista por esa carpeta, "+" crea una nueva.
- **Open local folder** abre la carpeta de Windows con todas las grabaciones.
- **Tu nombre**, abajo del todo, abre **Settings**.

**Home**

- **Tres contadores**: reuniones, actas producidas, tiempo total grabado.
- **El banner de grabación** aparece durante una reunión: cronómetro, nombre de la reunión, botón
  **Widget** (recupera el widget flotante si estaba oculto) y **"Stop & generate minutes"**. Los
  avisos (micrófono, sonido) aparecen a la derecha.

**All meetings**

- **Filtros** por nombre, carpeta de misión, estado y periodo (7, 30 o 90 días).
- **Select** activa la selección múltiple: marca reuniones y luego **Generate transcript** (relanzar
  en lote) o **Delete**.
- **La papelera** de una tarjeta borra esa reunión y toda su carpeta.

| Estado | Qué significa |
|---|---|
| **Minutes** | Transcripción y acta generadas. La página está completa. |
| **Recorded** | Audio grabado, pero todavía sin transcripción o sin acta. |
| **Processing** | Transcripción o redacción en curso. |
| **Waiting for API** | El proveedor de IA estaba saturado (429) o caído. Echo reintenta solo a los 10 min, luego 30 y 60. No hay nada que hacer, el audio se conserva. |
| **Error** | Un paso falló por un motivo que no se arregla esperando (clave rechazada, modelo fuera de tu plan). La notificación da la causa. Relanza desde la página con "Generate minutes" o "Re-run transcription". |

<a id="recording"></a>

## Grabar y el widget

Durante la grabación aparece un banner en Home y un pequeño **widget flotante** queda encima de tus
ventanas. Lo tienes todo a mano sin salir de la reunión.

### El widget flotante

Una tarjeta pequeña con cronómetro, notas, momentos clave, capturas y botón de parada. Su cabecera
muestra el **nombre de la reunión** y su estado. Arrastra la cabecera para **moverlo** y la esquina
inferior derecha para **cambiar su ancho**. La altura se adapta al contenido.

**Botones de la cabecera**

| Botón | Qué hace |
|---|---|
| **Pin** | Mantiene el widget delante (por defecto). Otro clic lo deja pasar detrás de otras ventanas. |
| **Fold into rail** | Pliega el widget en una barra vertical fina. Útil cuando compartes pantalla. |
| **Hide** | Oculta el widget. La grabación continúa. El botón "Widget" del banner lo recupera. |

**Notas y última captura**

- **Tomar una nota**: pulsa "Click to type a note" (o el botón de nota, abajo), escribe y pulsa
  `Enter`. Cada nota va a `notes.md` y aparece en la pestaña "Notes & key moments" de la reunión.
- **Hora** (botón reloj): activo por defecto, añade a cada nota el minuto de la reunión ("12:40").
- **Vincular a la captura** (botón cadena): une la nota a la última captura (prefijo "[capture]").
- **La miniatura "Last capture"** aparece tras una captura. Un clic abre la imagen.

**Botones de abajo**

| Botón | Qué hace |
|---|---|
| **Notes** | Abre o cierra el panel de notas. |
| **Key moment ⚑** | Marca el instante con una nota "Key moment" con hora, para encontrar el pasaje después. |
| **Screenshot** | Captura la pantalla (las diapositivas compartidas). La imagen va a la pestaña **Screens**. |
| **Stop** | Detiene la grabación y lanza el procesamiento (transcripción, luego acta y resumen si las automatizaciones están activas). |

**Modo rail (widget plegado)**: una barra vertical de unos 50 px con un punto rojo que late, un asa
para moverla y cuatro botones: **Unfold**, **Screenshot**, **Key moment ⚑** y **Stop**.

### Detección y automatizaciones

Todo está en **Settings > Recording**.

- **Meeting detection**: Echo vigila Teams y te **propone** grabar en cuanto entras en una llamada.
- **Automatic recording**: sin preguntar, cada reunión de Teams detectada se graba. Al salir de la
  reunión, Echo se detiene solo tras un breve plazo y encadena transcripción, acta y resumen.
- **Automatic screenshots**: las diapositivas compartidas se capturan con regularidad.
- **Global keyboard shortcut**: empieza o detiene la grabación desde cualquier aplicación
  (`Ctrl+Alt+R` por defecto).

En una reunión **presencial**, sin Teams, Echo graba solo el micrófono. Pon el PC en el centro de la
mesa.

> El sonido de los participantes debe salir por **este PC** (altavoces o auriculares). Si sale por
> otro sitio (otro ordenador, una TV), Echo graba solo el micrófono y te avisa.

<a id="after-the-meeting"></a>

## Después de la reunión

Pulsa una reunión para abrir su **página**. Tiene pestañas: Summary, Transcript, Minutes, Screens,
Attendees y Notes.

### Cabecera de la página

- **El lápiz** junto al título cambia el nombre de la reunión (la página y los archivos).
- **El icono de carpeta** abre la carpeta de Windows de la reunión.
- **El menú "No folder"** guarda la reunión en una carpeta de misión.
- **Generate minutes** abre la pestaña Minutes y redacta el acta con la plantilla elegida.
- **Re-run transcription** rehace todo desde el audio. Útil tras cambiar el vocabulario o si una
  transcripción falló. El audio nunca se vuelve a grabar, solo se analiza de nuevo.
- **Share & export** abre el menú de archivos.

### Pestañas

- **Summary**: lo esencial en 5 a 10 viñetas cortas, fieles a la transcripción. **"Regenerate"** lo
  rehace, por ejemplo tras nombrar a los oradores.
- **Transcript**: el texto por turnos de palabra, cada uno con su hora y su orador, leído de la
  ventana de Teams. **Search** filtra, **Copy** copia todo. Las voces desconocidas aparecen como
  "Speaker N" con un lápiz: da el nombre real una vez y se aplica a todos sus turnos. **▶** reproduce
  el pasaje, y un reproductor arriba permite escuchar toda la reunión.
- **Minutes**: elige una **plantilla** y el **idioma** (el de la reunión, o francés, inglés, español,
  alemán), y pulsa **Generate**. Hay dos plantillas de base, **Detailed** y **Summary**, y puedes
  crear las tuyas en **Settings > Minutes** (pautas y un ejemplo a imitar). Formatos: .eml (borrador
  de Outlook), .md y .docx.
- **Screens**: todas las capturas en cuadrícula. El lápiz añade una nota vinculada.
- **Attendees**: Teams solo da **nombres**. Escribe el e-mail de una persona **una vez**: Echo lo
  recuerda para todas sus reuniones y lo usa en el borrador .eml.
- **Notes & key moments**: notas con hora, **momentos clave ⚑** y notas de capturas. **Edit** permite
  editar en Markdown y **Save** actualiza `notes.md`.

### Vocabulario profesional

En **Settings > Vocabulary**, lista las palabras que la transcripción entiende mal: clientes,
proyectos, siglas, herramientas. Guían el reconocimiento de voz y se corrigen en la transcripción y
en el acta. Una entrada por línea: el **término** solo, o **variante1|variante2=>Término**. El
botón **"Copy-paste prompt"** copia un prompt para que tu asistente de IA te prepare la lista. Un
vocabulario puede ser general o propio de una carpeta de misión ("Applies to").

### Share & export

- **Meeting folder**: todos los archivos, en el Explorador.
- **Minutes**: cada archivo generado (.eml, .md, .docx) se abre directamente.
- **Audio**: el MP3 de escucha de la reunión.
- **Claude folder (.md)**: una carpeta lista para analizar en un asistente de IA (contexto,
  transcripción corregida, notas).

### Importar una transcripción de Teams o un archivo de audio

El botón **Import** (barra lateral, o Settings > Providers > Microsoft Teams) acepta una
transcripción exportada de Teams (.docx o .vtt) o un **archivo de audio o vídeo** (.mp3, .m4a, .wav,
.aac, .wma, .ogg, .opus, .flac, .amr, .3gp, .mp4, .mov, .mkv, .webm, .avi). Echo crea la reunión, la
transcribe con tu proveedor y redacta el acta y el resumen. El archivo original nunca se modifica.
Con una sola pista, las voces se numeran ("Participant 0", "Participant 1"...) cuando el motor sabe
distinguirlas (Voxtral, Gemini).

<a id="api-keys"></a>

## Obtener una clave API

Echo envía el audio a transcribir y el texto a resumir al proveedor de IA que elijas, con **tu
propia clave**. La clave se guarda cifrada en tu PC (DPAPI de Windows).

| Proveedor | Transcripción | Acta, resumen, corrección | Oferta gratuita |
|---|---|---|---|
| [Mistral](#mistral) | Sí (Voxtral) | Sí | Sí, con límites |
| [Cloudflare Workers AI](#cloudflare) | Sí (Whisper) | Sí | Sí, cuota diaria |
| [Google Gemini](#gemini) | Sí | Sí | Sí, con límites |
| [OpenRouter](#openrouter) | No | Sí | Sí, modelos `:free` |

**Cómo funciona en Echo**

- Todo está en **Settings > Providers**. Elige el **Transcription engine** y el
  **Minutes & summary engine**. Pueden ser proveedores distintos.
- Pega cada clave, pulsa **"Save"** y luego **"Test"**. La línea bajo el botón dice si la clave
  funciona.
- Los modelos con la etiqueta **Free** usan la oferta gratuita del proveedor. Activa **"Show model
  details"** para ver y cambiar el modelo de cada tarea.
- **Automatic fallback** (activo por defecto): si un proveedor está saturado o rechaza el modelo,
  Echo pasa al otro proveedor que tenga clave.
- Consejo: configura dos proveedores (por ejemplo Mistral y Cloudflare). Si uno falla, el otro toma
  el relevo.

Precios, límites y pantallas de estos servicios cambian a menudo. Los pasos se comprobaron el
13 de octubre de 2026.

<a id="mistral"></a>

### Mistral

Transcripción con Voxtral, más acta, resumen y corrección.

**Crear la clave**

1. Ve a [console.mistral.ai](https://console.mistral.ai) y regístrate o inicia sesión.
2. Si te lo pide, elige un plan. El plan gratuito se llama **"Free mode"** (antes "Experiment").
3. Abre **"API Keys"** en el menú.
4. Pulsa **"Create new key"**. Dale un nombre (por ejemplo `Echo`), deja el espacio de trabajo y la
   caducidad por defecto, y pulsa otra vez **"Create new key"**.
5. Copia la clave enseguida. **Solo se muestra una vez.** Si la pierdes, crea otra.
6. En Echo: **Settings > Providers > Mistral**, pega la clave en **"Mistral API key"**, pulsa
   **"Save"** y luego **"Test"**.

Usa una clave de la consola API. Una clave creada para otros productos de Mistral (por ejemplo
"Vibe" o "Mistral Code") no sirve aquí.

**Oferta gratuita y límites**

- La documentación de Mistral no es coherente: una página dice que no hace falta tarjeta, otra que
  las claves quedan inactivas hasta añadir un medio de pago. Según tu cuenta, te pueden pedir una
  tarjeta o un número de teléfono.
- Tus límites están en la consola: **Admin Panel > API > Limits**.
- Voxtral en Free mode no está documentado. En nuestra prueba del 13 de octubre de 2026,
  `voxtral-mini` funcionó con una clave gratuita (60 peticiones por minuto), igual que
  `ministral-8b`, `ministral-3b` y `open-mistral-nemo`.
- Algunos modelos (por ejemplo `mistral-small` o `mistral-medium`) respondieron 429 con un límite de
  **0 peticiones por minuto**: no están en tu plan. Echo pasa entonces a un modelo **Free**.

**Privacidad**

- En el plan gratuito, Mistral puede usar tus datos para mejorar sus modelos. Puedes negarte en
  **Admin > Privacy > "Anonymous improvement data"**.
- En un plan de pago, tus datos no se usan para entrenar.

**Errores frecuentes**

| Error | Significado | Qué hacer |
|---|---|---|
| 401 | Clave errónea, revocada o caducada | Crea una clave nueva y pégala otra vez. |
| 402 | Hace falta un pago | Añade un medio de pago en la consola, o usa otro proveedor. |
| 429 | Demasiadas peticiones, o cuota alcanzada | Espera. Echo reintenta y luego pasa al otro proveedor. |
| 429 con "0 peticiones por minuto" | Modelo fuera de tu plan | Elige un modelo **Free**. Echo lo hace solo. |

<a id="cloudflare"></a>

### Cloudflare Workers AI

Transcripción con Whisper (`@cf/openai/whisper-large-v3-turbo`), más acta, resumen y corrección.
Cloudflare necesita **dos valores**: un **token API** y tu **Account ID**.

**Crear el token**

1. Ve a [dash.cloudflare.com](https://dash.cloudflare.com) y regístrate o inicia sesión.
2. Abre **Workers AI** (en "AI" del menú).
3. Pulsa **"Use REST API"**.
4. Pulsa **"Create a Workers AI API Token"**, revisa los permisos y pulsa **"Create API Token"**.
5. Pulsa **"Copy API Token"**. **Solo se muestra una vez.** Los tokens empiezan por `cfut_`.
6. En la misma página, copia tu **Account ID** ("Get Account ID"). Tiene 32 caracteres.
7. En Echo: **Settings > Providers > Cloudflare Workers AI**, pega el Account ID en
   **"Cloudflare account id"**, el token en **"Cloudflare API token"**, pulsa **"Save"** y luego
   **"Test"**.

Otra vía: **My Profile > API Tokens > Create Token**, y un token personalizado con los permisos
**Workers AI: Read** y **Workers AI: Edit**.

**Oferta gratuita y límites**

- El plan gratuito da **10 000 neurons al día**, que se reinician a las 00:00 UTC. Sin exceso posible
  en gratuito: agotada la cuota, las peticiones se rechazan hasta el día siguiente.
- Whisper turbo cuesta unos 46,63 neurons por minuto de audio. Según nuestro cálculo, son unos
  **214 minutos, es decir alrededor de 3 h 30 de audio al día**. Es una estimación: el acta y el
  resumen usan la misma cuota.
- Whisper no separa las voces. Echo atribuye los turnos según la pista (tú o los demás) y los
  nombres de Teams leídos durante la reunión.

**Privacidad**

Cloudflare indica que no usa tus datos para entrenar modelos, ni en gratuito ni en pago.

**Errores frecuentes**

| Código | Significado | Qué hacer |
|---|---|---|
| 3036 | Cuota del día agotada | Espera a las 00:00 UTC, o usa otro proveedor. |
| 3040 | Sin capacidad por ahora | Inténtalo más tarde. Echo reintenta solo. |
| 5035 | Modelo reservado a planes de pago | Elige un modelo **Free**. |
| 3006 | Petición demasiado grande | Echo envía el audio en trozos de 10 minutos. Si sigue pasando, relanza la transcripción. |
| 3042 | Modelo no válido | Revisa el nombre del modelo, o elige uno de la lista. |
| "Failed to decode audio file" | El modelo de transcripción no acepta el formato de audio de Echo | Usa `@cf/openai/whisper-large-v3-turbo` y luego "Re-run transcription". |

<a id="gemini"></a>

### Google Gemini

Transcripción, acta, resumen y corrección con una clave de Google AI Studio.

**Crear la clave**

1. Ve a [aistudio.google.com/apikey](https://aistudio.google.com/apikey) e inicia sesión con una
   cuenta de Google.
2. Pulsa **"Get API key"** si hace falta y luego **"Create API key"**.
3. En una cuenta nueva se crean un proyecto y una clave. Si no, elige un proyecto o importa uno con
   **"Import projects"**.
4. Copia la clave. Buena práctica: restríngela a la **Gemini API**.
5. En Echo: **Settings > Providers > Google Gemini**, pega la clave en
   **"Google API key (Gemini)"**, pulsa **"Save"** y luego **"Test"**.

Con una cuenta profesional (Google Workspace), el botón puede salir en gris si el administrador no
te dio los permisos. Pídeselos, o usa una cuenta personal.

**Oferta gratuita y límites**

- Gratis sin facturación. Los modelos flash llevan la etiqueta **Free** en Echo.
- Los límites son **por proyecto**, no por clave. Consúltalos en
  [aistudio.google.com/rate-limit](https://aistudio.google.com/rate-limit).
- Las cuotas diarias se reinician a medianoche, hora del Pacífico.

**Privacidad**

- Google indica que en la UE y el EEE las reglas de privacidad del servicio de pago (sin
  entrenamiento con tus datos) se aplican también al nivel gratuito. Una cláusula de las condiciones
  ("only Paid Services for API clients in the EEA") es ambigua. Si dudas, activa la facturación o
  usa otro proveedor.
- Fuera de esas regiones, los datos del nivel gratuito pueden servir para mejorar productos de
  Google.

**Errores frecuentes**

| Error | Significado | Qué hacer |
|---|---|---|
| 400 / 403 | Clave no válida o no autorizada para la Gemini API | Revisa las restricciones de la clave, o crea una nueva. |
| 429 | Cuota del proyecto alcanzada | Espera al reinicio, o cambia de motor. Echo pasa al otro solo. |

<a id="openrouter"></a>

### OpenRouter

Solo acta, resumen y corrección. **OpenRouter no transcribe**: combínalo con Mistral, Cloudflare o
Gemini para la transcripción.

**Crear la clave**

1. Ve a [openrouter.ai/keys](https://openrouter.ai/keys) y regístrate o inicia sesión.
2. Pulsa el botón para crear una clave ("Create API Key" o "Create Key", según la versión de la
   página).
3. Dale un nombre y elige un límite de crédito (o ninguno). Un límite es buena idea.
4. Copia la clave. **Solo se muestra una vez.**
5. En Echo: **Settings > Providers > OpenRouter**, pega la clave en **"OpenRouter API key"**, pulsa
   **"Save"** y luego **"Test"**.

**Oferta gratuita y límites**

- Los modelos cuyo nombre termina en `:free` no cuestan nada. Echo los muestra con la etiqueta
  **Free**.
- Modelos gratuitos: **20 peticiones por minuto**, y **50 al día** si compraste menos de 10 USD de
  créditos, o **1 000 al día** a partir de 10 USD.

**Privacidad**

Algunos modelos gratuitos funcionan en proveedores que pueden guardar o usar tus peticiones. Decides
qué aceptas en [openrouter.ai/settings/privacy](https://openrouter.ai/settings/privacy).

**Errores frecuentes**

| Error | Significado | Qué hacer |
|---|---|---|
| 503 o "No endpoints found matching your data policy" | Tus ajustes de privacidad bloquean todos los proveedores de este modelo gratuito | En openrouter.ai/settings/privacy, permite los proveedores gratuitos que pueden entrenar con los datos, o elige otro modelo. |
| 402 | Saldo negativo (incluso con modelos `:free`) | Añade crédito, o usa otro proveedor. |
| 429 | Cuota por minuto o por día alcanzada | Espera. Echo reintenta y luego pasa al otro proveedor. |

<a id="troubleshooting"></a>

## Solución de problemas

- **La transcripción falla.** Revisa la clave (Settings > Providers > **"Test"**) y tu cuota con el
  proveedor. El detalle está en `echo.log`, en `%LOCALAPPDATA%\Echo`.
- **Saturado (429).** La clave llegó a su límite o el servicio está cargado. Echo reintenta unos dos
  minutos, pasa al otro proveedor si tiene clave, y luego marca la reunión "Waiting for API" y
  reintenta a los 10, 30 y 60 minutos. Una transcripción ya hecha nunca se pierde.
- **"Model unavailable on this key's tier" (403).** Tu clave no tiene acceso a ese modelo. En
  Settings > Providers, activa "Show model details" y elige un modelo **Free**, o cambia de
  proveedor.
- **El botón rojo "API key missing · Help"** de la barra lateral. Falta una clave que necesitan los
  motores elegidos, o una llamada falló. Púlsalo para abrir esta ayuda.
- **Espacio en disco.** Settings > Storage muestra lo que pesan tus grabaciones. Una hora pesa
  alrededor de 1 GB mientras se graba, luego el audio se archiva en MP3. **"Clean up"** aplica ya las
  reglas de conservación. Transcripciones, actas, notas y MP3 de escucha nunca se borran.
- **"A recording is already running".** Una grabación a la vez. Si el widget desapareció, usa el
  atajo de teclado o el icono junto al reloj para parar.
- **Sin nombres de oradores.** Echo activa la opción de Teams necesaria en el primer arranque. Luego
  **cierra Teams del todo una vez** (clic derecho en el icono de Teams > Quit) y vuelve a abrirlo.
- **Participantes no detectados (vista compacta).** Si la reunión está reducida a la **pequeña
  ventana flotante**, Teams no muestra ni participantes ni quién habla. Mantén la **ventana de la
  reunión grande**, al menos de vez en cuando.
- **"Participants' audio unavailable".** El sonido de la reunión debe salir por **este PC**. La
  grabación sigue solo con el micrófono.
- **Falta tu voz.** Comprueba que el **micrófono** correcto era el predeterminado de Windows y que
  Teams no lo usaba en exclusiva. El audio se conserva: "Re-run transcription" lo intenta de nuevo.
- **La ventana desapareció.** Echo sigue en el **área de notificación**. Pulsa su icono.
- **Actualizaciones.** Al arrancar, Echo busca una versión nueva en GitHub y te la propone
  (Settings > General > "Check on startup"). **"Check now"** lo comprueba en el acto. Si algo falla
  tras una actualización, **"Previous version"** restaura la instalación anterior.

**Archivos producidos**: cada reunión tiene su carpeta en `%LOCALAPPDATA%\Echo\recordings` (botón
"Open local folder").

| Archivo | Contenido |
|---|---|
| **&lt;Título&gt; - Minutes.eml / .md / .docx** | El acta, lista para enviar desde Outlook ("Compte rendu" con la aplicación en francés) |
| **&lt;Título&gt; - Transcript.txt** | La transcripción por orador, corregida |
| **&lt;Título&gt; - Summary.md** | El resumen corto ("Résumé" en francés) |
| **&lt;Título&gt; - Audio.mp3** | La grabación de audio |
| **notes.md** | Tus notas y momentos clave con hora |
| **Captures/** | Las capturas de pantalla |
| **.echo/** (oculta) | Los archivos de trabajo de Echo: pistas brutas archivadas (mic.mp3 / system.mp3, para volver a transcribir), transcripción bruta, datos de la reunión, registros |

&lt;Título&gt; es el nombre de la reunión: renombrar la reunión renombra estos archivos. Las reuniones
anteriores se ordenan así en el primer arranque de Echo.

<a id="settings"></a>

## Todos los ajustes de un vistazo

| Settings > | Qué encuentras |
|---|---|
| **Account** | Tu nombre, que firma las actas. |
| **Recording** | Detección de reuniones, grabación automática, capturas automáticas, zona de captura (ventana de Teams o pantalla completa), atajo de teclado global (`Ctrl+Alt+R` por defecto). |
| **Transcription** | El motor en uso (elegido en Providers), el idioma de la reunión (Auto, francés o inglés), la transcripción automática, la corrección de vocabulario (LLM). |
| **Vocabulary** | Tus términos y correcciones, generales o por misión, importación de archivo, "Copy-paste prompt". |
| **Minutes** | El motor en uso, acta automática, idioma del acta, formatos (.eml, .md, .docx), plantilla por defecto, tus plantillas. |
| **Providers** | Motor de transcripción, motor de acta y resumen, Automatic fallback, una clave por proveedor con "Save" y "Test", el Account ID de Cloudflare, "Show model details" (el modelo de cada tarea, con la etiqueta **Free**), importación de transcripciones de Teams. |
| **Storage** | Espacio usado, borrado del audio tras N días, reuniones fallidas (días), conservar los WAV originales, "Clean up now". |
| **General** | Tema, idioma de la aplicación (francés o inglés), arranque con Windows, carpeta de grabaciones, actualizaciones ("Check on startup", "Check now", "Previous version"), ayuda y versión. |
