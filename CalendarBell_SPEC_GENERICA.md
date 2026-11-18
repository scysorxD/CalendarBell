# CalendarBell — Especificación funcional, técnica y de UX (v1.0)

> **Estado:** especificación cerrada, lista para implementar de principio a fin.
> **Fecha:** 18 de noviembre de 2026.
> **Plataforma destino:** Windows 11 x64, aplicación de escritorio por usuario.
> **Idioma de este documento:** español. **Textos iniciales de la aplicación:** inglés simple, con localización preparada para español.
> **Nombre de producto de trabajo:** `CalendarBell` (nombre e identificadores consistentes en código, instalador, tray y notificaciones).
>
> **Neutralidad del proyecto:** CalendarBell es una herramienta genérica para calendarios ICS. Usar únicamente calendarios ficticios (`Calendar A`, `Calendar B`) en ejemplos, fixtures, capturas, pruebas, documentación y commits. No incorporar nombres de empleadores, clientes, organizaciones ni proyectos reales.
> **Instrucción principal para la IA desarrolladora:** este documento es la fuente de verdad. Construí, probá, empaquetá y documentá una aplicación real y usable, no una demo. Todas las decisiones de producto necesarias están especificadas aquí. No solicites decisiones intermedias al usuario; ante un detalle menor no definido, elegí la alternativa más sencilla, segura y mantenible y documentala. No finjas que algo fue probado si no pudiste ejecutarlo.

---

## 0. Resumen ejecutivo

CalendarBell es una pequeña aplicación nativa de escritorio para Windows 11, residente **en segundo plano y sin ventana visible**, que lee **uno o varios calendarios publicados mediante URLs ICS/iCalendar de solo lectura**, obtiene sus reuniones y muestra **notificaciones nativas de Windows** con sonido **15 minutos antes** del comienzo.

La motivación es que *New Outlook* muestra calendarios agregados por URL ICS, incluso calendarios externos que solo exponen disponibilidad (`Busy`, `Tentative`), pero no proporciona recordatorios confiables para esos calendarios. CalendarBell funciona **independiente de Outlook**: no requiere cerrar/abrir Outlook, instalar un add-in, conectar la cuenta corporativa ni autenticarse en Microsoft 365.

Se configura haciendo **clic derecho sobre el icono en la bandeja del sistema (tray) → Settings**. En Settings se pueden agregar, editar, deshabilitar y eliminar ilimitados calendarios, cada uno con **Calendar name** + **Calendar URL**. El nombre del calendario aparece claramente en cada notificación; por ejemplo `Calendar A` y `Calendar B`.

Cada aviso ofrece **Snooze** a **10 minutos antes**, **5 minutos antes** o **justo al iniciar** la reunión (0 minutos antes), y **Dismiss** definitivo. No se abre ninguna ventana al posponer o descartar. Si dos o más recordatorios se disparan juntos, se agrupan en una presentación compacta y **no se crean ventanas emergentes propias que se apilen de forma caótica**.

## 1. Alcance, restricciones y decisiones finales

### 1.1 Obligatorio en v1

- Ejecutarse **sin PowerShell/CMD/terminal abierta**, sin ventana principal permanente y sin Windows Service.
- Ser una **app WPF, `WinExe`, .NET 10**, con **tray icon** persistente y arranque automático al iniciar sesión de Windows.
- Abrir **Settings** desde el menú contextual del tray. Doble clic en tray abre una ventana compacta **Upcoming reminders**.
- Soportar múltiples calendarios ICS HTTP(S); **HTTPS obligatorio** al guardar un calendario, salvo servidores localhost usados en tests.
- Por cada calendario: nombre editable, URL editable, Enabled/Disabled, último sync, estado, prueba de conexión, actualización manual y eliminación.
- Consultar calendarios habilitados **cada 2 minutos** por defecto, en paralelo con límite de concurrencia, usando HTTP condicional y reintentos controlados.
- Notificar **15 minutos antes** de cada evento con hora, sonido nativo y datos mínimos.
- Permitir Snooze para la **hora absoluta objetivo** `eventStart - 10 min`, `eventStart - 5 min`, o `eventStart` (no “esperar 10/5/0 minutos a partir del clic”).
- Permitir Dismiss definitivo y evitar reapariciones por cada refresh, reinicio o clic en X.
- Respetar cambios de horario, cancelaciones, recurrencias, múltiples calendarios y zonas horarias.
- Agrupar recordatorios simultáneos y ofrecer manejo individual desde Upcoming reminders.
- Persistir configuración, estado de recordatorios y caché de calendarios entre reinicios.
- Inicio/cierre limpios, instancia única por usuario, logs locales sin secretos, instalador por usuario y procedimiento de desinstalación.
- Tests automatizados e integración real de notificaciones en Windows 11.

### 1.2 No objetivos de v1

- No sincronizar hacia Outlook ni copiar reuniones a calendarios personales.
- No escribir/modificar/invitar/aceptar reuniones en el servidor ICS.
- No iniciar sesión en cuentas Microsoft/Google, ni usar Graph/OAuth.
- No depender de servicios cloud, servidor externo, telemetría o backend.
- No desarrollar una burbuja personalizada tipo Electron, ni intentar replicar pixel-perfect la UI de una notificación de Windows: **Windows controla la posición, X, bordes, botones y estilo visual**.
- No garantizar entrega exacta si PC está apagada/suspendida, si Calendar A retrasa la publicación ICS, si la URL deja de funcionar o si Windows silencia notificaciones.
- No instalar servicio de Windows (Session 0 no es apropiada para mostrar notificaciones en la sesión del usuario).

### 1.3 Defaults cerrados

| Parámetro | Valor inicial | Comportamiento |
|---|---|---|
| Polling ICS | 2 minutos | Por calendario habilitado; `Check now` fuerza un refresh. |
| Anticipación inicial | 15 minutos | Fija en v1; configurable en código, no editable en Settings. |
| Opciones Snooze | 10 / 5 / 0 minutos **antes del inicio** | No son intervalos de espera desde el clic. |
| Sonido | Activado | Sonido nativo del sistema, una vez por toast emitido. |
| Auto-start | Activado después de instalar | Arranca **al iniciar sesión**, nunca elevado. |
| Privacy mode | Activado | Mostrar disponibilidad, hora y nombre de calendario; no título/descripción del evento. |
| Eventos de día completo | Ignorar | No generar recordatorios. |
| Eventos libres/transparentes | Ignorar | No generar recordatorios de `TRANSP:TRANSPARENT` / `Free`. |
| Eventos Busy/Tentative/OOF | Incluir | Respetar lo que realmente permita determinar el ICS. |
| Último cache válido | Hasta 24 h | Si no hay red, seguir alertando desde caché reciente; luego avisar del problema en tray y no confiar en datos muy viejos. |
| Reuniones pasadas | Ignorar | No disparar avalanchas atrasadas. |
| Ventana principal | Oculta | Solo Settings / Upcoming bajo demanda. |
| Idioma UI inicial | Inglés simple | Preparar recursos para en-US y es-AR, sin necesidad de traducir toda la app para cerrar v1. |

### 1.4 Criterio crucial sobre privacidad corporativa

Una URL ICS publicada puede funcionar como una **credencial secreta de acceso**. Nunca imprimirla completa en logs, errores, pantallas de diagnóstico, reportes, crash dumps voluntarios, capturas en README, tests o repositorios. **Solo usar URLs publicadas y compartidas conforme a la política del propietario del calendario**. El hecho de tener una URL no elimina restricciones de seguridad corporativa.

---

## 2. Historias de usuario y flujos end-to-end

### 2.1 Primer inicio

