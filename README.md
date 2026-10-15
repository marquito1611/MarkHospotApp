# MarkHotspot

Aplicación nativa para Windows 10 x64 en C# / WPF. Autor: **@marquito1611**.

## Estado actual

Este repositorio está en la fase inicial de implementación. La solución WPF y el flujo de compilación para Windows x64 están creados. La interfaz actual detecta y enumera interfaces de red reales de Windows y permite actualizar la lista. **Todavía no es una alternativa funcional a Mars WiFi**: activar/configurar Mobile Hotspot, detectar clientes conectados de forma fiable, bloqueo real y límites de velocidad por dispositivo aún no están implementados.

No uses esta versión como si ya ofreciera esas funciones.

## Compilación

En Windows con .NET 8 SDK:

```powershell
dotnet restore MarkHotspot.sln
dotnet build MarkHotspot.sln --configuration Release -p:Platform=x64
dotnet publish src/MarkHotspot.App/MarkHotspot.App.csproj --configuration Release --runtime win-x64 --self-contained true -p:Platform=x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --output artifacts/MarkHotspot
```

GitHub Actions compila y publica el directorio autocontenido como artefacto descargable en cada push relevante a `main`, pull request o ejecución manual. El artefacto solo existirá después de que una ejecución termine correctamente.

## Arquitectura y siguientes hitos

1. Integrar y probar la API de Windows Mobile Hotspot / NetworkOperatorTetheringManager para estado, SSID, credenciales e inicio/parada, según las capacidades del sistema.
2. Implementar detección de clientes activos usando fuentes reales del subsistema de tethering, DHCP y tablas de vecinos, con estado de confianza y expiración.
3. Crear persistencia de identidad, alias, reglas, lista negra y estadísticas.
4. Implementar un motor de filtrado y shaping por cliente con validación en tráfico reenviado real. La elección entre Windows Filtering Platform y un componente auxiliar debe validarse en Windows 10; no se declarará bloqueo o limitación funcional sin pruebas con clientes físicos.
5. Añadir telemetría, manejo de errores, pruebas automatizadas y documentación de instalación/desinstalación del componente de filtrado si se necesita.

## Seguridad y limitaciones

- No se desactiva globalmente Windows Firewall.
- No se afirma que ARP por sí solo identifique a todos los clientes.
- Una lista negra persistida no equivale a un bloqueo de red efectivo.
- Las cifras de tráfico y límites no se mostrarán como funcionales hasta que se midan desde el motor de tráfico.
