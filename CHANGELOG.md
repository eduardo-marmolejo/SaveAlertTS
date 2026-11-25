# Changelog

## [1.2.1] - 2026-11-24

### Added

- **Cuadro nativo "Model revision comment"**: al aceptar la alerta, `SaveBridge` despliega el macro `SaveAlert_NativeSave.cs` en `XS_MACRO_DIRECTORY\modeling` y lo ejecuta con `Operation.RunMacro`. El guardado real se confirma mediante `ModelSave`; si el cuadro se cancela o cierra, un watchdog de 120 s finaliza sin guardar.
- **Respuesta temprana `MODE=dialog`**: el watcher distingue el inicio del cuadro del guardado confirmado y registra "Cuadro nativo iniciado (guardado manual pendiente)" en lugar de un "Guardado exitoso" prematuro.
- **Deteccion de modelo compartido**: `SaveBridge` usa `ModelInfo.SharedModel` / `SingleUserModel` y escribe el marcador `SHARED=true|false`.

### Fixed

- **Alerta tardia al numerar/cancelar y abrir dibujos**: la rama suprimida de `ProcessModelState` limpia tambien `_pendingRealLoad` y `_lastUnloadingTicks`, evitando que una carga suprimida dispare el marcador al vencer la supresion.
- **Carrera de `ModelSave` durante `RunMacro`**: el watchdog se arma despues de despachar el macro, para no confundir un `ModelSave` atrasado con la confirmacion del dialogo actual.
- **Falsas alertas por vistas y dibujos**: el hueco `ModelUnloading -> ModelLoad` menor a 4000 ms se clasifica como espurio; supresiones de 5 s por `ModelSave`, 15 s por `Numbering`, 10 s por conversion y 12 s post-guardado.
- **Cierre con X de la alerta**: el modelo cargado no vuelve a avisar.

### Changed

- Version del instalador y del ensamblado actualizada a 1.2.1.
- `TeklaSaveService` expone `LastResponseMode` y registra el `MODE` de la respuesta del bridge.
- `Form1` propaga `SaveResponseMode` al watcher y `TeklaWatcher.ModelSaved` ajusta el mensaje segun el modo.

## [1.2.0.0] - 2026-08-02

### Added

- **Soporte multi-idioma (español / inglés)**: el instalador permite elegir el idioma y tanto la ventana de alerta como el icono de la bandeja del sistema se muestran en el idioma seleccionado (`Localization.cs` y archivo `lang.txt`).
- **Compatibilidad con Tekla Structures 2024 y Tekla Structures 2026**, además de Tekla Structures 2025.
- **Reapertura confiable del mismo modelo**: `SaveBridge` ahora registra `Events.ModelLoad` dentro de la sesion de Tekla y escribe un marcador IPC (`model_<pid>.txt`) en cada carga de modelo, incluida la reapertura del mismo modelo en la misma sesion. El watcher lee los marcadores como disparador determinista de la alerta, eliminando la dependencia del titulo de ventana.
- Polling de respaldo mas rapido (cada 800 ms) en `SaveBridge` por si el evento no se entrega.

### Fixed

- **La alerta ya no se dispara al abrir dibujos**: solo se activa al cargar o reabrir un modelo.
- Centrado correcto de los textos en la ventana de alerta.

### Changed

- `SaveBridge.cs`: registro de `Events.ModelLoad` y escritura del marcador `model_<pid>.txt` en `%LOCALAPPDATA%\SaveAlertTS\bridge`.
- `TeklaWatcher.cs`: nuevo `ProcessBridgeMarkers()` que consume los marcadores y dispara `ShowReminder`; deduplicación por marcador para evitar alertas repetidas por la misma carga; la deteccion por titulo queda como respaldo/limpieza de sesiones.
- `Form1.cs`: textos localizados y centrado de controles.
- Instalador v1.2.0.0: selección de idioma y escritura del archivo `lang.txt`.

## [1.1.0.0] - 2026-08-02

### Added

- **SaveBridge** (`Bridge/SaveBridge.cs`): macro de Tekla que se ejecuta automáticamente al iniciar cada sesión (vía `-m` en los accesos directos) y vigila un directorio IPC (`%LOCALAPPDATA%\SaveAlertTS\bridge`) esperando pedidos de guardado dirigidos a su PID. Ejecuta el guardado nativo (`ModelHandler.Save`) dentro del proceso correcto de Tekla.
- **Guardado nativo por sesión**: al presionar OK en la alerta, SaveAlertTS escribe un pedido con el PID de la sesión objetivo; el `SaveBridge` de esa sesión guarda el modelo y responde por archivo. Correcto con múltiples sesiones y al cambiar/reabrir modelos en una misma sesión.
- **`SaveAlertShortcutPatcher`**: herramienta que agrega/remueve `-m "<ruta>\SaveBridge.cs"` en los accesos directos de `TeklaStructures.exe` (escritorio, menú Inicio y barra de tareas). Se ejecuta desde el instalador al instalar/desinstalar.

### Changed

- `TeklaSaveService.SaveCurrentModel` ahora recibe el PID objetivo y usa el puente por archivo en lugar del `ModelHandler` externo (que no puede apuntar a una instancia específica).
- `TeklaWatcher` ahora identifica las sesiones por PID y dirige la alerta a la sesión que disparó el recordatorio.
- Instalador v1.1.0.0: instala `SaveBridge.cs` y el patcher, parchea los accesos directos al instalar y los restaura al desinstalar.

## [1.0.0] - 2026-07-20

### Added

- Monitoreo de sesiones de Tekla Structures 2025 en tiempo real.
- Alerta automática al abrir un modelo nuevo.
- Detección de reapertura del mismo modelo dentro de la misma sesión usando WinEvents del sistema operativo.
- Conexión a Tekla API (`Tekla.Structures.Model`) para registro de eventos `OnModelLoad` como mecanismo de respaldo.
- Ejecución automática al iniciar Windows via acceso directo en `shell:startup`.
- Instalador con Inno Setup.
- Mutex para evitar múltiples instancias simultáneas.
- Icono en la bandeja del sistema con menú contextual para salir.
