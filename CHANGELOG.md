# Changelog

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
