using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

namespace WpfApplication1
{
    class Route
    {
        public IPAddress network { get; set; }
        public int cidr { get; set; }
        public IPAddress nextHop { get; set; }
        public IPAddress mask { get; set; }
        public Port outputPort { get; set; }
        public int id { get; set; }
        public String type;
        public int metric;
    }
}
