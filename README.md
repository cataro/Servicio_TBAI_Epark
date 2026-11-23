# Servicio TBAI Epark

Servicio de Windows en C# (.NET Framework 4.7.2) que prepara y envía tickets, facturas y remesas a TicketBAI.

## Compilar y probar en local

1. Abre `ServicioTicketBai_EPARK.sln` en Visual Studio con las herramientas de .NET Framework 4.7.2 y restaura los paquetes NuGet.
2. Coloca una versión compatible de la biblioteca privada `easy.dll` y su dependencia `KPLock.dll` en `packages/easy/`, o indica la ruta de `easy.dll` con la propiedad de MSBuild `EasyAssemblyPath`.
3. Selecciona **Debug**, establece `ServicioTicketBai_EPARK` como proyecto de inicio y pulsa **Ctrl+F5** para procesar un ticket simulado. El ejecutable Debug admite `--simular-rechazo` para comprobar en memoria el límite, el estado 77 y el aviso. Ambos modos funcionan sin SQL Server ni tráfico a TicketBAI o SMTP.

`Setup_EPARK` contiene el proyecto de instalación y una copia anterior de la solución. Para compilar el servicio actual, usa la solución de la raíz.

## Configurar el servicio

- Completa las URL, el NIF y el token en la configuración local del ejecutable. Los valores del repositorio son de ejemplo. La biblioteca `easy.dll` determina la conexión a SQL Server.
- Aplica `ServicioTicketBai_EPARK/TicketBai_Intentos.sql` en la base que usa el servicio. El script añade el contador, el motivo completo del último fallo y el indicador de aviso pendiente.
- `TicketBaiMaxIntentos` fija el número máximo de intentos. El valor inicial es 5. Tras agotar el límite, el documento pasa a estado 77 y deja de enviarse automáticamente.
- Configura `TicketBaiAvisoPara` y la sección de SMTP `system.net/mailSettings` en la configuración local. El aviso incluye el documento y el motivo. Si el correo falla, el aviso queda pendiente para un ciclo posterior.

Las credenciales y los binarios privados se gestionan fuera de Git.
