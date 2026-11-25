# Changelog

## [1.2.1] - 2026-11-24

### Added

- **Cuadro nativo "Model revision comment"**: al aceptar la alerta, `SaveBridge` despliega el macro `SaveAlert_NativeSave.cs` en `XS_MACRO_DIRECTORY\modeling` y lo ejecuta con `Operation.RunMacro`. El guardado real se confirma mediante `ModelSave`; si el cuadro se cancela o cierra, un watchdog de 120 s finaliza sin guardar.
- **Respuesta temprana `MODE=dialog`**: el watcher distingue el inicio del cuadro del guardado confirmado y registra "Cuadro nativo iniciado (guardado manual pendiente)" en lugar de un "Guardado exitoso" prematuro.
- **Deteccion de modelo compartido**: `SaveBridge` usa `ModelInfo.SharedModel` / `SingleUserModel` y escribe el marcador `SHARED=true|false`.

### Errores conocidos

Problemas reportados antes de esta version, agrupados los que comparten causa. Todos quedan corregidos en esta version; se listan aqui como referencia.

1. **Guardado y cambio de tipo de modelo**: la alerta volvia a aparecer despues de guardar en un modelo compartido y al pasar de multiusuario a usuario único.
2. **Numeracion**: aparecia la alerta al numerar en multiusuario y, tras numerar/cancelar, salia una alerta tardia al abrir dibujos al vencer la supresion.
3. **Vistas y dibujos**: los movimientos rapidos de vistas y el abrir/cerrar dibujos disparaban la alerta falsamente.
4. **Cuadro de guardado**: el botón OK guardaba directo sin mostrar el cuadro nativo; el cuadro correcto debe llamarse **Model revision comment**.
5. **Cierre con X de la alerta**: al descartarla con la X mientras cargaba o ya cargado, volvia a aparecer.
6. **Cuadro nativo cancelado**: cerrar con X el cuadro **Model revision comment** no debia contarse como guardado.
7. **Carrera en el despacho del macro**: un `ModelSave` atrasado podia confundirse con la confirmacion del cuadro recien abierto.

### Correcciones

- **Grupo 1 (guardado / cambio de tipo de modelo)**: `SaveBridge` detecta `ModelInfo.SharedModel` / `SingleUserModel` y escribe `SHARED=true|false`; supresion de 10 s en la transicion y de 12 s post-guardado en el watcher.
- **Grupo 2 (numeracion)**: supresion de 15 s por `Numbering`; la rama suprimida de `ProcessModelState` limpia `_pendingRealLoad` y `_lastUnloadingTicks` para que no aparezca la alerta tardia al vencer la supresion.
- **Grupo 3 (vistas y dibujos)**: el hueco `ModelUnloading -> ModelLoad` menor a 4000 ms se clasifica como espurio y se normaliza el titulo para no disparar por dibujos; supresiones de 5 s por `ModelSave`, 10 s por conversion y 12 s post-guardado.
- **Grupo 4 (cuadro nativo)**: macro embebido desplegado en `XS_MACRO_DIRECTORY\modeling` e invocado con `Operation.RunMacro`; `IMacroRuntime.RunUI` + `akit.Callback("acmd_save_model", "", "main_frame")`. Cuadro: **Model revision comment**.
- **Grupo 5 (X en la alerta)**: `DismissModel` marca el modelo cargado como descartado; no vuelve a avisar.
- **Grupo 6 (X en el cuadro nativo)**: watchdog de 120 s; si no llega `ModelSave`, el resultado es `timeout_or_cancelled` y no se reporta guardado.
- **Grupo 7 (carrera)**: el watchdog se arma despues de `Operation.RunMacro`, no antes.

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