1. El usuario instala CalendarBell y al finalizar queda ejecutándose en tray, **sin ventana de consola**.
2. Si no hay calendarios, Settings se abre **una sola vez** para configurar el primero.
3. El usuario completa `Calendar name = Calendar A` y pega `Calendar URL = https://.../calendar.ics`.
4. Presiona `Test connection`. La app valida HTTPS, descarga con timeout y muestra `Connected · X upcoming events` o un error claro sin revelar la URL.
5. Presiona `Save`. El calendario aparece en la lista con `Enabled` activado, estado `Synced`, último sync y próxima actualización.
6. El usuario presiona `Add calendar`, carga `Calendar name = Calendar B` y otra URL; guarda.
7. Cierra Settings con X: **la app sigue corriendo en tray**.
8. En el siguiente inicio de sesión de Windows, el tray aparece automáticamente y los calendarios se sincronizan en segundo plano.

### 2.2 Notificación normal

Para una reunión de Calendar A de 10:30 a 11:00:

- A las **10:15**, mostrar notificación de Windows con sonido.
- Attribution de Windows: icono y nombre de aplicación `CalendarBell`.
- Título visible, breve: `Calendar A · Meeting in 15 min`.
- Texto visible: `Busy · 10:30–11:00` (si no se conoce el status, `Meeting · 10:30–11:00`).
- Opciones: selector `10 min before`, `5 min before`, `At start`; botón `Snooze`; botón `Dismiss`.
- Usuario puede cerrar con X; la app no vuelve a enviar **el mismo disparo inicial** por el hecho de que refresque el ICS.
- Si selecciona `10 min before` y pulsa Snooze antes de las 10:20, la siguiente notificación aparece a las **10:20** (no 10 minutos después del clic).
- A las 10:20 el selector ofrece solo targets futuros: `5 min before` (10:25), `At start` (10:30). Las opciones vencidas no se muestran.
- Al pulsar Dismiss se cancelan todos los recordatorios posteriores de **esa ocurrencia** de la reunión.

### 2.3 Dos calendarios a la vez

Si Calendar A y Calendar B tienen reuniones a las 10:30, ambas requieren aviso a las 10:15. En lugar de dos popups competidores, crear **una notificación agrupada**:

```text
CalendarBell                                   [X]
2 meetings in 15 min
Calendar A · Busy · 10:30   |   Calendar B · Tentative · 10:30
[ 10 min before  v ]   [ Snooze ] [ Dismiss ]
```

*Este bloque es una guía conceptual de contenido, no una promesa de composición exacta del toast; Windows determina el diseño final.*

- Para un grupo con **mismo inicio** (diferencia máxima 60 segundos), `Snooze` aplica la opción a todas las ocurrencias del grupo, cada una calculada con su propia hora de inicio; `Dismiss` descarta todas las ocurrencias del grupo.
- Si hay **3 o más**, título `3 meetings in 15 min`, mostrar como máximo dos líneas resumidas con nombres y `+N more`, sin desbordar el XML/toast.
- Tocar el cuerpo de la notificación abre `Upcoming reminders` filtrada a esas reuniones, con Snooze/Dismiss **individuales**.
- Si varios recordatorios vencen simultáneamente pero **sus horarios de inicio difieren más de 60 segundos**, emitir **un resumen** `N reminders need attention` con `View reminders` y `Dismiss all`. Dentro de Upcoming, cada uno tiene sus targets Snooze independientes; no aplicar ciegamente una hora común.
- Agrupar mediante una ventana de coalescencia de ~1 segundo para vencimientos simultáneos. Poner las entregas subsiguientes en cola con límite **1 nuevo toast por 8 segundos** para evitar ráfagas; los avisos de `At start` tienen prioridad. Documentar que esta limitación puede producir algunos segundos de demora en colisiones, nunca retrasos de minutos.
- Registrar las alertas de cada ocurrencia por separado aunque la visualización sea agrupada; ningún evento se pierde por estar en grupo.

### 2.4 Menú del tray

Clic derecho en icono `CalendarBell`:

```text
CalendarBell
────────────────────────
Upcoming reminders
Settings
Check now
Pause reminders  >  30 minutes | 1 hour | Until resumed
Resume reminders              (solo cuando paused)
Start with Windows       ✓
────────────────────────
Status: 2 calendars · Synced
About
Exit
```

- Doble clic en tray: abrir Upcoming reminders.
- `Check now`: refrescar fuentes habilitadas, mostrar resultado en Settings/tooltip; no duplicar avisos.
- `Pause reminders`: suspender emisiones, pero seguir sincronizando. Mostrar tray gris o estado `Paused`; al vencer pausa reanudar. Los vencimientos ocurridos durante pausa no se reproducen masivamente.
- `Exit`: cerrar **realmente** la app y liberar tray, temporizadores, conexiones y mutex. Cerrar Settings con X **no** equivale a Exit.
- Hover/tooltip del tray: `CalendarBell · Synced` / `Sync warning` / `Paused`.
- Estados del icono: normal, warning, paused; iconos `.ico` propios, sobrios y legibles a 16px.

---

## 3. Interfaz de configuración (Settings)

### 3.1 Tecnologías y lenguaje visual

- WPF (`net10.0-windows`, `UseWPF=true`, `OutputType=WinExe`). Se puede utilizar `System.Windows.Forms.NotifyIcon` desde WPF para el tray.
- Ventana compacta ~760 × 560 px, redimensionable con mínimo razonable, centrada al primer uso, recordar última posición válida en monitores múltiples.
- Estética **Fluent/Windows 11**: fondo neutro, paneles claros, radios de 8–12px, tipografía Segoe UI/Segoe UI Variable, espaciado generoso, sombras discretas, iconografía lineal, soporte light/dark según Windows y alto DPI. Sin gradientes recargados.
- Controles accesibles: Tab, Enter, Escape, foco, labels para lectores de pantalla, contraste y escalado.
- UI inicial en inglés claro; texto técnico de logs/README en inglés o español consistente. Utilizar `.resx` para futura traducción. No complicar la app con un framework de diseño si genera dependencias innecesarias.

### 3.2 Layout sugerido

```text
┌──────────────────── CalendarBell · Settings ─────────────────────┐
│ Calendars                                      [+ Add calendar]  │
│                                                                 │
│ [✓] Calendar A        Synced · 10:12   [Edit] [Check now] [Remove]      │
│ [✓] Calendar B  Synced · 10:11   [Edit] [Check now] [Remove]      │
│                                                                 │
│ General                                                         │
│ [✓] Start with Windows                                          │
│ [✓] Play notification sound                                     │
│ [✓] Privacy mode (availability only)                            │
│ Initial reminder: 15 min before     [read-only info]            │
│ Snooze targets: 10 / 5 / start      [read-only info]            │
│                                                                 │
│ [Send test notification]    [Open upcoming]        [Close]       │
└─────────────────────────────────────────────────────────────────┘
```

`Add calendar` y `Edit` abren un panel/modal **real** y cómodo:

```text
Calendar name *   [ Calendar A                              ]
Calendar URL *    [ https://...                  👁 ]
Enabled          [✓]
[Test connection]    [Cancel]                 [Save]
```

