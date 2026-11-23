using easy;
using Microsoft.VisualBasic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Configuration;
using System.Data;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mail;
using System.ServiceModel;
using System.ServiceProcess;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ServicioTicketBai_EPARK
{
    public partial class TicketBai_EPARK : ServiceBase
    {
        private const int LimitePredeterminado = 5;
        private int cicloActivo;
        readonly int maxIntentos;
        readonly string destinatariosAviso;
        readonly string Nif;
        readonly string token;
        readonly Uri URI;
        readonly Uri URIRemesa;
        readonly SimulacionTicketBai simulacion;
        //install servicio
        internal static ServiceHost hostService = null;

        public TicketBai_EPARK()
        {
            string url = ConfigurationManager.AppSettings["urlTbai"].ToString();
            string urlRemesa = ConfigurationManager.AppSettings["urlTbaiRemesa"].ToString();
            Nif = ConfigurationManager.AppSettings["nif"].ToString();
            token = ConfigurationManager.AppSettings["token"].ToString();
            string limite = ConfigurationManager.AppSettings["TicketBaiMaxIntentos"];
            if (!int.TryParse(limite ?? LimitePredeterminado.ToString(), out maxIntentos) || maxIntentos < 1)
                throw new ConfigurationErrorsException("TicketBaiMaxIntentos debe ser un entero mayor que cero.");
            destinatariosAviso = ConfigurationManager.AppSettings["TicketBaiAvisoPara"];
            long intervalo = Convert.ToInt64(ConfigurationManager.AppSettings["intervalo_milisegundos"]);
            InitializeComponent();
            URI = new Uri(url);
            URIRemesa = new Uri(urlRemesa);
            timer1.Interval = intervalo;
        }

        private TicketBai_EPARK(SimulacionTicketBai simulacion)
        {
            this.simulacion = simulacion;
            maxIntentos = LimitePredeterminado;
        }

        internal static int EjecutarSimulacion(bool rechazar)
        {
            var simulacion = new SimulacionTicketBai(rechazar);
            using (var servicio = new TicketBai_EPARK(simulacion))
            {
                Console.WriteLine("Simulación TicketBAI. Lectura, envío y guardado en memoria.");
                if (rechazar)
                {
                    for (int ciclo = 0; ciclo <= servicio.maxIntentos; ciclo++)
                        servicio.ProcesarPendientes(simulacion.DamePendientes(), simulacion);
                    return simulacion.Estado == 77 && simulacion.Intentos == servicio.maxIntentos ? 0 : 1;
                }

                int enviados = servicio.ProcesarPendientes(simulacion.DamePendientes(), simulacion);
                Console.WriteLine("Tickets procesados correctamente: " + enviados);
                return enviados == 1 ? 0 : 1;
            }
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                //iniciar servicio
                hostService = new ServiceHost(typeof(WcfServiceLibraryEPARK.Service1));
                WcfServiceLibraryEPARK.Service1 mgmtService = new WcfServiceLibraryEPARK.Service1();
                hostService.Open();
                EventLog.WriteEntry("Se ha inicidado el servicio");
                timer1.Start();
            }
            catch (Exception ex)
            {
                EscribirLog("ERROR al iniciar el servicio");
                throw new Exception(ex.Message);
            }
        }

        protected override void OnStop()
        {
            timer1.Stop();
            hostService.Close();
            EventLog.WriteEntry("Se ha finalizado el servicio");
        }

        private void timer1_Elapsed_1(object sender, System.Timers.ElapsedEventArgs e)
        {
            if (Interlocked.Exchange(ref cicloActivo, 1) != 0)
                return;

            try
            {
                ProcesarCiclo();
            }
            catch (Exception ex)
            {
                EventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
            }
            finally
            {
                Interlocked.Exchange(ref cicloActivo, 0);
            }
        }

        private void ProcesarCiclo()
        {
            using (var conexion = ControlEnviosTicketBai.CrearConexion())
            {
                conexion.Open();
                var control = new ControlEnviosTicketBai(conexion);
                if (!control.TomarTurno())
                    return;

                control.MarcarAgotados(maxIntentos);
                ProcesarPendientes(new TicketBai().DameTicketsBaiPendientes(), control);
                EnviarAvisos(control);
            }
        }

        private int ProcesarPendientes(TicketBai[] pendientes, IRegistroEnviosTicketBai registro)
        {
            if (pendientes == null)
                return 0;

            int enviados = 0;
            foreach (TicketBai pendiente in pendientes)
            {
                if (EnviarTicketBai(pendiente, registro))
                    enviados++;
                else
                    RegistrarEvento("No se ha podido generar TicketBai de la Factura: " + pendiente.Factura, EventLogEntryType.Warning);
            }
            return enviados;
        }

        private bool EnviarTicketBai(TicketBai documento, IRegistroEnviosTicketBai registro)
        {
            if (simulacion != null && documento.Tipo != "T")
                throw new InvalidOperationException("La simulación admite tickets simplificados.");

            if (documento.Tipo == "T")
            {
                TicketCompleto ticket = simulacion == null
                    ? new Ticket().DameTicketCompleto(new NumeroDocumento(documento.Ejercicio, documento.Serie, documento.Factura))
                    : simulacion.DameTicketCompleto();
                if (ticket == null)
                    return false;

                return EnviarDocumento(documento, ComponerTicketTicketBai(ticket), URI, registro);
            }

            if (documento.Tipo != "F")
                return false;

            FacturaCompleta factura = new Fac().DameFacturaCompleta(documento.Ejercicio, documento.Serie, documento.Factura, 1);
            if (factura == null)
                return false;

            return documento.Remesa
                ? EnviarDocumento(documento, ComponerFacturaTicketBaiRemesa(factura), URIRemesa, registro)
                : EnviarDocumento(documento, ComponerFacturaTicketBai(factura), URI, registro);
        }

        private bool EnviarDocumento(TicketBai documento, object datos, Uri destino, IRegistroEnviosTicketBai registro)
        {
            if (datos == null)
                return false;

            byte[] json = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(datos));
            if (!registro.RegistrarIntento(documento, maxIntentos))
                return false;

            string motivo;
            try
            {
                JObject respuesta = JObject.Parse(SendRequest(destino, json, "application/json", "POST"));
                string resultado = (string)respuesta["result"];
                bool aceptado = resultado == "PENDING";
                string rechazo = LeerMensaje(respuesta);
                if (resultado == "ERROR" && !string.IsNullOrEmpty(rechazo) &&
                    rechazo.IndexOf("LROEPJ240FacturasEmitidasConSGAltaRespuesta", StringComparison.Ordinal) >= 0)
                {
                    // El documento ya existe en el proveedor. Recuperar el QR y la firma no cuenta como otro envío.
                    Uri consulta = CrearConsultaDocumento(documento);
                    respuesta = JObject.Parse(SendRequest(consulta, null, "application/json", "GET"));
                    resultado = (string)respuesta["result"];
                    aceptado = resultado == "OK";
                    rechazo = LeerMensaje(respuesta) ?? rechazo;
                }

                if (aceptado)
                {
                    JObject contenido = respuesta["return"] as JObject;
                    string qr = (string)contenido?["qr"];
                    string firma = (string)contenido?["huella_tbai"];
                    if (string.IsNullOrWhiteSpace(qr) || string.IsNullOrWhiteSpace(firma))
                        motivo = "La respuesta de TicketBAI no contiene el QR o la firma.";
                    else if (registro.GuardarEnvio(documento, qr, firma))
                        return true;
                    else
                        motivo = "TicketBAI devolvió el QR y la firma, pero no se pudo confirmar el envío en SQL Server.";
                }
                else
                {
                    motivo = string.IsNullOrWhiteSpace(rechazo)
                        ? "Respuesta de TicketBAI: " + (resultado ?? "sin campo result")
                        : rechazo;
                }
            }
            catch (WebException ex)
            {
                motivo = ObtenerErrorHttp(ex);
            }
            catch (Exception ex)
            {
                motivo = ex.Message;
            }

            registro.GuardarFallo(documento, motivo, maxIntentos);
            return false;
        }

        private Uri CrearConsultaDocumento(TicketBai documento)
        {
            if (simulacion != null)
                return null;

            var consulta = new UriBuilder(URI);
            string parametros = "serie=" + Uri.EscapeDataString(documento.Serie)
                + "&numero=" + documento.Factura.ToString(System.Globalization.CultureInfo.InvariantCulture);
            consulta.Query = consulta.Query.Length > 1 ? consulta.Query.Substring(1) + "&" + parametros : parametros;
            return consulta.Uri;
        }

        private static string ObtenerErrorHttp(WebException error)
        {
            if (error.Response == null)
                return error.Message;

            try
            {
                using (WebResponse respuesta = error.Response)
                using (Stream contenido = respuesta.GetResponseStream())
                {
                    if (contenido == null)
                        return error.Message;

                    using (var lector = new StreamReader(contenido))
                    {
                        string texto = lector.ReadToEnd();
                        if (respuesta.ContentType?.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase) == true
                            && !string.IsNullOrWhiteSpace(texto))
                            return texto;

                        string mensaje = LeerMensaje(JObject.Parse(texto));
                        return string.IsNullOrWhiteSpace(mensaje) ? error.Message : mensaje;
                    }
                }
            }
            catch (Exception ex) when (ex is JsonException || ex is IOException || ex is InvalidCastException ||
                                       ex is InvalidOperationException || ex is ArgumentException)
            {
                return error.Message;
            }
        }

        private static string LeerMensaje(JObject respuesta)
        {
            JToken mensaje = respuesta["msg"];
            if (mensaje == null || mensaje.Type == JTokenType.Null)
                return null;

            return mensaje.Type == JTokenType.String ? (string)mensaje : mensaje.ToString(Formatting.None);
        }

        private void EnviarAvisos(ControlEnviosTicketBai control)
        {
            if (string.IsNullOrWhiteSpace(destinatariosAviso))
                return;

            foreach (var aviso in control.LeerAvisos())
            {
                try
                {
                    EnviarAviso(destinatariosAviso, aviso.Documento, aviso.Intentos, aviso.Motivo);
                    control.ConfirmarAviso(aviso.Documento);
                }
                catch (Exception ex)
                {
                    RegistrarEvento("No se pudo completar el aviso de TicketBAI. Queda pendiente: " + ex.Message,
                        EventLogEntryType.Error);
                    return;
                }
            }
        }

        internal static void EnviarAviso(string destinatarios, TicketBai documento, int intentos, string motivo)
        {
            string referencia = string.Format("{0}/{1}-{2} (tipo {3})",
                documento.Ejercicio, documento.Serie, documento.Factura, documento.Tipo);
            using (var mensaje = new MailMessage())
            using (var smtp = new SmtpClient())
            {
                mensaje.To.Add(destinatarios);
                mensaje.Subject = "TicketBAI: documento bloqueado " + referencia;
                mensaje.Body = string.Format(
                    "No se ha podido completar el envío a TicketBAI del documento {0} tras {1} intento(s).{2}" +
                    "El documento está en estado 77 y no se volverá a enviar automáticamente.{2}{2}Motivo: {3}",
                    referencia, intentos, Environment.NewLine, motivo);
                smtp.Send(mensaje);
            }
        }

        private FacturaTicketBai ComponerTicketTicketBai(TicketCompleto tc)
        {
            FacturaTicketBai retorno = new FacturaTicketBai();
            try
            {
                //primero las lineas
                LineasFacturaTicketBai[] lineas = new LineasFacturaTicketBai[1];
                LineasFacturaTicketBai n = new LineasFacturaTicketBai
                {
                    descripcion = "Servicio Parking",
                    cantidad = 1,
                    importe_unitario = tc.Cabecera.BaseImponible , // e.Lineas[1].BaseImponible,
                    tipo_iva = tc.Cabecera.TantoIVA, //e.Lineas[1].TantoIVA,
                    tipo_req = 0,
                };
                lineas[0] = n;
                retorno.lineas = lineas;
                retorno.fecha = cambiar_fecha(tc.Cabecera.Fecha, 0, 2);
                retorno.hora = DateAndTime.Now.ToString("HH:mm:ss");
                retorno.fecha_operacion = retorno.fecha;
                retorno.pais_cliente = "ES";
                retorno.serie = tc.Cabecera.Serie;
                retorno.numero = tc.Cabecera.Numero.ToString();
                retorno.simplificada = true;
                retorno.tipo_operacion = "servicios";
                retorno.intracomunitaria = false;
                retorno.exportacion = false;
                retorno.retencion = 0;
                retorno.total_factura = tc.Cabecera.Total;


            }
            catch (Exception ex)
            {
                EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + tc.Cabecera.Numero.ToString() + "Excepcion: " + ex.Message);
                retorno = null;
            }
            return retorno;
        }
        private FacturaTicketBaiRemesa ComponerFacturaTicketBaiRemesa(FacturaCompleta e)
        {
            FacturaTicketBaiRemesa retorno = new FacturaTicketBaiRemesa();
            try
            {
                retorno.fecha = cambiar_fecha(e.Cabecera.Fecha, 0, 2);
                retorno.hora = DateAndTime.Now.ToString("HH:mm:ss");
                retorno.nif = e.Cabecera.Nif;
                retorno.nombre = e.Cabecera.Nombre;
                //coger datos del cliente
                retorno.direccion = "";
                retorno.cp = "";
                retorno.pais_cliente = "";
                if (e.Cabecera.TipoCliente == "O")
                {
                    //es operador
                    RespuestaOperador rop = new RespuestaOperador();
                   // EscribirLog("Operador: " + e.Cabecera.IDCliente.ToString());
                    Operador ope = new Operador
                    {
                        Id = (int)e.Cabecera.IDCliente
                    };
                   
                    rop = ope.DameOperador();
                    //EscribirLog("resultado DameOperador: " + rop.Resultado.ToString() + "del Operador: " + ope.Id.ToString());
                    if (rop.Resultado)
                    {
                        retorno.nombre = rop.Operador.NombreFiscal;
                        retorno.direccion = rop.Operador.DireccionFiscal;
                        retorno.cp = rop.Operador.CpFiscal;
                        Pais pa = new Pais();
                        pa = pa.DamePais(rop.Operador.PaisFiscal);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                        
                    }
                    else
                    {
                        //eventolog
                        EscribirLog("[ComponerFacturaTicketBaiRemesa] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Operador");
                    }
                }
                else if (e.Cabecera.TipoCliente == "E")
                {
                    //es empresa 
                    Clientes_Facturacion cf = new Clientes_Facturacion();
                    cf = cf.ObtenerDatosFacturacionCliente(e.Cabecera.IDCliente);
                    if (cf != null)
                    {
                        retorno.nombre = cf.NombreEmpresa;
                        retorno.direccion = cf.Direccion;
                        retorno.cp = cf.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cf.Pais);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                    }
                    else
                    {
                        //eventlog
                        EscribirLog("[ComponerFacturaTicketBaiRemesa] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Empresa");
                    }
                }
                else
                {
                    // es particular
                    Cliente cli = new Cliente();
                    cli = cli.ObtenerDatosClientesPorId(e.Cabecera.IDCliente);
                    if (cli != null)
                    {
                        retorno.nombre = cli.Nombre;
                        retorno.direccion = cli.Direccion;
                        retorno.cp = cli.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cli.Pais);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                    }
                    else
                    {
                        //eventlog
                        EscribirLog("[ComponerFacturaTicketBaiRemesa] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Particular");
                    }
                }
                retorno.serie = e.Cabecera.Serie;
                retorno.numero = e.Cabecera.Numero.ToString();
                retorno.intracomunitaria = false;
                retorno.exportacion = false;
                //faltan las simplicadas
              
                Simplificada[] lineas = new Simplificada[e.Lineas.Count()];
             
                for (int i = 0; i < e.Lineas.Count(); i++)
                {
                    //EscribirLog("He entrado el valor de i es igual a " + i.ToString());
                    //EscribirLog("Serie: " + e.Lineas[i].SerieT);
                    //EscribirLog("Numero: "+ e.Lineas[i].NumeroT.ToString());
                    //EscribirLog("Fecha: " + e.Lineas[i].Fecha);
                    Simplificada nueva = new Simplificada();
                    nueva.serie = e.Lineas[i].SerieT;
                    nueva.numero = e.Lineas[i].NumeroT.ToString();
                    nueva.fecha = cambiar_fecha(e.Lineas[i].Fecha, 0, 2);
                    lineas[i] = nueva;
                }
                //EscribirLog("Ya he construido lineas de las simplificadas");
                retorno.simplificadas = lineas;
            }
            catch (Exception ex)
            {
                EscribirLog("[ComponerFacturaTicketBaiRemesa] Error al componer la Factura Remesa: " + e.Cabecera.Numero.ToString() + " Excepcion: " + ex.Message);
                retorno = null;
            }
            return retorno;
        }
        private FacturaTicketBai ComponerFacturaTicketBai(FacturaCompleta e)
        {
            FacturaTicketBai retorno = new FacturaTicketBai();
            try
            {
                //primero las lineas
                LineasFacturaTicketBai[] lineas = new LineasFacturaTicketBai[1];

                

                LineasFacturaTicketBai n = new LineasFacturaTicketBai
                {
                    descripcion = "Servicio Parking",
                    cantidad = 1,
                    importe_unitario = e.Cabecera.BaseImponible, // e.Lineas[1].BaseImponible,
                    tipo_iva = e.Cabecera.TantoIVA , //e.Lineas[1].TantoIVA,
                    tipo_req = 0,
                };
                lineas[0] = n;

                //despues cabecera
                //Array.Resize(ref lineas, contador + 1); siempre 1
                retorno.lineas = lineas;
                retorno.fecha = cambiar_fecha(e.Cabecera.Fecha, 0, 2);
                retorno.hora = DateAndTime.Now.ToString("HH:mm:ss");
                retorno.fecha_operacion = retorno.fecha;
                retorno.nif = e.Cabecera.Nif;
                retorno.tipo_documento = null;
                retorno.simplificada = false;
                retorno.nombre = e.Cabecera.Nombre;
                //coger datos del cliente
                retorno.direccion = "";
                retorno.cp = "";
                retorno.pais_cliente = "";
                if (e.Cabecera.TipoCliente == "O"){
                    //es operador
                    RespuestaOperador rop = new RespuestaOperador();
                    //EscribirLog("Operador: " + e.Cabecera.IDCliente.ToString());
                    Operador ope = new Operador(Convert.ToInt32(e.Cabecera.IDCliente));
                    rop = ope.DameOperador();
                   // EscribirLog("resultado DameOperador: " + rop.Resultado.ToString());
                    if (rop.Resultado)
                    {
                        retorno.nombre = rop.Operador.NombreFiscal;
                        retorno.direccion = rop.Operador.DireccionFiscal;
                        retorno.cp = rop.Operador.CpFiscal;
                        Pais pa = new Pais();
                        pa = pa.DamePais(rop.Operador.PaisFiscal);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                    }
                    else
                    {
                        //eventolog
                        EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Operador");
                    }
                }
                else if (e.Cabecera.TipoCliente == "E")
                {
                    //es empresa 
                    Clientes_Facturacion cf = new Clientes_Facturacion();
                    cf = cf.ObtenerDatosFacturacionCliente(e.Cabecera.IDCliente);
                    if (cf != null)
                    {
                        retorno.nombre = cf.NombreEmpresa;
                        retorno.direccion = cf.Direccion;
                        retorno.cp = cf.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cf.Pais);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                    }
                    else
                    {
                        //eventlog
                        EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Empresa");
                    }
                }else
                {
                    // es particular
                    Cliente cli = new Cliente();
                    cli = cli.ObtenerDatosClientesPorId(e.Cabecera.IDCliente);
                    if (cli != null)
                    {
                        retorno.nombre = cli.Nombre;
                        retorno.direccion = cli.Direccion;
                        retorno.cp = cli.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cli.Pais);
                        if (pa != null)
                        {
                            retorno.pais_cliente = pa.ISO2; // pasar a iso2
                        }
                        else
                        {
                            retorno.pais_cliente = "ES";
                        }
                    }
                    else
                    {
                        //eventlog
                        EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Particular");
                    }
                }

                retorno.serie =e.Cabecera.Serie ;
                retorno.numero = e.Cabecera.Numero.ToString();
                retorno.rectificativa = false;
                retorno.tipo_operacion = "servicios";
                retorno.intracomunitaria = false;
                retorno.exportacion = false;
                retorno.retencion = 0;
                retorno.total_factura = e.Cabecera.TotalFactura;

            }
            catch (Exception ex)
            {
                EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Excepcion: " + ex.Message);
                retorno = null;
            }
            return retorno;
        }
        public string cambiar_fecha(string wfecha, int formato_entrada, int formato_salida)
        {
            // Formatos:  0-> AAAAMMDD    1->DDMMAAAA     2->DD/MM/AAAA   3-> DD/MM/AA
            string retorno = "";
            switch (formato_entrada)
            {
                case 0:
                    {
                        switch (formato_salida)
                        {
                            case 1:
                                {
                                    retorno = wfecha.Substring(6, 2) + wfecha.Substring(4, 2) + wfecha.Substring(0, 4); // Right(wfecha, 2) & wfecha.Substring(4, 2) & Left(wfecha, 4)
                                    break;
                                }

                            case 2:
                                {
                                    retorno = Strings.Right(wfecha, 2) + "/" + wfecha.Substring(4, 2) + "/" + Strings.Left(wfecha, 4);
                                    break;
                                }

                            case 3:
                                {
                                    retorno = Strings.Right(wfecha, 2) + "/" + wfecha.Substring(4, 2) + "/" + wfecha.Substring(2, 2);
                                    break;
                                }
                        }

                        break;
                    }
                case 1:
                    {
                        switch (formato_salida)
                        {
                            case 0:
                                {
                                    retorno = Strings.Right(wfecha, 4) + wfecha.Substring(2, 2) + Strings.Left(wfecha, 2);
                                    break;
                                }

                            case 2:
                                {
                                    retorno = Strings.Left(wfecha, 2) + "/" + wfecha.Substring(2, 2) + "/" + Strings.Right(wfecha, 4);
                                    break;
                                }

                            case 3:
                                {
                                    retorno = Strings.Left(wfecha, 2) + "/" + wfecha.Substring(2, 2) + "/" + Strings.Right(wfecha, 2);
                                    break;
                                }
                        }

                        break;
                    }
                case 2:
                    {
                        switch (formato_salida)
                        {
                            case 0:
                                {
                                    retorno = Strings.Right(wfecha, 4) + wfecha.Substring(3, 2) + Strings.Left(wfecha, 2);
                                    break;
                                }

                            case 1:
                                {
                                    retorno = Strings.Left(wfecha, 2) + "/" + wfecha.Substring(2, 2) + "/" + Strings.Right(wfecha, 4);
                                    break;
                                }

                            case 3:
                                {
                                    retorno = Strings.Left(wfecha, 6) + Strings.Right(wfecha, 2);
                                    break;
                                }
                        }

                        break;
                    }

                case 3:
                    {
                        switch (formato_salida)
                        {
                            case 0:
                                {
                                    retorno = "20" + Strings.Right(wfecha, 2) + wfecha.Substring(2, 2) + Strings.Left(wfecha, 2);
                                    break;
                                }

                            case 1:
                                {
                                    retorno = Strings.Left(wfecha, 2) + wfecha.Substring(2, 2) + "20" + Strings.Right(wfecha, 2);
                                    break;
                                }

                            case 2:
                                {
                                    retorno = Strings.Left(wfecha, 6) + "20" + Strings.Right(wfecha, 2);
                                    break;
                                }
                        }

                        break;
                    }
            }
            return retorno;
        }
        private string SendRequest(Uri uri, Byte[] jsonDataBytes, string contentType, string method)
        {
            if (simulacion != null)
                return simulacion.Responder(jsonDataBytes);

            string response = "";
            WebRequest request;

            request = WebRequest.Create(uri);
            request.Method = method;
            request.PreAuthenticate = true;
            request.Headers.Add("Nif", Nif);
            request.Headers.Add("Token", token);
            request.ContentType = contentType;
            if (jsonDataBytes != null)
            {
                request.ContentLength = jsonDataBytes.Length;
                using (var requestStream = request.GetRequestStream())
                {
                    requestStream.Write(jsonDataBytes, 0, jsonDataBytes.Length);
                    requestStream.Close();
                    using (var responseStream = request.GetResponse())
                    {
                        using (StreamReader reader = new StreamReader(responseStream.GetResponseStream()))
                        {
                            response = reader.ReadToEnd();
                        }
                    }

                }

            }
            else
            {
                // aqui es get
                using (var responseStream = request.GetResponse())
                {
                    using (StreamReader reader = new StreamReader(responseStream.GetResponseStream()))
                    {
                        response = reader.ReadToEnd();
                    }
                }
            }


            return response;
        }
        private void EscribirLog(string texto)
        {
            if (simulacion != null)
            {
                Console.Error.WriteLine(texto);
                return;
            }

            String strFile = String.Format(@"C:\GestPark\LOGS\Ticketbai_EPARK_Log_{0:dd-MM-yyyy}.txt", DateTime.Now);
            File.AppendAllText(strFile, String.Format("{0} : {1}" + System.Environment.NewLine, DateTime.Now, texto));
        }

        private void RegistrarEvento(string texto, EventLogEntryType tipo)
        {
            if (simulacion == null)
                EventLog.WriteEntry(texto, tipo);
            else
                Console.Error.WriteLine(texto);
        }
    }
}
