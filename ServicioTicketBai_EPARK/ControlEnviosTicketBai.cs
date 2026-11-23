using easy;
using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Reflection;

namespace ServicioTicketBai_EPARK
{
    internal interface IRegistroEnviosTicketBai
    {
        bool RegistrarIntento(TicketBai documento, int limite);
        bool GuardarEnvio(TicketBai documento, string qr, string firma);
        void GuardarFallo(TicketBai documento, string motivo, int limite);
    }

    internal sealed class ControlEnviosTicketBai : IRegistroEnviosTicketBai
    {
        private const string ClaveDocumento =
            "Ejercicio = @ejercicio AND Serie = @serie AND Factura = @factura AND Tipo = @tipo";

        private readonly SqlConnection conexion;

        internal ControlEnviosTicketBai(SqlConnection conexion)
        {
            this.conexion = conexion;
        }

        internal static SqlConnection CrearConexion()
        {
            // La DLL heredada no expone públicamente el módulo que usa para sus consultas.
            Type funciones = typeof(TicketBai).Assembly.GetType("easy.Funciones");
            MethodInfo obtenerConexion = funciones?.GetMethod("dame_conexion", BindingFlags.Public | BindingFlags.Static);
            string cadena = obtenerConexion?.Invoke(null, null) as string;
            if (string.IsNullOrWhiteSpace(cadena))
                throw new InvalidOperationException("No se pudo obtener la conexión a SQL Server de easy.dll.");

            // Cerrar la conexión física libera el bloqueo de sesión del ciclo.
            var opciones = new SqlConnectionStringBuilder(cadena) { Pooling = false };
            return new SqlConnection(opciones.ConnectionString);
        }

        internal bool TomarTurno()
        {
            using (var comando = new SqlCommand(@"
                DECLARE @resultado int;
                EXEC @resultado = sys.sp_getapplock
                    @Resource = N'ServicioTicketBai_EPARK.Envios', @LockMode = 'Exclusive',
                    @LockOwner = 'Session', @LockTimeout = 0;
                SELECT @resultado;", conexion))
            {
                int resultado = (int)comando.ExecuteScalar();
                if (resultado < -1)
                    throw new InvalidOperationException("No se pudo adquirir el turno de envíos TicketBAI. Código SQL " + resultado);
                return resultado >= 0;
            }
        }

        public bool RegistrarIntento(TicketBai documento, int limite)
        {
            using (var comando = ComandoDocumento(@"
                UPDATE TicketBai
                SET IntentosEnvio = IntentosEnvio + 1,
                    UltimoErrorEnvio = N'El último intento quedó interrumpido sin una respuesta registrada.'
                WHERE Estado = 0 AND IntentosEnvio < @limite AND " + ClaveDocumento + "; SELECT @@ROWCOUNT;", documento))
            {
                comando.Parameters.Add("@limite", SqlDbType.Int).Value = limite;
                return (int)comando.ExecuteScalar() > 0;
            }
        }

        public bool GuardarEnvio(TicketBai documento, string qr, string firma)
        {
            using (var comando = ComandoDocumento(@"
                UPDATE TicketBai
                SET QR = @qr, Firma = @firma, Estado = 1, Respuesta = 1, Mensaje = N'',
                    UltimoErrorEnvio = NULL, AvisoEnvioPendiente = 0
                WHERE Estado = 0 AND " + ClaveDocumento + "; SELECT @@ROWCOUNT;", documento))
            {
                comando.Parameters.Add("@qr", SqlDbType.NVarChar, -1).Value = qr;
                comando.Parameters.Add("@firma", SqlDbType.NVarChar, -1).Value = firma;
                return (int)comando.ExecuteScalar() > 0;
            }
        }

        public void GuardarFallo(TicketBai documento, string motivo, int limite)
        {
            using (var comando = ComandoDocumento(@"
                UPDATE TicketBai
                SET Respuesta = 1, Mensaje = @mensaje, UltimoErrorEnvio = @motivo,
                    Estado = CASE WHEN IntentosEnvio >= @limite THEN 77 ELSE Estado END,
                    AvisoEnvioPendiente = CASE WHEN IntentosEnvio >= @limite THEN 1 ELSE AvisoEnvioPendiente END
                WHERE Estado = 0 AND " + ClaveDocumento, documento))
            {
                comando.Parameters.Add("@limite", SqlDbType.Int).Value = limite;
                comando.Parameters.Add("@mensaje", SqlDbType.NVarChar, 200).Value = motivo;
                comando.Parameters.Add("@motivo", SqlDbType.NVarChar, -1).Value = motivo;
                comando.ExecuteNonQuery();
            }
        }

        internal void MarcarAgotados(int limite)
        {
            using (var comando = new SqlCommand(@"
                UPDATE TicketBai
                SET Estado = 77, Respuesta = 1, AvisoEnvioPendiente = 1,
                    UltimoErrorEnvio = COALESCE(UltimoErrorEnvio, N'Se alcanzó el límite de intentos.')
                WHERE Estado = 0 AND IntentosEnvio >= @limite;", conexion))
            {
                comando.Parameters.Add("@limite", SqlDbType.Int).Value = limite;
                comando.ExecuteNonQuery();
            }
        }

        internal List<(TicketBai Documento, int Intentos, string Motivo)> LeerAvisos()
        {
            var avisos = new List<(TicketBai, int, string)>();
            using (var comando = new SqlCommand(@"
                SELECT Ejercicio, Serie, Factura, Tipo, IntentosEnvio, UltimoErrorEnvio
                FROM TicketBai
                WHERE Estado = 77 AND AvisoEnvioPendiente = 1;", conexion))
            using (var lector = comando.ExecuteReader())
            {
                while (lector.Read())
                {
                    avisos.Add((new TicketBai
                    {
                        Ejercicio = Convert.ToInt32(lector.GetValue(0)),
                        Serie = lector.GetString(1),
                        Factura = Convert.ToInt64(lector.GetValue(2)),
                        Tipo = lector.GetString(3)
                    }, lector.GetInt32(4), lector.GetString(5)));
                }
            }
            return avisos;
        }

        internal void ConfirmarAviso(TicketBai documento)
        {
            using (var comando = ComandoDocumento(@"
                UPDATE TicketBai SET AvisoEnvioPendiente = 0
                WHERE Estado = 77 AND " + ClaveDocumento, documento))
            {
                comando.ExecuteNonQuery();
            }
        }

        private SqlCommand ComandoDocumento(string sql, TicketBai documento)
        {
            var comando = new SqlCommand(sql, conexion);
            comando.Parameters.Add("@ejercicio", SqlDbType.Int).Value = documento.Ejercicio;
            comando.Parameters.Add("@serie", SqlDbType.NVarChar, 3).Value = documento.Serie;
            comando.Parameters.Add("@factura", SqlDbType.BigInt).Value = documento.Factura;
            comando.Parameters.Add("@tipo", SqlDbType.NVarChar, 1).Value = documento.Tipo;
            return comando;
        }
    }
}
