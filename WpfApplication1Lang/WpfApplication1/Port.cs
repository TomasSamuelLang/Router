using System;
using System.Collections.Generic;
using System.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpPcap;
using SharpPcap.WinPcap;
using System.Diagnostics;
using PcapDotNet.Core;
using System.Net.NetworkInformation;
using PacketDotNet;
using System.Threading;
using PacketDotNet.Utils;
using PacketDotNet.LLDP;
using System.Collections;
using System.Net;

namespace WpfApplication1
{
    class Port
    {

        public IPAddress ipAddress { get; set; }
        public IPAddress mask { get; set; }
        public WinPcapDevice device { get; set; }
        public int idPort;



    }
}
