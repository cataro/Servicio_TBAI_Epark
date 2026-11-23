# Servicio TBAI Epark

Servicio de Windows en C# (.NET Framework 4.7.2) que prepara y envía tickets, facturas y remesas a TicketBAI.

## Compilar

1. Abre `ServicioTicketBai_EPARK.sln` en Visual Studio con las herramientas de .NET Framework 4.7.2 y restaura los paquetes NuGet.
2. Coloca una versión compatible de la biblioteca privada `easy.dll` y su dependencia `KPLock.dll` en `packages/easy/`, o indica la ruta de `easy.dll` con la propiedad de MSBuild `EasyAssemblyPath`.
3. Compila en **Release** para el despliegue. El ejecutable se ejecuta como servicio de Windows.

`Setup_EPARK` contiene el proyecto de instalación y una copia anterior de la solución. Para compilar el servicio actual, usa la solución de la raíz.

## Configurar el servicio

- Completa las URL, el NIF y el token en la configuración local del ejecutable. Los valores del repositorio son de ejemplo. La biblioteca `easy.dll` determina la conexión a SQL Server.
- Aplica `ServicioTicketBai_EPARK/TicketBai_Intentos.sql` en la base que usa el servicio. El script añade el contador, el último motivo de fallo y el indicador de aviso pendiente.
- `TicketBaiMaxIntentos` fija el límite de intentos. El valor inicial es 5. Si falla el último intento permitido, el documento pasa a estado 77 y deja de enviarse automáticamente.
- Configura `TicketBaiAvisoPara` y `system.net/mailSettings` en la configuración local para activar el correo. El aviso incluye el documento y el motivo del fallo. Si SMTP falla, el aviso queda pendiente.

Gestiona las credenciales y los binarios privados fuera de Git.
