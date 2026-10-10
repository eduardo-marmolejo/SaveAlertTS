# SaveAlertTS

Notificación de recordatorio de guardado para **Tekla Structures 2025**.

## Descripción

SaveAlertTS es una aplicación de bandeja del sistema que monitorea las sesiones de Tekla Structures y muestra una alerta cada vez que se abre o recarga un modelo, recordándote guardar tu trabajo antes de continuar.

## Funcionalidades

- **Detección de modelos nuevos**: Alerta automáticamente cuando se abre un modelo en Tekla Structures.
- **Detección de recarga**: Detecta cuando se cierra y vuelve a abrir el mismo modelo dentro de la misma sesión.
- **Monitoreo por WinEvent**: Usa eventos del sistema operativo para detectar cambios en el título de las ventanas de Tekla en tiempo real.
- **Conexión a Tekla API**: Se conecta a la API de Tekla Structures para registrar eventos de carga de modelo (`OnModelLoad`).
- **Ejecución automática**: Se inicia con Windows y espera silenciosamente en la bandeja del sistema hasta que Tekla esté abierto.
- **Instalador incluido**: Incluye un script de Inno Setup para distribución sencilla.

## Requisitos

- Windows 10/11
- .NET Framework 4.8
- Tekla Structures 2025.0 instalado en `C:\Program Files\Tekla Structures\2025.0\`

## Instalación

1. Descarga el instalador desde la sección [Releases](../../releases) de este repositorio.
2. Si tienes una versión antigua, primero desinstala la versión antigua.
3. Ejecuta `SaveAlertTS_Setup.exe`.
4. Sigue las instrucciones del instalador.
5. La aplicación se iniciará automáticamente con Windows.

## Compilación

1. Abre `SaveAlertTS.slnx` en Visual Studio 2022.
2. Selecciona la configuración **Release | x64**.
3. Build → Build Solution (Ctrl+Shift+B).
4. El ejecutable se genera en `bin\x64\Release\SaveAlertTS.exe`.

## Uso

1. La aplicación se ejecuta en la bandeja del sistema al iniciar Windows.
2. Abre Tekla Structures y carga un modelo.
3. Aparecerá una ventana de alerta recordándote guardar tu trabajo.
4. Haz clic en **OK** para cerrar la alerta.
5. Para salir, haz clic derecho en el ícono de la bandeja y selecciona **Salir**.

## Arquitectura

| Archivo | Descripción |
|---------|-------------|
| `Program.cs` | Punto de entrada con Mutex para instancia única |
| `TeklaWatcher.cs` | Lógica principal: detección de modelos, WinEvents, conexión a Tekla API |
| `Form1.cs` | Formulario de alerta con botón OK |
| `Form1.Designer.cs` | Diseño del formulario de alerta |

## Autor

**Eduardo Marmolejo Ornelas** - 2026

## Licencia

Todos los derechos reservados. Este software no está bajo ninguna licencia open source.