- `Calendar name`: requerido, trim, **1–40 caracteres**, sensible a caracteres unicode, único sin considerar mayúsculas dentro de la instalación (para que las notificaciones no sean ambiguas). `Calendar A`, `Calendar B`, `Personal`, etc.
- URL: requerida, válida, esquema HTTPS (permitir `http://localhost` solamente en modo debug/test), longitud razonable (máximo 4096 caracteres); conservar query string en repositorio cifrado. No exigir extensión `.ics`.
- URL visible en edición únicamente mediante control explícito mostrar/ocultar; ocultarla en la lista principal y no incluirla en mensajes de error.
- `Test connection`: asíncrono, indicador de carga, botón cancelable, feedback de formato ICS, cantidad de próximos eventos e instante de sync; no guardar cambios hasta Save.
- `Save`: validar, persistir atómicamente, programar sync inmediato, cerrar editor; en URL distinta invalidar caché anterior y estados de ocurrencias del calendario (sin afectar otros).
- `Remove`: confirmar, borrar ajustes/URL cifrada, eventos/estados de ese calendario y notificaciones relacionadas; otros calendarios no cambian.
- `Enabled=false`: detener sync y avisos de ese calendario, y limpiar notificaciones activas correspondientes; conservar config. Rehabilitar hace sync inmediato.
- Cambiar **solo el nombre** del calendario no debe borrar historial ni duplicar notificaciones; usar GUID interno inmutable.
- Controles con mensajes de error inline y no `MessageBox` invasivos salvo errores críticos.
- En Settings indicar número de calendarios, fecha de última sincronización, estado `Synced`, `Refreshing`, `Network error`, `Invalid calendar`, `Paused`, `Never synced`.
- `Privacy mode` activado: mostrar exclusivamente nombre de calendario, disponibilidad e inicio/fin. Desactivado: se puede mostrar `SUMMARY` real si existe, **escapado, sanitizado y truncado** (por ejemplo 80 caracteres); nunca mostrar descripción, participantes, token URL ni links de reunión en toasts. Si el título no existe, usar `Meeting`.
- `Send test notification` debe emitir un toast real de prueba **sin escribir estados de reuniones de producción**; usar ID marcado `test`, y demostrar sonido/estilo. Sus acciones de prueba no deben afectar eventos reales.
- Si Windows tiene notificaciones desactivadas para CalendarBell o `Do not disturb`, mostrar banner informativo y atajo a configuración de notificaciones de Windows cuando sea técnicamente posible, sin intentar evadir las preferencias del usuario.

### 3.3 Upcoming reminders

Ventana compacta bajo demanda, no en ejecución permanente:

- Ver reuniones próximas en las próximas 24 horas (scroll, agrupadas por fecha/hora), mostrando `Calendar name`, `Busy/Tentative`, inicio, fin y estado.
- Cada elemento muestra acciones `Snooze to 10 min before`, `5 min before`, `At start` (solo targets futuros) y `Dismiss`.
- Indicadores: `Pending`, `Notified`, `Snoozed to HH:mm`, `Dismissed`, `Cancelled`, `Expired`; ocultar vencidas por defecto, opción `Show dismissed`.
- Filtro por calendario y estado; si el toast agrupado se tocó, destacar las ocurrencias del grupo.
- Si el usuario descarta desde Upcoming, remover el toast activo de esa ocurrencia/grupo cuando corresponda y persistir.
- No mostrar detalles sensibles mientras Privacy mode esté activado.

---

## 4. Notificaciones nativas: contrato de comportamiento

### 4.1 Implementación requerida

- Preferir **Windows App SDK**: `Microsoft.Windows.AppNotifications.AppNotificationManager` + `AppNotificationBuilder`/XML (`ToastGeneric`, `scenario='reminder'`).
- Para .NET WPF usar la guía oficial actual de registro de notificaciones; crear registro `NotificationInvoked` **antes** de `AppNotificationManager.Default.Register()`.
- En app **unpackaged**, `Register()` configura el registro COM necesario de activación. Verificar activación tanto con app viva como con app cerrada; en la segunda ruta Windows puede relanzar el `.exe` y la acción debe recibirse/reenviarse a la instancia principal sin abrir Settings.
- **No ejecutar como administrador**: Microsoft documenta que las notificaciones de aplicación no funcionan en procesos elevados.
- `OutputType=WinExe` y una única instancia; el clic de notificación puede iniciar otra instancia: usar single-instance coordination (named mutex por SID del usuario + named pipe local o mecanismo equivalente) para reenviar argumentos a la instancia residente.
- Implementar `IToastNotificationService` detrás de una interfaz para tests.
- Registrar `Tag` y `Group` estables en los toasts y removerlos mediante `RemoveByTagAndGroupAsync` o equivalente cuando cambien, se cancelen, queden descartados o expiren.
- Especificar expiración razonable; no llenar Notification Center. Respetar los límites del SO (por ejemplo, 20 avisos activos por app documentados para headers) limpiando avisos obsoletos.

### 4.2 Apariencia exigida

Minimalista, limpia, agradable y compacta. Usar solo texto útil, un icono propio y controles nativos. Evitar imágenes de reuniones, fotos, enlaces, JSON o texto truncado con secretos. Textos sugeridos:

**Un evento**:

```text
CalendarBell
Calendar A · Meeting in 15 min
Busy · 10:30–11:00
[10 min before ▾]       [Snooze] [Dismiss]
```

**En repetición/Snooze**:

```text
CalendarBell
Calendar A · Meeting in 5 min
Busy · 10:30–11:00
[At start ▾]            [Snooze] [Dismiss]
```

**En el minuto exacto**:

```text
CalendarBell
Calendar A · Meeting starting now
Busy · 10:30–11:00
[Dismiss]
```

**Dos simultáneas**: `2 meetings in 15 min` y descripción breve `Calendar A · Busy · 10:30 | Calendar B · Tentative · 10:30`.

- Nombre del calendario siempre visible en notificación individual. En resumen mostrar nombres de al menos dos calendarios si caben, más `+N more`.
- Respetar formatos de hora locales de Windows (12/24 h), configuración regional, accesibilidad y dark mode.
- La **X del toast** cierra visualmente; una vez `InitialNotified=true`, **nunca reemitir la fase inicial** solo porque el usuario cerró con X. No depender de detectar el clic de X (no siempre se expone como acción de negocio). Dismiss sí debe persistirse explícitamente.
- Para un toast marcado `Reminder`, Windows puede mantenerlo visible hasta que se interactúe. **No asumir** que se puede controlar su posición, apilamiento exacto, dimensiones o animación.

### 4.3 Snooze: precisión semántica innegociable

**No usar ciegamente el `system snooze` de Windows**, porque ese mecanismo pospone un **intervalo relativo al momento del clic**; el requisito aquí es una **hora absoluta respecto al inicio del evento**. Implementar un selector `AppNotificationComboBox` y botones de **activación controlada por la app** (`action=snooze`, `targetMinutesBefore=10|5|0` o lectura del valor de `UserInput`). Construir XML crudo si el builder no cubre un caso. Manejar la acción sin abrir una ventana.

Para una ocurrencia de inicio `T` (UTC):

```text
InitialDueUtc = T - 15 minutes
Snooze10DueUtc = T - 10 minutes
Snooze05DueUtc = T -  5 minutes
Snooze00DueUtc = T
```

**Solo se ofrecen targets estrictamente futuros** respecto del instante de clic (con margen técnico de 2 segundos):

- A las 10:15, reunión 10:30 → 10:20, 10:25, 10:30.
- Si pulsa Snooze a las 10:18 y elige `10 min before` → alertar **10:20**, no 10:28.
- Si pulsa a las 10:22 → no ofrecer `10 min before`, ofrecer 10:25 y 10:30.
- Si pulsa a las 10:29:58 → permitir `At start` solo si el instante objetivo sigue siendo futuro; si no, Dismiss y/o vista Upcoming.
- A las 10:30 → no ofrecer Snooze; solo Dismiss.
- Nunca ofrecer un target pasado ni generar re-alerta inmediata en un bucle.
- Al Snooze: persistir `SnoozedUntilUtc` antes de remover/reemplazar el toast actual; registrar la transición de estado, programar próximo disparo, evitar doble ejecución si se reciben clicks repetidos.
- Al dispararse un Snooze, reproducir sonido de nuevo. Mostrar tiempo restante real; no reutilizar el texto `in 15 min`.
- No programar simultáneamente los tres Snooze: **solo el target explícitamente elegido**.
- Si el inicio cambia después de Snooze, recalcular; si el nuevo target ya venció y la reunión sigue futura, aplicar política de notificación tardía controlada (ver §6.6).
- Para toast agrupado con mismo inicio, Snooze/Dismiss alcanza **solo a miembros de ese grupo**, nunca otras reuniones.
- **No agregar un Snooze periódico automático**; no hay repeticiones si no hay un clic en Snooze.

