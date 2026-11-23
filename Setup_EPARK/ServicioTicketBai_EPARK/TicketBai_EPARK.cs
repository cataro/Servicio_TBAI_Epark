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
using System.ServiceModel;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace ServicioTicketBai_EPARK
{
    public partial class TicketBai_EPARK : ServiceBase
    {
        bool bandera = false;
        bool ok = false;
        string url = ConfigurationManager.AppSettings["urlTbai"].ToString();
        string Nif = ConfigurationManager.AppSettings["nif"].ToString();
        string token = ConfigurationManager.AppSettings["token"].ToString();
        long Intervalo = Convert.ToInt64(ConfigurationManager.AppSettings["intervalo_milisegundos"]);
        Uri URI = null;
        //install servicio
        internal static ServiceHost hostService = null;

        public TicketBai_EPARK()
        {
            InitializeComponent();
            URI = new Uri(url);
            timer1.Interval = Intervalo;
        }

        protected override void OnStart(string[] args)
        {
            try
            {
                //iniciar servicio
                EscribirLog("Abrir host service");
                hostService = new ServiceHost(typeof(WcfServiceLibraryEPARK.Service1));
                WcfServiceLibraryEPARK.Service1 mgmtService = new WcfServiceLibraryEPARK.Service1();
                hostService.Open();
                EventLog.WriteEntry("Se ha inicidado el servicio");
                EscribirLog("Se ha inicidado el servicio");
                bandera = false;
                timer1.Start();
            }
            catch (Exception ex)
            {
                EscribirLog("NO se ha inicidado el servicio");
                throw new Exception(ex.Message);
            }
        }

        protected override void OnStop()
        {
            hostService.Close();
            EscribirLog("NO se ha inicidado el servicio");
            EventLog.WriteEntry("Se ha finalizado el servicio");
            timer1.Stop();
        }

        private void timer1_Elapsed_1(object sender, System.Timers.ElapsedEventArgs e)
        {
            EventLog.WriteEntry("Se inicio la escucha de facturas pendientes de TicketBai", EventLogEntryType.Information);
            if (bandera) return;

            try
            {
                bandera = true;
                EscribirLog("Se inicio la escucha de facturas pendientes de TicketBai");

                TicketBai tb = new TicketBai();
                TicketBai[] pendientes;
                pendientes = tb.DameTicketsBaiPendientes();
                EscribirLog("TICKETS PENDIENTES: " + pendientes.Length);
                if (pendientes != null)
                {
                    for (int i = 0; i <= pendientes.Length - 1; i++)
                    {
                        TicketBai n = new TicketBai();
                        n = pendientes[i];
                        ok = EnviarTicketBai(n);
                        if (!ok)
                        {
                            EventLog.WriteEntry("No se ha podido generar TicketBai de la Factura: " + n.Factura.ToString(), EventLogEntryType.Warning);
                            //tenemos que indicar al cliente de alguna manera que no se ha podido generar el TBai.
                        }
                    }
                }

            }
            catch (Exception ex)
            {
                EventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
            }
            bandera = false;
        }

        private bool EnviarTicketBai(TicketBai f)
        {
            bool retorno = false;
            if (f.Tipo == "F")
            {
                EscribirLog("Inicio dame Factura");
                Fac fa = new Fac();
                FacturaCompleta fac = fa.DameFacturaCompleta(f.Ejercicio, f.Serie, f.Factura, 1); //TIPOFACTURA 1?¿
                if (fac != null)
                {
                    //ahora tendriamos que conectar con ticketbai para enviar datos
                    // y recibir respuesta
                    retorno = EnviarFactura(fac);
                    //actualizar tabla con estado 1
                }
                else
                {
                    EscribirLog("[EnviarTicketBai] Error al enviar la Factura:" + f.Factura);
                }
            }
            return retorno;
        }
        private bool EnviarFactura(FacturaCompleta e)
        {
            bool retorno = false;
            try
            {
                FacturaTicketBai ftb = new FacturaTicketBai();
                ftb = ComponerFacturaTicketBai(e);
                EscribirLog("Componer Factura realizada: " + ftb.Nombre + "  -   " + ftb.Numero);
                if (ftb != null)
                {
                    // enviar a batuz http
                    string jsonString = JsonConvert.SerializeObject(ftb);
                    try
                    {
                        var data = Encoding.UTF8.GetBytes(jsonString);
                        var result_post = SendRequest(URI, data, "application/json", "POST");
                        //respuestaPOST resp = JsonConvert.DeserializeObject<respuestaPOST>(result_post);
                        var jo_result_post = JObject.Parse(result_post);
                        if (jo_result_post["result"].ToString() != "ERROR")
                        {
                            if (jo_result_post["result"].ToString() != "PENDING")
                            {

                            }
                            else
                            {
                                String qr64 = jo_result_post["return"]["qr"].ToString();
                                String firma = jo_result_post["return"]["huella_tbai"].ToString();
                                EscribirLog("TICKETBAI OBTENIDO");
                                int ano = e.Cabecera.Ejercicio;
                                TicketBai ntb = new TicketBai(ano, e.Cabecera.Numero, e.Cabecera.Serie, "F", qr64, firma, 1, true, "");
                                ok = ntb.Actualizar();
                                retorno = ok;
                                if (!ok)
                                {
                                    EscribirLog("Ha ocurrido un error al actualizar el TicketBai " + e.Cabecera.Numero.ToString());
                                }
                            }
                        }
                        else
                        {
                            //Error no firmado
                            if (jo_result_post["msg"].ToString() != null)
                            {
                                int a = Strings.InStr(jo_result_post["msg"].ToString(), "LROEPJ240FacturasEmitidasConSGAltaRespuesta");
                                if (a != 0)
                                {
                                    //ya existe ticketbai recuperar firma y qr grabar y devolver ok=true
                                    string nuevaUriString = url.ToString() + "?serie=" + ftb.Serie + "&numero=" + ftb.Numero.ToString();
                                    Uri myUri = new Uri(nuevaUriString);
                                    var result_post1 = SendRequest(myUri, null, "application/json", "GET");
                                    var jo = JObject.Parse(result_post1);
                                    var resultado = jo["result"].ToString();
                                    //respuestaPOST resp1 = JsonConvert.DeserializeObject<respuestaPOST>(result_post1);
                                    if (resultado == "OK")
                                    {
                                        String qr64 = jo["return"]["qr"].ToString();
                                        String firma = jo["return"]["huella_tbai"].ToString();
                                        int ano = Convert.ToInt32(e.Cabecera.Ejercicio);
                                        TicketBai ntb = new TicketBai(ano, e.Cabecera.Numero,e.Cabecera.Serie, "F", qr64, firma, 1, true, "");
                                        ok = ntb.Actualizar();
                                        if (!ok)
                                        {
                                            EscribirLog("Ha ocurrido un error al actualizar el TicketBai " + e.Cabecera.Numero.ToString());
                                            retorno = false;
                                        }
                                        retorno = true;
                                    }
                                }
                                else
                                {
                                    int ano = Convert.ToInt32(e.Cabecera.Ejercicio);
                                    TicketBai ntb = new TicketBai();
                                    string msg = jo_result_post["msg"].ToString();
                                    ok = ntb.ActualizarError(ano, e.Cabecera.Numero, e.Cabecera.Serie , "F", true, msg);
                                    if (!ok)
                                    {
                                        EscribirLog("Ha ocurrido un error al actualizar Error en el TicketBai Factura: " + e.Cabecera.Numero.ToString());
                                    }
                                    //ha ocurrido algun error con los datos enviados
                                    EscribirLog("Ha ocurrido un error al actualizar el TicketBai de la factura: " + e.Cabecera.Numero.ToString() + " " + jo_result_post["msg"].ToString());
                                    retorno = false;
                                }
                            }
                            else
                            {
                                EventLog.WriteEntry(jo_result_post["result"].ToString(), EventLogEntryType.Error);
                                retorno = false;
                            }

                        }
                    }
                    catch (Exception ex)
                    {
                        EventLog.WriteEntry(ex.Message, EventLogEntryType.Error);
                        retorno = false;
                    }
                }
                else
                {
                    EscribirLog("[EnviarTicketBai] Error al enviar la Factura:" + e.Cabecera.Numero.ToString());
                    retorno = false;
                }
            }
            catch (Exception ex)
            {
                EscribirLog("[EnviarFactura] Error al enviar la Factura:" + e.Cabecera.Numero.ToString() + "Excepción " + ex.Message);
                retorno = false;
                //throw;
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
                    Descripcion = "Servicio Parking",
                    Cantidad = 1,
                    Importe_unitario = e.Cabecera.BaseImponible, // e.Lineas[1].BaseImponible,
                    Tipo_iva = e.Cabecera.TantoIVA , //e.Lineas[1].TantoIVA,
                    Tipo_req = 0,
                };
                lineas[0] = n;

                //despues cabecera
                //Array.Resize(ref lineas, contador + 1); siempre 1
                retorno.Lineas = lineas;
                retorno.Fecha = cambiar_fecha(e.Cabecera.Fecha, 0, 2);
                retorno.Hora = DateAndTime.Now.ToString("HH:mm:ss");
                retorno.Fecha_operacion = retorno.Fecha;
                retorno.Nif = e.Cabecera.Nif;
                retorno.Tipo_documento = null;
                retorno.Simplificada = false;
                retorno.Nombre = e.Cabecera.Nombre;
                //coger datos del cliente
                retorno.Direccion = "";
                retorno.Cp = "";
                retorno.Pais_cliente = "";
                if (e.Cabecera.TipoCliente == "O"){
                    //es operador
                    RespuestaOperador rop = new RespuestaOperador();
                    Operador ope = new Operador(Convert.ToInt32(e.Cabecera.IDCliente));
                    rop = ope.DameOperador();
                    if (rop.Resultado)
                    {
                        retorno.Nombre = rop.Operador.NombreFiscal;
                        retorno.Direccion = rop.Operador.DireccionFiscal;
                        retorno.Cp = rop.Operador.CpFiscal;
                        Pais pa = new Pais();
                        pa = pa.DamePais(rop.Operador.PaisFiscal);
                        retorno.Pais_cliente = pa.ISO2; // pasar a iso2
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
                        retorno.Nombre = cf.NombreEmpresa;
                        retorno.Direccion = cf.Direccion;
                        retorno.Cp = cf.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cf.Pais);
                        retorno.Pais_cliente = pa.ISO2; // pasar a iso2
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
                        retorno.Nombre = cli.Nombre;
                        retorno.Direccion = cli.Direccion;
                        retorno.Cp = cli.Cp;
                        Pais pa = new Pais();
                        pa = pa.DamePais(cli.Pais);
                        retorno.Pais_cliente = pa.ISO2; // pasar a iso2
                    }
                    else
                    {
                        //eventlog
                        EscribirLog("[ComponerFacturaTicketBai] Error al componer la Factura:" + e.Cabecera.Numero.ToString() + "Datos Particular");
                    }
                }

                retorno.Serie =e.Cabecera.Serie ;
                retorno.Numero = e.Cabecera.Numero.ToString();
                retorno.Rectificativa = false;
                retorno.Tipo_operacion = "servicios";
                retorno.Intracomunitaria = false;
                retorno.Exportacion = false;
                retorno.Retencion = 0;
                retorno.Total_factura = e.Cabecera.TotalFactura;

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
            String strFile = String.Format(@"C:\GestPark\LOGS\Ticketbai_EPARK_Log_{0:dd-MM-yyyy}.txt", DateTime.Now);
            File.AppendAllText(strFile, String.Format("{0} : {1}" + System.Environment.NewLine, DateTime.Now, texto));
        }
    }
}
