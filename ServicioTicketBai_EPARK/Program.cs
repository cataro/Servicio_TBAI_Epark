using System;
using System.ServiceProcess;

namespace ServicioTicketBai_EPARK
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal para la aplicación.
        /// </summary>
        static int Main(string[] args)
        {
#if DEBUG
            bool simularRechazo = Array.Exists(args, arg => arg == "--simular-rechazo");
            if (Environment.UserInteractive || simularRechazo || Array.Exists(args, arg => arg == "--simular"))
            {
                try
                {
                    return TicketBai_EPARK.EjecutarSimulacion(simularRechazo);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex);
                    return 1;
                }
            }
#endif
            ServiceBase.Run(new TicketBai_EPARK());
            return 0;
        }
    }
}