### 4.4 Dismiss y X

- `Dismiss` significa: marcar esa ocurrencia `Dismissed`, remover toast, cancelar cualquier Snooze y no volver a notificar esa ocurrencia, aunque se refresque el ICS o se reinicie Windows.
- `X` significa: cerrar el toast; la fase que ya fue emitida permanece registrada como emitida. Si no se programó Snooze, no rearmar avisos artificialmente.
- Un evento cancelado (`STATUS:CANCELLED` o eliminado de una actualización completa válida) debe quitar notificaciones programadas/visibles y su estado pasa a `Cancelled`.
- Si un evento cambia realmente de **fecha/hora de inicio** tras haber sido descartado, tratarlo como nueva planificación de esa misma ocurrencia y permitir un nuevo recordatorio para el horario cambiado (no para cambios menores de texto).

### 4.5 Sonido

- Emitir **un sonido de notificación del sistema** por toast nuevo (inicial o después de un Snooze); **no loop**, no sonido fuerte propio, no `Console.Beep()`.
- Priorizar sonido estándar de recordatorios/notificaciones por Windows App SDK (`scenario='reminder'` y audio conforme a SDK), con opción global `Play notification sound` en Settings.
- Si sound off, toast silencioso. Si Windows tiene `Do not disturb`, volumen en cero o sonido deshabilitado, respetarlo y reflejar la posible causa en ayuda/Settings.
- En grupo simultáneo, **un solo sonido** por aviso agrupado.

---

## 5. Calendarios ICS: descarga, parsing y privacidad

### 5.1 Sincronización HTTP

- `HttpClient` único/factory con timeout de 15 s, `User-Agent: CalendarBell/<version>`, `Accept: text/calendar` (aceptar otros content types solo si cuerpo ICS válido).
- GET cada 2 min en calendarios activos, no HEAD obligatorio; `If-None-Match` y `If-Modified-Since` cuando existan ETag/Last-Modified; soportar `304 Not Modified`.
- Validar TLS/HTTPS; **nunca** permitir certificados inválidos o SSL bypass. Hasta 3 redirects, siempre HTTPS y con políticas de seguridad; no reenviar secretos a otro host sin validación/confirmación explícita.
- Protección básica contra contenido malicioso: tamaño máximo por respuesta 10 MiB (configurable), no consumir respuestas infinitas, timeout, parsing seguro, sin ejecutar scripts ni renderizar HTML, máximo 100.000 instancias expandidas por consulta.
- Máximo 3 descargas simultáneas; escalonar con pequeño jitter para no generar picos. No solapar dos refreshes del mismo calendario; `Check now` cancela/reutiliza la operación en curso sin duplicar.
- Respetar `429` y `Retry-After` cuando exista; 5xx/timeout → backoff exponencial progresivo 2, 4, 8, 16, 30 minutos máximo, con jitter. Un error en Calendar B no bloquea Calendar A.
- Un `200 OK` con ICS inválido **no debe borrar** el caché válido previo. Un feed ICS válido vacío sí puede cancelar reuniones que desaparecieron, con protecciones contra contenido truncado/incompleto.
- Guardar última actualización válida, ETag y metadatos; ante 304 conservar eventos existentes sin reiniciar estados.
- No afirmar que los datos estarán “en vivo”: la frescura depende de la publicación del proveedor, cachés HTTP y la frecuencia de polling. Mostrar `Last synced` real.

### 5.2 Parsing RFC 5545

Usar **Ical.Net** (versión estable compatible con .NET 10, fijada en proyecto/lockfile). Cubrir al menos:

- `VCALENDAR`/`VEVENT`, `UID`, `DTSTART`, `DTEND`/`DURATION`, `SUMMARY`, `STATUS`, `TRANSP`, `DTSTAMP`, `SEQUENCE`, `LAST-MODIFIED`.
- `RRULE`, `RDATE`, `EXDATE`, excepciones con `RECURRENCE-ID`, cancelaciones individuales y eventos reprogramados.
- `TZID`, `VTIMEZONE`, UTC (`Z`), offset cuando proceda, DST y eventos *floating time* (interpretarlos en zona local del sistema para v1; registrar advertencia de ambigüedad, nunca convertir arbitrariamente a UTC sin zona).
- Expansión de ocurrencias con ventana móvil `[now - 1 día, now + 30 días]`, con cota de tamaño y tiempo. Actualizarla en cada sync y al cambiar fecha/sistema.
- Excluir eventos de día completo (fechas `VALUE=DATE`), eventos cancelados, intervalos inválidos (`end < start`), transparent/free; incluir eventos con inicio/fin válido y estado `Busy`, `Tentative` o `Out of office` cuando ese dato exista.
- `Tentative` puede provenir de `STATUS:TENTATIVE` y `Busy/Free` de la implementación del proveedor; interpretar también extensiones reconocidas como `X-MICROSOFT-CDO-BUSYSTATUS` solo si hay evidencia de su semántica. **No inventar un status** si el ICS no permite conocerlo. Fallback en pantalla: `Meeting`.
- Si `SUMMARY` es `Busy` o `Tentative`, se puede mapear a texto de disponibilidad; nunca inferir por `SUMMARY` una etiqueta de seguridad o certeza que no exista.
- Para eventos sin `UID`, generar clave estable de respaldo en base a propiedades relevantes (documentar limitación; no duplicar indiscriminadamente); advertir en log sanitizado.
- Tratar texto ICS como **datos no confiables**: escapar el XML del toast, no interpolar directamente valores externos en XML, no lanzar links/archivos, longitud limitada para mostrar.

### 5.3 Modelo de evento

```csharp
public sealed record CalendarSource(
    Guid CalendarId,
    string Name,
    string EncryptedUrl,
    bool Enabled);

public sealed record EventOccurrence(
    Guid CalendarId,
    string Uid,
    string OccurrenceKey,
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Availability, // Busy|Tentative|OutOfOffice|Unknown
    bool IsAllDay,
    bool IsCancelled,
    string? PrivacySafeTitle,
    string RevisionFingerprint);
```

- `CalendarId`: GUID estable e independiente del nombre visible.
- `OccurrenceKey`: ID determinista estable por instancia recurrente. Para recurring, usar `(CalendarId, UID, RECURRENCE-ID original)`; para single, `(CalendarId, UID)`; **no incluir hora actual reprogramada en la identidad**.
- `RevisionFingerprint`: hash de campos relevantes para detectar modificación de inicio/fin/estado y no revivir avisos por cambios cosméticos.
- Dos calendarios distintos con el mismo UID representan ocurrencias distintas: distinguir siempre por `CalendarId`. No deduplicar entre fuentes automáticamente.

### 5.4 Seguridad de URL y datos

- Guardar URL cifrada con DPAPI `ProtectedData` usando `DataProtectionScope.CurrentUser`; jamás en texto plano en `appsettings.json` ni en base de datos.
- Ruta recomendada: `%LOCALAPPDATA%\CalendarBell\`, permisos de usuario, no `%PROGRAMDATA%` compartido.
- Guardar `settings.json` solo con preferencias no sensibles y campos cifrados codificados; proteger también backups de configuración. En la caché conservar solo los campos normalizados necesarios para recordatorios; **no persistir el ICS crudo, DESCRIPTION, ATTENDEE ni enlaces de reunión**. Cuando Privacy mode esté activo, tampoco persistir `SUMMARY` en claro.
- `logs` con calendario por **nombre/ID**, tipo de error, código HTTP y duración; sin URL completa, query string, summary/títulos ni credenciales.
- Sin telemetría ni envío a terceros; no introducir Sentry, Application Insights u otros servicios por defecto.
- Si se exportan settings, **no exportar URLs** por defecto. Si se añade export con secretos, exigir acción explícita y advertencia (no es requisito v1).
- Nunca incorporar URLs reales en fixtures, código fuente, README o tests.

---

## 6. Motor de recordatorios y reglas de tiempo

### 6.1 Separar sincronización de scheduling

**Error a evitar:** utilizar un timer de 2 minutos como mecanismo de notificación; produciría alertas tarde.

- `CalendarSyncService`: descarga/parsing cada 2 min y actualiza datos.
- `ReminderScheduler`: mantiene cola ordenada por `DueUtc`; tras un sync recalcula próximos vencimientos; utiliza `TimeProvider`/timers precisos, sin depender del polling ICS.
- `NotificationOrchestrator`: recibe ocurrencias debidas, agrupa, emite un toast, maneja acciones.
- Persistir antes de emitir (con estado transicional idempotente) para minimizar duplicados al crash; registrar el resultado de la API y reconciliar al reiniciar.

### 6.2 Máquina de estados por ocurrencia

Estados propuestos:

```text
Pending → InitialDue → Notified
                         ├─ Snoozed(target=10|5|0) → NotifiedAgain
                         │                              ├─ Snoozed(otro target futuro)
                         │                              └─ Dismissed
                         ├─ Dismissed
                         └─ Expired
