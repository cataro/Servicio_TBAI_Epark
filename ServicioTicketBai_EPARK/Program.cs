using System;
using System.Collections.Generic;
using System.Linq;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;

namespace ServicioTicketBai_EPARK
{
    static class Program
    {
        /// <summary>
        /// Punto de entrada principal para la aplicación.
        /// </summary>
        static int Main(string[] args)
        {
            ServiceBase.Run(new TicketBai_EPARK());
            return 0;
        }
    }
}
