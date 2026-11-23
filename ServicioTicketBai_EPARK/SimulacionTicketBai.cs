using easy;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text;

namespace ServicioTicketBai_EPARK
{
    internal sealed class SimulacionTicketBai : IRegistroEnviosTicketBai
    {
        private readonly bool rechazar;
        private readonly TicketBai pendiente = new TicketBai
        {
            Ejercicio = 2026,
            Serie = "SIM",
            Factura = 1,
            Tipo = "T",
            Estado = 0
        };

        internal SimulacionTicketBai(bool rechazar)
        {
            this.rechazar = rechazar;
        }

        internal int Estado => pendiente.Estado;
        internal int Intentos { get; private set; }

        internal TicketBai[] DamePendientes()
        {
            Console.WriteLine("Lectura simulada del ticket {0}-{1}, estado {2}.",
                pendiente.Serie, pendiente.Factura, pendiente.Estado);
            return pendiente.Estado == 0 ? new[] { pendiente } : Array.Empty<TicketBai>();
        }

        internal TicketCompleto DameTicketCompleto()
        {
            return new TicketCompleto
            {
                Cabecera = new Ticket
                {
                    Ejercicio = pendiente.Ejercicio,
                    Serie = pendiente.Serie,
                    Numero = pendiente.Factura,
                    Fecha = "20260115",
                    BaseImponible = 10,
                    TantoIVA = 21,
                    Total = 12.10
                }
            };
        }

        internal string Responder(byte[] datos)
        {
            if (Intentos == 1)
            {
                var json = JObject.Parse(Encoding.UTF8.GetString(datos));
                Console.WriteLine("JSON generado para el envío simulado:");
                Console.WriteLine(json.ToString(Formatting.Indented));
            }

            string respuesta = rechazar
                ? @"{""result"":""ERROR"",""msg"":""Rechazo simulado de TicketBAI: identificación del documento incorrecta.""}"
                : @"{""result"":""PENDING"",""return"":{""qr"":""UVIgU0lNVUxBRE8="",""huella_tbai"":""FIRMA-SIMULADA""}}";
            Console.WriteLine("Respuesta simulada: " + respuesta);
            return respuesta;
        }

        public bool RegistrarIntento(TicketBai documento, int limite)
        {
            if (documento.Estado != 0 || Intentos >= limite)
                return false;

            Intentos++;
            Console.WriteLine("Intento simulado {0} de {1}.", Intentos, limite);
            return true;
        }

        public bool GuardarEnvio(TicketBai documento, string qr, string firma)
        {
            documento.Estado = 1;
            documento.QR = qr;
            documento.Firma = firma;
            Console.WriteLine("Guardado en memoria de {0}-{1}: estado {2}, QR {3}, firma {4}.",
                documento.Serie, documento.Factura, documento.Estado, documento.QR, documento.Firma);
            return true;
        }

        public void GuardarFallo(TicketBai documento, string motivo, int limite)
        {
            documento.Respuesta = true;
            documento.Mensaje = motivo;
            if (Intentos < limite)
                return;

            documento.Estado = 77;
            Console.WriteLine("Aviso simulado de {0}-{1}, estado {2}. Motivo: {3}",
                documento.Serie, documento.Factura, documento.Estado, motivo);
        }
    }
}