(any active state) ── cancellation ──> Cancelled
(any state) ── changed start time ──> Pending (nueva revisión temporal)
```

Persistir como mínimo:

```csharp
public sealed record ReminderRecord(
    Guid CalendarId,
    string OccurrenceKey,
    string RevisionFingerprint,
    DateTimeOffset EventStartUtc,
    DateTimeOffset InitialDueUtc,
    bool InitialNotificationSent,
    DateTimeOffset? SnoozedUntilUtc,
    bool Dismissed,
    bool Cancelled,
    DateTimeOffset? LastNotifiedUtc,
    string? LastToastTag,
    string? LastToastGroup,
    long StateVersion);
```

Añadir histórico de transiciones/eventos solo si simplifica depuración; no guardar títulos si Privacy mode.

### 6.3 Persistencia local

- Utilizar **SQLite** (`Microsoft.Data.Sqlite`), una DB por usuario en `%LOCALAPPDATA%\CalendarBell\calendarbell.db`, schema versionado, índices y `WAL` si corresponde.
- Tablas sugeridas: `Calendars`, `OccurrencesCache`, `Reminders`, `SyncState`, `AppState`, `SchemaMigrations`. El campo URL se almacena cifrado.
- Transacciones para sincronización y cambios de estado; `UNIQUE(calendar_id, occurrence_key)`.
- Cargar estados al inicio; recuperar próximos eventos; evitar reavisar avisos que ya salieron.
- Manejar DB corrupta: copiar respaldo local sanitizado para diagnóstico, crear nueva DB solo con aprobación de seguridad? En v1 informar `Storage error`, preservar copia y **no borrar automáticamente secretos/datos**; permitir recuperación manual. Si falla almacenamiento, no afirmar al usuario que Snooze/Dismiss se guardó.
- Mantener copia de seguridad atómica de ajustes; limpieza de estados vencidos después de 30 días, respetar privacidad.

### 6.4 Cálculo y cambios

- Usar UTC internamente (`DateTimeOffset`), convertir solo al mostrar.
- Calcular `T-15` una sola vez por revisión temporal y registrar `InitialNotificationSent`.
- Si el ICS se actualiza en T-12 y todavía no se notificó: emitir **notificación tardía inmediata** `Meeting in 12 min` y luego permitir los targets futuros de Snooze.
- Si llega en T+0 a T+60 segundos: como máximo un aviso `Meeting starting now` si nunca se notificó y la sesión está activa; si T+60 o después, marcar `Expired` sin toast atrasado.
- Si cambia el inicio después del aviso: comparar nuevo inicio, cancelar/reconciliar estado y crear nueva planificación si el inicio es futuro. No duplicar por cambio de nombre, título o URL equivalente.
- Si se elimina el calendario o se deshabilita, cancelar próximos avisos correspondientes; otros calendarios continúan.
- Si PC estuvo dormida: al reanudar recalcular con la hora real; no enviar avisos pasados en masa. Si aún no comenzó la reunión, aplicar aviso tardío; si pasó >60 segundos desde el inicio, descartar alerta obsoleta.
- Si cambia el reloj del sistema, zona horaria o DST: recalcular vencimientos y timestamps locales. Escuchar eventos de power resume/time change cuando sea factible; además verificar drift periódicamente.
- Los cambios de ICS tardíos/inestables no deben generar lluvia de mensajes por cada polling.

### 6.5 Priorización y colisiones

1. Guardar cada ocurrencia como unidad independiente, aunque se presente con otras.
2. En un tick del scheduler juntar todas las `DueUtc <= now` dentro de una pequeña ventana de coalescencia (~1 s) y marcar como lote.
3. **Una sola notificación por lote**; nunca crear varias ventanas custom para apilar.
4. Lotes con horarios iniciales equivalentes (±60 s): usar `Snooze`/`Dismiss` grupal y título resumido.
5. Lotes con inicios distintos: resumen con `View reminders`/`Dismiss all`; manejo Snooze individual en Upcoming.
6. Máximo una **emisión nueva** de toast cada ~8 s; cola estable FIFO por deadline, con `At start` de mayor prioridad. Evitar retrasar arbitrariamente avisos críticos.
7. Hacer clean-up de los toasts por tags/expiración; no superar límites del sistema.
8. Si Windows decide apilar banners o retener un reminder en pantalla, eso lo maneja el shell; la app no debe calcular coordenadas ni forzar posiciones de popup.
9. Para lotes muy grandes, mostrar `N reminders` con máximo dos líneas de detalle y un clic que abra la lista. Evitar XML > 5 KB.
10. El sonido se reproduce una sola vez por lote, no N veces.

### 6.6 Política de estado de Windows

- App ejecutada con sesión iniciada, no elevada; puede funcionar con Settings cerrado y Outlook cerrado.
- Bloquear la sesión **no debe matar** el proceso ni perder agenda; Windows decidirá cuándo mostrar toast. No intentar mantener PC activa artificialmente.
- Suspensión/hibernación/apagado: timers no se ejecutan. Recuperar al despertar siguiendo §6.4.
- Si el usuario silenció la app en Windows, respetarlo. Mostrar estado de diagnóstico, no reproducir sonido por otro canal para evadirlo.
- No prometer precisión de reloj de tiempo real: objetivo **±2 segundos** con PC despierta y evento conocido (excluyendo retrasos del sistema y colisiones); en ráfagas máximo ~8 s extra por rate limiting.

---

## 7. Arquitectura y estructura del repositorio

### 7.1 Stack decidido

- **.NET 10, C# moderno, WPF** (`net10.0-windows`, `WinExe`), Windows 11 x64.
- **Windows App SDK** `Microsoft.WindowsAppSDK` para notificaciones (`AppNotificationManager`), última versión estable compatible verificada al implementar, versión fijada.
- **Ical.Net** para parsing y recurrencias (fijar versión estable probada).
- **Microsoft.Data.Sqlite** para persistencia.
- `HttpClient` con `IHttpClientFactory` o instancia gestionada; `System.Text.Json` para preferencias no sensibles.
- Tray `System.Windows.Forms.NotifyIcon` mediante referencias Windows Forms en proyecto WPF; no Forms visibles.
- Dependency injection con `Microsoft.Extensions.Hosting`/DI/Logging cuando aporte claridad, sin convertirlo en Windows Service.
- Unit testing **xUnit** o **NUnit** (elegir uno), `FakeTimeProvider` y mocks/fakes. Preferir interfaces y SOLID/YAGNI.
- No Electron, no WebView2, no navegador embebido, no procesos shell persistentes, no base de datos servidor.

### 7.2 Estructura sugerida (adaptable sin reducir responsabilidades)

```text
CalendarBell/
├─ CalendarBell.sln
├─ src/
│  ├─ CalendarBell.App/                    # WPF, tray, Settings, Upcoming, bootstrap
│  │  ├─ App.xaml / App.xaml.cs
│  │  ├─ Views/SettingsWindow.xaml(.cs)
│  │  ├─ Views/UpcomingWindow.xaml(.cs)
│  │  ├─ ViewModels/
│  │  ├─ Resources/Icons/, Styles/, Strings/
│  │  └─ Services/TrayIconHost.cs
│  ├─ CalendarBell.Core/                   # lógica sin dependencia de UI/Windows
│  │  ├─ Models/
│  │  ├─ Interfaces/
│  │  ├─ Services/ReminderScheduler.cs
│  │  ├─ Services/ReminderStateMachine.cs
│  │  ├─ Services/NotificationBatcher.cs
│  │  └─ Time/
│  └─ CalendarBell.Infrastructure/         # HTTP, ICS, SQLite, DPAPI, Win notifications, startup
│     ├─ Ics/IcsCalendarParser.cs
│     ├─ Sync/CalendarSyncService.cs
│     ├─ Storage/
│     ├─ Notifications/WindowsToastService.cs
│     ├─ Security/DpapiUrlProtector.cs
│     └─ Windows/StartupRegistration.cs
├─ tests/
│  ├─ CalendarBell.Core.Tests/
│  ├─ CalendarBell.Infrastructure.Tests/
│  └─ CalendarBell.Windows.SmokeTests/     # pruebas manuales/automatizables en Win11
├─ fixtures/                               # ICS de prueba SIN URLs corporativas
├─ installer/                              # Inno Setup o equivalente sin admin
├─ scripts/                                # build.ps1, publish.ps1, install-dev.ps1
├─ docs/                                   # arquitectura, testing, user guide
├─ README.md
├─ AGENTS.md                               # reglas para IA / agentes
└─ LICENSE (solo si corresponde al tipo de distribución)
```

### 7.3 Interfaces mínimas

```csharp
public interface ICalendarFeedClient
{
    Task<CalendarFetchResult> FetchAsync(CalendarSource calendar, CancellationToken ct);
}

public interface ICalendarParser
{
    IReadOnlyList<EventOccurrence> Parse(string ics, Guid calendarId,
        DateTimeOffset windowStartUtc, DateTimeOffset windowEndUtc);
}

public interface IReminderScheduler
{
    Task ReconcileAsync(Guid calendarId, IReadOnlyList<EventOccurrence> events, CancellationToken ct);
    Task SnoozeAsync(IReadOnlyList<string> occurrenceKeys, int minutesBeforeStart, CancellationToken ct);
    Task DismissAsync(IReadOnlyList<string> occurrenceKeys, CancellationToken ct);
}

public interface INotificationPresenter
{
    Task ShowAsync(NotificationBatch batch, CancellationToken ct);
    Task RemoveAsync(string tag, string group, CancellationToken ct);
}

public interface IClock { DateTimeOffset UtcNow { get; } }
```

El `minutesBeforeStart` no significa “cantidad de minutos a esperar”: siempre resolverlo contra la hora `StartUtc` de **cada** evento.

### 7.4 Lifetime e instancia única

- Al iniciar: obtener mutex per-user; si ya existe, reenviar `open-settings`, `open-upcoming` o `notification-action` por named pipe y finalizar el segundo proceso sin flash de ventana.
- Inicializar DI, storage, notification event handler, registrar notificaciones, crear NotifyIcon y cargar caché; refrescar en segundo plano sin bloquear UI.
- Manejar excepciones no observadas con log sanitizado; fallos de un calendario no detienen proceso.
- Al `Exit`: cancelar tokens, guardar cambios, esconder/dispose tray icon, cerrar DB, dar de baja `AppNotificationManager` si corresponde, liberar mutex.
- Mantener hilo STA para WPF y dispatcher correcto para acciones que llegan desde threads de notificación.
- Asegurar que la aplicación no quede huérfana después de un logout; al iniciar sesión siguiente se relanza vía startup.

---

## 8. Instalación, inicio automático y distribución

### 8.1 No usar Windows Service

Un Windows Service ejecuta normalmente en Session 0 y no puede presentar de forma directa las notificaciones interactivas del usuario. Requeriría un agente adicional. Por eso **el proceso principal será la aplicación WPF invisible en tray**, no un servicio.

### 8.2 Inicio automático

- En la instalación registrar una tarea en **Windows Task Scheduler**, `At logon` del usuario actual, **`Run only when user is logged on`**, **`Run with highest privileges` DESACTIVADO**, ruta al `CalendarBell.exe` instalado. Puede usarse `HKCU\Software\Microsoft\Windows\CurrentVersion\Run` como fallback si la tarea por usuario falla, sin duplicar ambos métodos.
- Opción `Start with Windows` en Settings controla ese registro de forma idempotente.
- No arrancar con `powershell.exe`, `cmd.exe`, `dotnet run`, consola ni proceso hijo visible. Invocar EXE publicado directamente.
- Instalar bajo `%LOCALAPPDATA%\Programs\CalendarBell\` para no necesitar admin; archivos de datos bajo `%LOCALAPPDATA%\CalendarBell\`.
- Reinstalación/actualización: conservar settings y estado; no crear segunda tarea, segunda instancia ni borrar URL.
- Desinstalación: eliminar tarea/registro de inicio, aplicación, accesos y COM registrations propios; ofrecer opción explícita para conservar o borrar datos y URL cifrada.

### 8.3 Build y release

- Publicar **win-x64 self-contained** para no exigir al usuario instalar .NET runtime aparte.
- Configurar Windows App SDK self-contained **si es viable** en el modo unpackaged elegido; incluir todos los archivos nativos requeridos y verificarlo en una instalación limpia. No asumir que un único EXE contiene automáticamente dependencias de Windows App SDK.
- Entregar carpeta `dist/CalendarBell-win-x64/` con `CalendarBell.exe` + dependencias y **instalador per-user** (`CalendarBell-Setup.exe` usando Inno Setup o equivalente). Si no se dispone de la herramienta de packaging, entregar además ZIP listo para uso + script instalador per-user, documentar limitación y dejar fuente del instalador.
- Incluir iconos `.ico`, versión, metadatos de aplicación y desinstalación. La firma de código es deseable pero **no inventar un certificado**: explicar SmartScreen si binario no está firmado.
- Agregar scripts `scripts/build.ps1`, `scripts/test.ps1`, `scripts/publish.ps1` que funcionan cuando se ejecutan explícitamente durante desarrollo, pero **no son requeridos para que la app funcione**.
- La versión release **no** muestra ninguna consola al iniciar, sincronizar, abrir Settings, ejecutar Snooze ni Dismiss.
- Versión mínima: Windows 11 x64 y .NET 10 SDK solo en máquina de desarrollo; compilar/trabajar en Windows cuando las API nativas no existan en el CI.

---

## 9. Calidad, seguridad, rendimiento y observabilidad

### 9.1 Objetivos

- Consumo de recursos reducido cuando está ociosa; no implementar busy-waiting ni polling por segundo a la red.
- UI responsive aunque un feed tarde, falle o tenga miles de eventos.
- No dejar timers duplicados al editar calendarios ni al reabrir Settings.
- Sin leaks de eventos WinRT, NotifyIcon, HttpClient, CTS, Dispatcher, SQLite.
- En condiciones normales: no consumir CPU de manera sostenida, no bloquear arranque de Windows, no elevar privilegios, no manejar secretos en memoria más tiempo del necesario.
- Acotar caché, eventos expandidos y registros; rotación de logs (por ejemplo, máximo 5 archivos × 2 MiB, retención 14 días).

### 9.2 Diagnóstico accesible desde Settings

Panel `Diagnostics` (puede estar en About):

- App version, notification registration status, `Notifications enabled` si accesible, startup registration, total calendars, last successful sync por fuente y siguiente refresh.
- Hora local detectada, timezone, sync errors sanitizados, pending reminders count.
- Botones `Check now`, `Send test notification`, `Open logs folder`, `Copy diagnostics` **sin URL/secretos**.
- Si falla el acceso al feed: warning en tray y status del calendario, no modal repetitivo ni tormenta de toasts de error.

### 9.3 Riesgos explícitos que la IA debe verificar

1. **Toast Snooze:** usar acción custom por hora objetivo. La acción `system snooze` de Windows no representa por sí sola T−10/T−5/T.
2. **Toast dismiss / X:** no confundir `Dismiss` app-managed con cerrar X. La ausencia de callback de X no debe provocar repeticiones.
3. **Registro y activación:** app unpackaged/Windows App SDK y clicks cuando la app ya está abierta o Windows la relanza.
4. **Sesión no elevada:** no marcar startup con `highest privileges`.
5. **Grupos simultáneos:** un lote visual, estado independiente de cada miembro, limpieza correcta al Snooze/Dismiss.
6. **ICS recurrente:** excepciones, cambios de hora, TZID, cancelaciones y DST.
7. **Privacidad:** URL publicada es un secreto; protegido al guardar y en logs.
8. **Entrega real:** la fuente puede publicar demorado, y Windows puede suprimir avisos/sonidos. No afirmar entrega garantizada al milisegundo.
9. **Persistencia:** reinicio no causa duplicación ni pierde Snooze.
10. **Gestión de archivos/instalación:** limpieza completa y arranque directo de EXE sin terminal.

---

## 10. Plan de pruebas (obligatorio, no opcional)

La IA debe crear datos de prueba sintéticos y ejecutar todas las pruebas posibles. **No requiere URL real de Calendar A para probar la aplicación.** Agregar fixture ICS local y un mock HTTP server configurado en tests/debug.

### 10.1 Unit tests de horario y estado

| ID | Entrada | Resultado requerido |
|---|---|---|
| T01 | Inicio 10:30, feed ya leído | Un aviso inicial 10:15. |
| T02 | Polling ICS 10:14/10:16 | Aviso 10:15 por scheduler independiente. |
| T03 | Snooze a 10:16, target 10 | Nuevo aviso 10:20, no 10:26. |
| T04 | Snooze a 10:22, target 10 | Target no disponible; no alerta instantánea. |
| T05 | Snooze a 10:22, target 5 | Nuevo aviso 10:25. |
| T06 | Snooze a 10:28, target 0 | Nuevo aviso 10:30. |
| T07 | Dismiss después de inicial | Cero avisos posteriores, incluso tras reiniciar. |
| T08 | Cerrar visualmente con X | No redirige ni repite la fase inicial por refresh. |
| T09 | Dos eventos Calendar A y Calendar B a las 10:30 | Una notificación agrupada, dos estados independientes. |
| T10 | Agrupado → Dismiss all | Ambas ocurrencias quedan descartadas, ninguna de otro grupo. |
| T11 | Agrupado → Snooze a T−5 | Ambas reprogramadas a sus respectivos T−5. |
| T12 | 100 eventos con mismo vencimiento | Una notificación resumen, sin overflow ni pérdida de eventos. |
| T13 | Dos calendarios tienen UID idéntico | IDs independientes por CalendarId. |
| T14 | Mismo ICS devuelve 304 | No resetear estado ni duplicar avisos. |
| T15 | Feed cambia hora de 10:30 a 11:30 | Cancelar programación vieja y notificar nueva según reglas. |
| T16 | Evento eliminado/cancelado | No enviar avisos pendientes y limpiar UI. |
| T17 | Laptop despierta a T−7 | Aviso tardío inmediato si no había aviso inicial. |
| T18 | Laptop despierta a T+10 | No enviar aviso obsoleto. |
| T19 | Cambio de timezone / DST | Correcto UTC y horario local; sin doble aviso. |
| T20 | URL no responde | Cache válido y warning; resto de calendarios funcionan. |
| T21 | Feed ICS inválido | Mantener el último cache válido, no borrar todos los eventos. |
| T22 | 3 refresh concurrentes del mismo feed | Una sola operación efectiva y sin corrupción. |
| T23 | Evento recurrente con EXDATE | Ocurrencia excluida no se notifica. |
| T24 | RECURRENCE-ID modifica hora | Una sola ocurrencia reprogramada, sin duplicado. |
| T25 | Evento allday / transparente / cancelado | No generar reminder. |
| T26 | Guardar URL | Archivo/DB no contienen URL en texto plano. |
| T27 | Log de error HTTP con query token | No contiene token, query ni credenciales. |
| T28 | Sonido off | Toast silencioso, resto de acciones funcional. |
| T29 | Pause 1h | No avisar durante pausa; al reanudar no replay masivo. |
| T30 | Cambiar nombre Calendar A→Team Calendar | Mismo CalendarId y estado; no re-alertar. |
| T31 | Cambiar URL | Invalidación solo de ese calendario. |
| T32 | Dos procesos inician al mismo tiempo | Un único tray icon y una sola agenda activa. |
| T33 | Notificación con caracteres `<`, `&`, comillas en título | XML válido, no inyección ni crash. |
| T34 | 15 min antes con PC activa | ±2 s objetivo en test con reloj falso; validación manual aproximada. |
| T35 | App cerrada: clicar notificación anterior | Activación/reinicio no abre consola ni duplica tray; acción procesada. |
| T36 | Feed stale >24h | Estado warning, no usar cache obsoleto como si fuera fresco. |

### 10.2 UI/UX y pruebas de Windows 11

- Instalación limpia: aparece tray icon, sin PowerShell/CMD, Settings se puede abrir desde clic derecho.
- Agregar Calendar A y Calendar B, editar, deshabilitar, reactivar, borrar, volver a instalar conservando config.
- `Test connection` con ICS real de prueba, ICS inválido, HTTP 401/403/404/429/500, sin internet, timeout y 304.
- `Send test notification`: aparece con sonido, nombre app e icono adecuados; Dismiss cierra. Verificar sonido off.
- Verificar click `Snooze`, `Dismiss`, click en cuerpo y X; acciones no abren Settings innecesariamente.
- Comprobar Light/Dark, 100%/150%/200% DPI, monitor secundario, teclado y texto largo.
- Verificar que Windows puede tener **Do not disturb** o notificaciones de app desactivadas sin que la aplicación lo eluda.
- Outlook cerrado: alertas funcionando. Abrir/cerrar Settings repetidamente: tray permanece, una sola instancia.
- Reinicio Windows: arranque automático, proceso normal no elevado, recordar calendarios y Snooze.
- Duración extendida: 24 horas con varios feeds simulados, sin fuga relevante de memoria ni duplicados.
- Windows Event Viewer/logs: sin excepciones ocultas ni filtración de URLs.

### 10.3 Criterio de cierre de pruebas

- `dotnet build -c Release` finaliza sin errores.
- `dotnet test -c Release` pasa **todos** los tests automatizados (reportar cantidad).
- Publicación win-x64 y arranque del EXE funcionan en una sesión Windows 11 real o VM de prueba.
- Notificación inicial, Snooze T−10/T−5/T, Dismiss y agrupación **demostrados** con una fuente sintética y un reloj acelerado/controlado o test de integración reproducible.
- Si el agente no tiene Windows ni soporte de UI nativa, **no declarar** esa validación hecha: dejar test manual detallado y señalar limitación explícita en README, pero entregar todo el código, mocks, scripts y pruebas automatizadas que sí puedan ejecutarse.

---

## 11. Requisitos de entrega final para la IA que programe

Entrega un proyecto **terminado**, no solo snippets.

1. Solución completa compilable (`.sln`, proyectos, NuGet versionado, código fuente, assets).
2. Implementación completa de WPF UI, tray, menú contextual, Settings, Upcoming, sync ICS, scheduler, toasts, sonido, Snooze y Dismiss, colisiones, persistencia, startup y seguridad.
3. Fixtures ICS sintéticos y tests unitarios/integración automatable.
4. `README.md`: descripción, requisitos de Windows, instalación, cómo agregar calendarios, notas de privacidad, cómo activar notificaciones de Windows, cómo verificar el tray, troubleshooting, desinstalación.
5. `docs/ARCHITECTURE.md`: diagrama de componentes, decisiones (incluidas limitaciones Windows), flujo de acciones y modelo de estados.
6. `docs/TESTING.md`: comandos, tests, resultados observados, pruebas manuales.
7. `scripts/build.ps1`, `scripts/test.ps1`, `scripts/publish.ps1`.
8. Directorio de salida `dist/CalendarBell-win-x64` y, si el entorno lo permite, instalador por usuario `dist/CalendarBell-Setup.exe`.
9. `AGENTS.md` con convenciones C#, seguridad de logs y obligación de tests/reviews.
10. Resumen final con archivos generados, cómo correrlo, números reales de tests ejecutados y limitaciones/pendientes verdaderos. No inventar evidencias.

**Definición de terminado (DoD):** un usuario en Windows 11 puede instalar la app, abrir Settings desde tray, registrar Calendar A + Calendar B, cerrar Settings, ver alertas con el nombre correcto a T−15, posponer a T−10/T−5/T, descartar, seguir recibiendo alertas con Outlook cerrado, y reiniciar Windows sin tener una consola abierta, duplicar recordatorios ni perder configuración. Debe manejar dos reuniones simultáneas sin romper la UI.

---

## 12. Orden sugerido de implementación para un agente autónomo

1. Crear solución, dependencias, contratos, modelos, build de WPF sin ventana de consola.
2. Tests primero del motor de tiempo/estados; implementar scheduler y recurrencias.
3. Crear fixtures ICS; parser y tests de escenarios complejos.
4. Implementar HTTP sync con cache, timeouts, fallos y protección de URL.
5. SQLite y migraciones; tests de persistencia/recovery/restart.
6. Integrar Windows App SDK toast (`Register`, activación, sonido, acciones), con prueba en Win11 real.
7. Añadir agrupación/concurrencia y tests de lotes.
8. Tray icon, single-instance, menú y WPF Settings/Upcoming con UX consistente.
9. Startup por Task Scheduler per-user, installer, actualizaciones, uninstall.
10. Tests completos, visual QA, revisión de seguridad (URL/DPAPI/logs), memory leaks y análisis estático.
11. Release reproducible, README, documentación y evidencia de resultados.

### Uso de Superpowers (opcional, recomendado)

Si el agente dispone del framework **[obra/superpowers](https://github.com/obra/superpowers)**, **sí puede utilizarlo**. En particular: `writing-plans`, `test-driven-development`, `subagent-driven-development`/`executing-plans`, `requesting-code-review`, `systematic-debugging`, `verification-before-completion`.

La definición de producto y UX **ya está resuelta por este documento**. No bloquear el trabajo en una nueva ronda de brainstorming/preguntas/aprobaciones. Generar internamente el plan y **ejecutarlo** con commits/checkpoints y tests, sin pedir input intermedio. Si faltan credenciales, usar feeds de prueba; el único dato que el usuario cargará luego en UI son las URLs reales de sus calendarios. Mantener el uso de Superpowers como herramienta de proceso, **no como dependencia runtime** de CalendarBell.

### Prompt listo para pegar en Claude Code / Codex / Copilot / otro agente

> Implementá desde cero **CalendarBell** siguiendo íntegramente el archivo `CalendarBell_SPEC_GENERICA.md` de este repositorio. Tratá el documento como contrato de alcance y criterios de aceptación. No hagas prototipos desechables ni simplifiques Snooze, múltiples calendarios, manejo simultáneo, persistencia, auto-start o seguridad. Usá Superpowers para plan, TDD, subagentes, code review y verificación si está instalado, sin pedirme decisiones intermedias porque la especificación ya las define. Creá todo el proyecto .NET 10 WPF, tests, mocks, assets, documentación y artefactos de release. Priorizá un ejecutable Windows real sin consola, icono tray, Settings con CRUD de ICS y notificaciones nativas interactivas. Si una API/paquete cambió, comprobá la documentación oficial actual y adoptá la versión estable compatible, explicando la decisión. Ejecutá build/tests; verificá en Windows lo que el entorno permita; no afirmes que algo pasó si no lo ejecutaste. No termines hasta completar la Definition of Done y entregar instrucciones de instalación/uso y limitaciones reales.

---

## 13. Referencias oficiales de implementación

Estas fuentes establecen **capacidades y limitaciones**, pero si la documentación cambia al momento de implementar prevalecen los contratos de UX anteriores y el equipo debe adaptar la API sin inventar soporte:

1. Microsoft — **Use app notifications with a .NET app** (WPF/WinForms, `Register`, sin elevación): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-dotnet>
2. Microsoft — **Quickstart: App notifications** (activaciones desde toast, botón custom y limitaciones de background): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-quickstart>
3. Microsoft — **App notification content** (template, combo, botones, scenario Reminder, snooze de sistema): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-content>
4. Microsoft — **Manage app notifications** (tags, groups, remover/expirar): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/manage-app-notifications>
5. Microsoft — **App notification headers** (máximo 20 notificaciones de una app, tamaño XML 5 KB): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-headers>
6. Microsoft — **Custom audio on app notifications**: <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-custom-audio>
7. Microsoft — **Schedule an app notification** (ventana de entrega de 5 minutos; no utilizarla como sustituto de los estados de negocio): <https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-scheduled>
8. Microsoft — **Deployment of unpackaged Windows App SDK apps**: <https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/deploy-unpackaged-apps>
9. Microsoft — **Self-contained Windows App SDK deployment**: <https://learn.microsoft.com/en-us/windows/apps/package-and-deploy/self-contained-deploy/deploy-self-contained-apps>
10. Ical.Net — documentación/proyecto: <https://github.com/rianjs/ical.net>
11. Superpowers (opcional para agentes): <https://github.com/obra/superpowers>

---

## 14. Matriz rápida de aceptación para revisión final

- [ ] No aparece consola ni es necesario dejar PowerShell abierta.
- [ ] Un icono tray está siempre visible mientras CalendarBell funciona (salvo ocultamiento automático del área de notificación de Windows).
- [ ] Clic derecho → Settings funciona, y cerrar Settings no mata la app.
- [ ] Se agregan al menos dos calendarios: `Calendar A` y `Calendar B`, cada uno con su nombre, URL, enable y estado.
- [ ] Ambas URLs sobreviven reinicios cifradas y no aparecen en logs.
- [ ] Se refrescan los ICS y los cambios se aplican sin duplicar estados.
- [ ] Reunión 10:30 → aviso 10:15 con sonido y el nombre de calendario correcto.
- [ ] Snooze `10 min before` → 10:20; `5 min before` → 10:25; `At start` → 10:30.
- [ ] Targets vencidos no se ofrecen; Snooze no vuelve inmediatamente ni causa loops.
- [ ] Dismiss permanente y cierre X no hacen rebotar notificaciones tras refresh.
- [ ] Calendar A + Calendar B con recordatorios simultáneos → un resumen claro, no ventanas superpuestas propias.
- [ ] Se puede gestionar por separado cada evento desde Upcoming reminders.
- [ ] Calendario cancelado/cambiado/deshabilitado no emite recordatorios obsoletos.
- [ ] Recurrencias, EXDATE, RECURRENCE-ID, zonas horarias y DST funcionan.
- [ ] Outlook puede estar completamente cerrado y la app sigue avisando.
- [ ] Windows reinicia la app al iniciar sesión, sin admin ni servicio.
- [ ] Existe test notification, diagnóstico, logs seguros y procedimiento de uninstall.
- [ ] `dotnet build`, `dotnet test`, publish e instalador tienen resultados verificables.

**Fin de la especificación.**
