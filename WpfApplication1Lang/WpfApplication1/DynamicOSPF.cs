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
using PacketDotNet.LSA;

namespace WpfApplication1
{
    class OspfNetwork {

        public IPAddress network;
        public IPAddress wildMask;
    }
    class Vertex
    {
        public IPAddress id;
        public LSA lsa;
        public Vertex nexthop;
        public int cost;

        public Vertex(IPAddress id, LSA lsa, int cost, Vertex nexthop) {
            this.id = id;
            this.lsa = lsa;
            this.cost = cost;
            this.nexthop = nexthop;
        }
    }

    public class DatabaseCopy {

        public LSA lsa;
        public int age;

        public DatabaseCopy(LSA lsa)
        {
            this.lsa = lsa;
            this.age = 0;
        }
    }

    class Neighbor {

        public IPAddress neighbor, ipAddress;
        public Port port;
        public uint priority, deadInterval;
        public ushort state;
        public char type;
        public IPAddress dr, bdr;
        public Boolean master;

        public uint database_seq;
        public OSPFv2DDPacket lastDatabase_received, lastDatabase_sent;
        public List<LSA> retransnissionList, summaryList;
        public List<LSA> requests;
        public List<OSPFv2DDPacket> databaseQueue = new List<OSPFv2DDPacket>();

        public Neighbor(IPAddress neighbor, IPAddress ip , uint pr, uint dead, Port port, ushort state, IPAddress dr, IPAddress bdr, Boolean master) {

            this.neighbor = neighbor;
            this.port = port;
            this.priority = pr;
            this.ipAddress = ip;
            this.deadInterval = dead;
            this.state = state;
            this.dr = dr;
            this.bdr = bdr;
            this.type = 'O';
            this.master = master;
            this.requests = new List<LSA>();
            this.retransnissionList = new List<LSA>();
            this.summaryList = new List<LSA>();
        }
    }

    class DynamicOSPF
    {
        Port port1;
        Port port2;
        public IPAddress routerID;
        public Tuple<IPAddress, IPAddress> port1_Dr_Bdr;
        public Tuple<IPAddress, IPAddress> port2_Dr_Bdr;
        SwRouter router;

        public RouterLSA routerLSA = null;
        public List<NetworkLSA> networkLSA = new List<NetworkLSA>();
        public List<RouterLink> routerLinks = new List<RouterLink>();


        public List<DatabaseCopy> database_of_lsa = new List<DatabaseCopy>();

        public Boolean initCalculation = false;
        public int waitTime = 40;

        public List<IPAddress> neighbor1 = new List<IPAddress>();
        public List<IPAddress> neighbor2 = new List<IPAddress>();
        public List<Neighbor> neighborList = new List<Neighbor>();
        public readonly object locker = new object();
        public readonly object spflock = new object();
        public readonly object databaselock = new object();
       
        Thread t1, t2, t3, t4;

        public Boolean database_init = false;
        public int retransmissionCount = 1;

        public LinkedList<OspfNetwork> ospfNetList = new LinkedList<OspfNetwork>();


        public DynamicOSPF(Port port1, Port port2, SwRouter rtr, IPAddress ip) {

            this.port1 = port1;
            this.port2 = port2;
            this.router = rtr;

            port1_Dr_Bdr = Tuple.Create(IPAddress.Parse("0.0.0.0"), IPAddress.Parse("0.0.0.0"));
            port2_Dr_Bdr = Tuple.Create(IPAddress.Parse("0.0.0.0"), IPAddress.Parse("0.0.0.0"));

            RouterLink link = new RouterLink();

            this.routerID = ip;

            t1 = new Thread(() => sendHelloPacket(port1));
            t1.Start();
            t2 = new Thread(() => sendHelloPacket(port2));
            t2.Start();
            t3 = new Thread(() => waitTimer());
            t3.Start();
            t4 = new Thread(() => lsaTimer());
            t4.Start();

        }

        public IPAddress IPtoNet(IPAddress ip, IPAddress mask)
        {
            byte[] bip = ip.GetAddressBytes();
            byte[] bmask = mask.GetAddressBytes();
            byte[] result = new byte[4];

            for (int i = 0; i < 4; i++)
            {
                result[i] = (byte)((int)bip[i] & (int)bmask[i]);
            }

            return new IPAddress(result);
        }

        public void sendHelloPacket(Port port) {

            EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("01-00-5E-00-00-05"), EthernetPacketType.IPv4);

            IPv4Packet ippacket = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.5"));
            ethpacket.PayloadPacket = ippacket;
            while (true)
            {

                OSPFv2HelloPacket hello;
                if (port.idPort == 1)
                    hello = new OSPFv2HelloPacket(port.mask, 10, 40, neighbor1);
                else
                    hello = new OSPFv2HelloPacket(port.mask, 10, 40, neighbor2);
                hello.AreaID = IPAddress.Parse("0.0.0.0");
                hello.RtrPriority = 1;
                hello.HelloOptions = 2;
                hello.RouterID = routerID;
                if (port.idPort == 1)
                {
                    hello.DesignatedRouterID = port1_Dr_Bdr.Item1;
                    hello.BackupRouterID = port1_Dr_Bdr.Item2;
                }
                else
                {
                    hello.DesignatedRouterID = port2_Dr_Bdr.Item1;
                    hello.BackupRouterID = port2_Dr_Bdr.Item2;
                }
                hello.Checksum = 0;
                hello.Checksum = calculateChecksum(hello.HeaderData, 0, hello.HeaderData.Length);
                ippacket.PayloadPacket = hello;
                ippacket.TimeToLive = 1;
                ippacket.UpdateIPChecksum();
                port.device.SendPacket(ethpacket);

                System.Threading.Thread.Sleep(10000);
            }
        }
        public ushort calculateChecksum(byte[] header, int start, int length)
        {
            ushort word;
            long sum = 0;
            for (int i = start; i < length + start; i += 2)
            {
                word = (ushort)(((header[i] << 8) & 0xFF00) + (header[i + 1] & 0xFF));
                sum += (long)word;
            }

            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = ~sum;
            return (ushort)sum;
        }

        public IPAddress compareIP(IPAddress ip1, IPAddress ip2) => IPAddressToLongBackwards(ip1) > IPAddressToLongBackwards(ip2) ? ip1 : ip2;
        static private uint IPAddressToLongBackwards(IPAddress ipA) {
            byte[] byteIP = ipA.GetAddressBytes();
            uint ip = (uint)byteIP[0] << 24;
            ip += (uint)byteIP[1] << 16;
            ip += (uint)byteIP[2] << 8;
            ip += (uint)byteIP[3];
            return ip;
        }

        public void addNeighbor(OSPFv2HelloPacket packet, IPAddress ip, Port port) {


            foreach (Neighbor n in neighborList) {
                if (n.ipAddress.Equals(ip) && n.neighbor.Equals(packet.RouterID)) {
                    n.deadInterval = packet.RouterDeadInterval;
                    n.port = port;
                    if (packet.NeighborID.Contains(routerID) && n.state < 2) {
                        n.priority = packet.RtrPriority;
                        n.dr = packet.DesignatedRouterID;
                        n.bdr = packet.BackupRouterID;
                        n.state = 2;

                        if (initCalculation)
                        {
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                    }
                    if (n.state >= 2) {
                        if (!n.dr.Equals(packet.DesignatedRouterID) && packet.DesignatedRouterID.Equals(ip))
                        {

                            n.dr = packet.DesignatedRouterID;
                            n.bdr = packet.BackupRouterID;
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                        else if (!n.bdr.Equals(packet.BackupRouterID) && packet.BackupRouterID.Equals(ip))
                        {

                            n.dr = packet.DesignatedRouterID;
                            n.bdr = packet.BackupRouterID;
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                        if (n.dr.Equals(ip) && !packet.DesignatedRouterID.Equals(ip))
                        {

                            n.dr = packet.DesignatedRouterID;
                            n.bdr = packet.BackupRouterID;
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                        else if (n.bdr.Equals(ip) && !packet.BackupRouterID.Equals(ip))
                        {

                            n.dr = packet.DesignatedRouterID;
                            n.bdr = packet.BackupRouterID;
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                        if (n.priority != packet.RtrPriority)
                        {
                            n.priority = packet.RtrPriority;
                            n.dr = packet.DesignatedRouterID;
                            n.bdr = packet.BackupRouterID;
                            if (port.idPort == 1)
                                chooseDRBDR(port, port1_Dr_Bdr);
                            else
                                chooseDRBDR(port, port2_Dr_Bdr);
                        }
                    }

                    return;
                }
            }
            Boolean temp = false;
            if (!compareIP(packet.RouterID, routerID).Equals(routerID))
            {
                temp = true;
            }
            if (packet.NeighborID.Contains(routerID))
            {
                neighborList.Add(new Neighbor(packet.RouterID, ip, packet.RtrPriority, packet.RouterDeadInterval, port, 2, packet.DesignatedRouterID, packet.BackupRouterID, temp));
                if (initCalculation) {
                    if (port.idPort == 1)
                        chooseDRBDR(port, port1_Dr_Bdr);
                    else
                        chooseDRBDR(port, port2_Dr_Bdr);
                }
            }
            else {
                neighborList.Add(new Neighbor(packet.RouterID, ip, packet.RtrPriority, packet.RouterDeadInterval, port, 1, packet.DesignatedRouterID, packet.BackupRouterID, temp));
            }

            if (port.idPort == 1)
            {
                neighbor1.Add(packet.RouterID);
            }
            else {
                neighbor2.Add(packet.RouterID);
            }
        }

        public Tuple<IPAddress, IPAddress> electDrBdr(Port port, Tuple<IPAddress, IPAddress> port_Dr_Bdr) {

            LinkedList<Neighbor> candidates = new LinkedList<Neighbor>();
            Boolean possibleBDR = false, possibleDR = false;
            Neighbor assignedBDR = null, assignedDR = null;

            

                if (port_Dr_Bdr.Item2.Equals(port.ipAddress) && !port_Dr_Bdr.Item1.Equals(port.ipAddress)) possibleBDR = true;
                if (port_Dr_Bdr.Item1.Equals(port.ipAddress)) possibleDR = true;

                candidates.AddLast(new Neighbor(routerID, port.ipAddress, 1, 40, null, 2, port_Dr_Bdr.Item1, port_Dr_Bdr.Item2, false));
                foreach (Neighbor n in neighborList) {

                    if (n.port.Equals(port) && n.priority > 0 && n.state > 1) {

                        if (n.bdr.Equals(n.ipAddress) && !n.dr.Equals(n.ipAddress)) possibleBDR = true;
                        if (n.dr.Equals(n.ipAddress)) possibleDR = true;

                        candidates.AddLast(n);
                    }
                }
            

            foreach (Neighbor n in candidates) {
                if (!(n.dr.Equals(n.ipAddress)))
                {
                    if (possibleBDR)
                    {
                        if (n.bdr.Equals(n.ipAddress))
                        {
                            if (assignedBDR != null)
                            {
                                if (n.priority > assignedBDR.priority || (n.priority == assignedBDR.priority && compareIP(n.neighbor, assignedBDR.neighbor) == n.neighbor))
                                {
                                    assignedBDR = n;
                                }
                            }
                            else
                            {
                                assignedBDR = n;
                            }
                        }
                    }
                    else
                    {
                        if (assignedBDR != null)
                        {
                            if (n.priority > assignedBDR.priority || (n.priority == assignedBDR.priority && compareIP(n.neighbor, assignedBDR.neighbor) == n.neighbor))
                            {
                                assignedBDR = n;
                            }
                        }
                        else
                        {
                            assignedBDR = n;
                        }
                    }
                }
            }

            if (possibleDR)
            {
                foreach (Neighbor n in candidates) {
                    if (n.dr.Equals(n.ipAddress)) {
                        if (assignedDR != null) {
                            if (n.priority > assignedDR.priority || (n.priority == assignedDR.priority && compareIP(n.neighbor, assignedDR.neighbor) == n.neighbor))
                            {
                                assignedDR = n;
                            }
                        }
                        else
                        {
                            assignedDR = n;
                        }
                    }
                }
            }
            else {
                assignedDR = assignedBDR;
            }


            Console.WriteLine("New DR: " + assignedDR.neighbor.ToString());
            if (assignedBDR == null)
                Console.WriteLine("New BDR: " + "0.0.0.0");
            else Console.WriteLine("New BDR: " + assignedBDR.neighbor.ToString());

            if (assignedBDR == null)
                return Tuple.Create(assignedDR.ipAddress, IPAddress.Parse("0.0.0.0"));
            else
                return Tuple.Create(assignedDR.ipAddress, assignedBDR.ipAddress);
        }

        public void chooseDRBDR(Port port, Tuple<IPAddress, IPAddress> port_Dr_Bdr)
        {
            Tuple<IPAddress, IPAddress> resultOfElection = electDrBdr(port, port_Dr_Bdr);

            if ((port_Dr_Bdr.Item1.Equals(port.ipAddress) && !resultOfElection.Item1.Equals(port_Dr_Bdr.Item1)) || (port_Dr_Bdr.Item2.Equals(port.ipAddress) && !resultOfElection.Item2.Equals(port_Dr_Bdr.Item2))
                || (!port_Dr_Bdr.Item1.Equals(port.ipAddress) && resultOfElection.Item1.Equals(port.ipAddress)) ||
                (!port_Dr_Bdr.Item2.Equals(port.ipAddress) && resultOfElection.Item2.Equals(port.ipAddress))) {

                if (port.idPort == 1)
                {
                    port1_Dr_Bdr = resultOfElection;
                    resultOfElection = electDrBdr(port, port1_Dr_Bdr);
                }
                else
                {
                    port2_Dr_Bdr = resultOfElection;
                    resultOfElection = electDrBdr(port, port2_Dr_Bdr);
                }
            }
            // port_Dr_Bdr = resultOfElection;

            if (port.idPort == 1)
            {
                port1_Dr_Bdr = resultOfElection;
            }
            else {
                port2_Dr_Bdr = resultOfElection;
            }

            adjChange(port, resultOfElection.Item1, resultOfElection.Item2);
            //change adjecencies
            generateLSA(port);
            if(port.ipAddress.Equals(port_Dr_Bdr.Item1) && !port.ipAddress.Equals(resultOfElection.Item1))
            {
                //flush network
                if(networkLSA.Count > 0)
                {
                    NetworkLSA badlsa = null;
                    foreach(NetworkLSA netlsa in networkLSA)
                    {
                        if (netlsa.LinkStateID.Equals(port.ipAddress))
                        {
                            badlsa = netlsa;
                        }
                    }
                    if(badlsa != null)
                    {
                        networkLSA.Remove(badlsa);
                        badlsa.LSAge = 3600;
                        flood(badlsa, null, null);
                    }
                }
            }
            else if(port.ipAddress.Equals(resultOfElection.Item1) && getrouterIDS(port).Count == 0)
            {
                if (networkLSA.Count > 0)
                {
                    NetworkLSA badlsa = null;
                    foreach (NetworkLSA netlsa in networkLSA)
                    {
                        if (netlsa.LinkStateID.Equals(port.ipAddress))
                        {
                            badlsa = netlsa;
                        }
                    }
                    if (badlsa != null)
                    {
                        networkLSA.Remove(badlsa);
                        badlsa.LSAge = 3600;
                        flood(badlsa, null, null);
                    }
                }
            }
            if (!port.ipAddress.Equals(port_Dr_Bdr.Item1) && port.ipAddress.Equals(resultOfElection.Item1)) {
                //generate lsa

                networkLSAgenerator(port, resultOfElection.Item1);
            }
        }

        public void waitTimer() {
            System.Threading.Thread.Sleep(40000);
            Console.WriteLine("+++++++++++ cas uplynul +++++++++++++++++");
            lock (locker)
            {
                if (!initCalculation) {
                    initCalculation = true;
                    chooseDRBDR(port1, port1_Dr_Bdr);
                    chooseDRBDR(port2, port2_Dr_Bdr);
                }
            }
        }

        public void adjChange(Port port, IPAddress dr, IPAddress bdr) {

            if (dr.Equals(port.ipAddress))
            {

                foreach (Neighbor n in neighborList)
                {

                    if (n.port.Equals(port))
                    {
                        if (n.ipAddress.Equals(bdr))
                            n.type = 'B';
                        else
                            n.type = 'O';
                        if (n.state == 2) {

                            n.state = 3;
                            Thread tt = new Thread(() => initDatabaseExchange(n));
                            tt.Start();

                        }
                    }
                }
            }
            else if (bdr.Equals(port.ipAddress))
            {
                foreach (Neighbor n in neighborList)
                {

                    if (n.port.Equals(port))
                    {
                        if (n.ipAddress.Equals(dr))
                            n.type = 'D';
                        else
                            n.type = 'O';
                        if (n.state == 2)
                        {

                            n.state = 3;
                            Thread tt = new Thread(() => initDatabaseExchange(n));
                            tt.Start();
                        }
                    }
                }
            }
            else {
                foreach (Neighbor n in neighborList)
                {

                    if (n.port.Equals(port))
                    {
                        if (n.ipAddress.Equals(dr))
                        {
                            n.type = 'D';
                            if (n.state == 2)
                            {
                                n.state = 3;
                                Thread tt = new Thread(() => initDatabaseExchange(n));
                                tt.Start();
                            }
                        }
                        else if (n.ipAddress.Equals(bdr))
                        {
                            n.type = 'B';
                            if (n.state == 2)
                            {
                                n.state = 3;
                                Thread tt = new Thread(() => initDatabaseExchange(n));
                                tt.Start();
                            }
                        }
                        else
                        {
                            n.type = 'O';
                            if (n.state > 2) n.state = 2;
                        }
                    }
                }
            }
        }

        public void initDatabaseExchange(Neighbor neighbor) {

            String nei_mac = router.findDestMac(neighbor.ipAddress);
            while (nei_mac == null) {
                router.sendArpRequest(neighbor.ipAddress);
                Thread.Sleep(500);
                nei_mac = router.findDestMac(neighbor.ipAddress);
            }

            EthernetPacket packet = new EthernetPacket(neighbor.port.device.MacAddress, PhysicalAddress.Parse(nei_mac), EthernetPacketType.IPv4);
            IPv4Packet ipPacket = new IPv4Packet(neighbor.port.ipAddress, neighbor.ipAddress);

            OSPFv2DDPacket databasePacket = new OSPFv2DDPacket();
            databasePacket.AreaID = IPAddress.Parse("0.0.0.0");
            databasePacket.InterfaceMTU = 1500;
            databasePacket.RouterID = routerID;
            databasePacket.DBDescriptionOptions = 66;
            databasePacket.DBDescriptionBits = 7;
            neighbor.database_seq = 1313;
            databasePacket.DDSequence = neighbor.database_seq;
            databasePacket.Checksum = calculateChecksum(databasePacket.HeaderData, 0, databasePacket.HeaderData.Length);

            ipPacket.TimeToLive = 1;
            ipPacket.Checksum = 0;

            ipPacket.PayloadPacket = databasePacket;
            packet.PayloadPacket = ipPacket;
            ipPacket.UpdateIPChecksum();
            int sleepcount = 2000;
            neighbor.lastDatabase_sent = databasePacket;

            neighbor.port.device.SendPacket(packet);
            while (neighbor.state == 3)
            {
                Thread.Sleep(50);

                if (neighbor.state < 3)
                {
                    return;
                }
                if (--sleepcount == 0)
                {
                    neighbor.state = 0;
                    return;
                }
                if (sleepcount % 100 == 0 && sleepcount >= 100) {
                    neighbor.port.device.SendPacket(packet);
                }
            }
            // initialize exchange
            exchange(neighbor, packet);

        }

        public Neighbor getNeighbor(IPAddress routerID, IPAddress ip) {

            foreach (Neighbor n in neighborList) {
                if (n.neighbor.Equals(routerID) && n.ipAddress.Equals(ip)) {
                    return n;
                }
            }
            return null;
        }

        public void exchange(Neighbor neighbor, EthernetPacket ethPacket)
        {
            List<LSA> currentLSAs = new List<LSA>();

            LSA tempLSA;

            lock (databaselock)
            {
                foreach (DatabaseCopy lsa in database_of_lsa) {
                    tempLSA = new LSA() {
                        AdvertisingRouter = lsa.lsa.AdvertisingRouter,
                        LSType = lsa.lsa.LSType,
                        LinkStateID = lsa.lsa.LinkStateID,
                        LSSequenceNumber = lsa.lsa.LSSequenceNumber,
                        LSAge = lsa.lsa.LSAge,
                        Options = lsa.lsa.Options,
                        Checksum = lsa.lsa.Checksum,
                        Length = lsa.lsa.Length
                    };
                    if (tempLSA.LSAge != 3600)
                        currentLSAs.Add(tempLSA);
                    else
                        neighbor.retransnissionList.Add(lsa.lsa);
                }
                neighbor.summaryList = currentLSAs;

                
            }
            if (!neighbor.master)
            {
                int sleepcount = 2000;
                retransmissionCount = 0;

                OSPFv2DDPacket fullLSAPacket = new OSPFv2DDPacket(neighbor.summaryList)
                {
                    AreaID = IPAddress.Parse("0.0.0.0"),
                    DDSequence = neighbor.database_seq,
                    InterfaceMTU = 1500,
                    RouterID = routerID,
                    DBDescriptionBits = 3,
                    DBDescriptionOptions = 66,
                    Checksum = 0

                };

                fullLSAPacket.Checksum = calculateChecksum(fullLSAPacket.HeaderData, 0, fullLSAPacket.HeaderData.Length);
                ethPacket.PayloadPacket.PayloadPacket = fullLSAPacket;
                ((IPv4Packet)ethPacket.PayloadPacket).UpdateIPChecksum();
                neighbor.lastDatabase_sent = fullLSAPacket;
                neighbor.port.device.SendPacket(ethPacket);

                while (neighbor.lastDatabase_received.DDSequence != neighbor.lastDatabase_sent.DDSequence)
                {
                    Thread.Sleep(50);

                    if (neighbor.state < 4)
                    {
                        return;
                    }
                    if (--sleepcount == 0)
                    {
                        neighbor.state = 0;
                        return;
                    }
                    if (sleepcount % 100 == 0 && sleepcount >= 100)
                    {
                        neighbor.port.device.SendPacket(ethPacket);
                    }
                }
                neighbor.summaryList.Clear();
                OSPFv2DDPacket emptyLSAPacket = new OSPFv2DDPacket()
                {
                    AreaID = IPAddress.Parse("0.0.0.0"),
                    DDSequence = neighbor.database_seq,
                    InterfaceMTU = 1500,
                    RouterID = routerID,
                    DBDescriptionBits = 1,
                    DBDescriptionOptions = 66,
                    Checksum = 0

                };

                emptyLSAPacket.Checksum = calculateChecksum(emptyLSAPacket.HeaderData, 0, emptyLSAPacket.HeaderData.Length);
                ethPacket.PayloadPacket.PayloadPacket = emptyLSAPacket;
                ((IPv4Packet)ethPacket.PayloadPacket).UpdateIPChecksum();
                neighbor.lastDatabase_sent = emptyLSAPacket;
                neighbor.port.device.SendPacket(ethPacket);

                sleepcount = 2000;
                retransmissionCount = 0;

                while (neighbor.lastDatabase_received.DDSequence != neighbor.lastDatabase_sent.DDSequence || neighbor.state == 4)
                {
                    Thread.Sleep(50);

                    if (neighbor.state < 4)
                    {
                        return;
                    }
                    if (--sleepcount == 0)
                    {
                        neighbor.state = 0;
                        return;
                    }
                    if (sleepcount % 100 == 0 && sleepcount >= 100)
                    {
                        neighbor.port.device.SendPacket(ethPacket);
                    }
                }
            }
            else
            {
                //slave
                OSPFv2DDPacket fullLSAPacket = new OSPFv2DDPacket(neighbor.summaryList)
                {
                    AreaID = IPAddress.Parse("0.0.0.0"),
                    DDSequence = neighbor.database_seq,
                    InterfaceMTU = 1500,
                    RouterID = routerID,
                    DBDescriptionBits = 2,
                    DBDescriptionOptions = 66,
                    Checksum = 0

                };

                fullLSAPacket.Checksum = calculateChecksum(fullLSAPacket.HeaderData, 0, fullLSAPacket.HeaderData.Length);
                ethPacket.PayloadPacket.PayloadPacket = fullLSAPacket;
                ((IPv4Packet)ethPacket.PayloadPacket).UpdateIPChecksum();
                neighbor.lastDatabase_sent = fullLSAPacket;
                neighbor.port.device.SendPacket(ethPacket);
                Console.WriteLine("Poslal som more");


                while (neighbor.lastDatabase_received.DDSequence != neighbor.lastDatabase_sent.DDSequence + 1)
                {
                    Thread.Sleep(69);
                }

                OSPFv2DDPacket emptyLSAPacket = new OSPFv2DDPacket()
                {
                    AreaID = IPAddress.Parse("0.0.0.0"),
                    DDSequence = neighbor.database_seq,
                    InterfaceMTU = 1500,
                    RouterID = routerID,
                    DBDescriptionBits = 0,
                    DBDescriptionOptions = 66,
                    Checksum = 0

                };

                emptyLSAPacket.Checksum = calculateChecksum(emptyLSAPacket.HeaderData, 0, emptyLSAPacket.HeaderData.Length);
                ethPacket.PayloadPacket.PayloadPacket = emptyLSAPacket;
                ((IPv4Packet)ethPacket.PayloadPacket).UpdateIPChecksum();
                neighbor.lastDatabase_sent = emptyLSAPacket;
                neighbor.port.device.SendPacket(ethPacket);
                Console.WriteLine("Poslal som emptyy");
                while (neighbor.state != 5)
                {
                    if (neighbor.state < 4)
                    {
                        return;
                    }
                    Thread.Sleep(50);

                }
                emptyLSAPacket.DDSequence = neighbor.database_seq;
                emptyLSAPacket.Checksum = 0;
                emptyLSAPacket.Checksum = calculateChecksum(emptyLSAPacket.HeaderData, 0, emptyLSAPacket.HeaderData.Length);
                ethPacket.PayloadPacket.PayloadPacket = emptyLSAPacket;
                ((IPv4Packet)ethPacket.PayloadPacket).UpdateIPChecksum();
                neighbor.lastDatabase_sent = emptyLSAPacket;
                neighbor.port.device.SendPacket(ethPacket);
            }
            //loading state
            while (neighbor.state != 5) Thread.Sleep(50);
            loading(neighbor, ethPacket);
        }

        public void loading(Neighbor neighbor, EthernetPacket ethpacket)
        {
            List<LinkStateRequest> requestlist = new List<LinkStateRequest>();

            foreach (LSA lsa in neighbor.requests)
            {
                requestlist.Add(new LinkStateRequest()
                {
                    AdvertisingRouter = lsa.AdvertisingRouter,
                    LinkStateID = lsa.LinkStateID,
                    LSType = lsa.LSType

                });
            }

            while (neighbor.requests.Count > 0)
            {
                if (neighbor.state < 5)
                {
                    return;
                }

                OSPFv2LSRequestPacket requestpacket = new OSPFv2LSRequestPacket(requestlist);
                requestpacket.AreaID = IPAddress.Parse("0.0.0.0");
                requestpacket.Checksum = 0;
                requestpacket.RouterID = routerID;
                requestpacket.Checksum = calculateChecksum(requestpacket.HeaderData, 0, requestpacket.HeaderData.Length);
                ethpacket.PayloadPacket.PayloadPacket = requestpacket;
                ((IPv4Packet)ethpacket.PayloadPacket).UpdateIPChecksum();
                neighbor.port.device.SendPacket(ethpacket);
                Thread.Sleep(5000);
            }

            neighbor.state = 6;
            generateLSA(neighbor.port);
            if (neighbor.port.ipAddress.Equals(port1_Dr_Bdr.Item1))
            {
                networkLSAgenerator(neighbor.port,port1_Dr_Bdr.Item1);
            }
            else if (neighbor.port.ipAddress.Equals(port2_Dr_Bdr.Item1))
            {
                networkLSAgenerator(neighbor.port, port2_Dr_Bdr.Item1);
            }
        }

        public DatabaseCopy getLSas(LSA lsa)
        {
            foreach (DatabaseCopy copy in database_of_lsa)
            {
                if (lsa.LSType == copy.lsa.LSType && lsa.LinkStateID.Equals(copy.lsa.LinkStateID) && lsa.AdvertisingRouter.Equals(copy.lsa.AdvertisingRouter))
                {
                    return copy;
                }
            }
            return null;
        }

        public LSA GetNewerLSA(LSA newLSA, LSA myLSA)
        {
            if (newLSA.LSSequenceNumber > myLSA.LSSequenceNumber)
            {
                return newLSA;
            }
            else if (newLSA.LSSequenceNumber < myLSA.LSSequenceNumber)
            {
                return myLSA;
            }
            else
            {
                if (newLSA.Checksum != myLSA.Checksum)
                {
                    if (newLSA.Checksum > myLSA.Checksum)
                    {
                        return newLSA;
                    }
                    else return myLSA;
                }
                else if ((newLSA.LSAge == 3600 && myLSA.LSAge != 3600) || (newLSA.LSAge != 3600 && myLSA.LSAge == 3600))
                {
                    return newLSA.LSAge == 3600 ? newLSA : myLSA;
                }
                else if (Math.Abs(newLSA.LSAge - myLSA.LSAge) > 900)
                {
                    return newLSA.LSAge < myLSA.LSAge ? newLSA : myLSA;
                }
                else return null;
            }
        }

        public void processDatabasePacket(Neighbor neighbor, List<LSA> list)
        {
            foreach (LSA lsa in list)
            {
                if (lsa.LSType != LSAType.Network && lsa.LSType != LSAType.Router)
                {
                    neighbor.database_seq++;
                    neighbor.state = 3;
                    //extart thread
                    Thread tt = new Thread(() => initDatabaseExchange(neighbor));
                    tt.Start();
                    return;
                }
                DatabaseCopy copy = getLSas(lsa);

                if (copy == null || GetNewerLSA(lsa, copy.lsa) == lsa)
                {
                    neighbor.requests.Add(lsa);
                }
            }
        }

        public LSA getLSArequest(LinkStateRequest lsa)
        {
            foreach (DatabaseCopy copy in database_of_lsa)
            {
                if (lsa.LSType == copy.lsa.LSType && lsa.LinkStateID.Equals(copy.lsa.LinkStateID) && lsa.AdvertisingRouter.Equals(copy.lsa.AdvertisingRouter))
                {
                    return copy.lsa;
                }
            }
            return null;
        }

        public RouterLSA generateLSA(Port port)
        {
            if (port.idPort == 1)
            {
                if((port1_Dr_Bdr.Item1.Equals(port.ipAddress) && getNei(6, 6) > 0) || (!port1_Dr_Bdr.Item1.Equals(port.ipAddress) && getDrState(port1_Dr_Bdr.Item1) >= 6)){
                    //Ford Transit
                    makeLSA(port, 2, port1_Dr_Bdr.Item1);
                }
                else
                {
                    makeLSA(port, 3, port1_Dr_Bdr.Item1);
                    //stub
                }
            }
            else if (port.idPort == 2)
            {
                if ((port2_Dr_Bdr.Item1.Equals(port.ipAddress) && getNei(6, 6) > 0) || (!port2_Dr_Bdr.Item1.Equals(port.ipAddress) && getDrState(port2_Dr_Bdr.Item1) >= 6))
                {
                    //Ford Transit
                    makeLSA(port, 2, port2_Dr_Bdr.Item1);
                }
                else
                {
                    //stub
                    makeLSA(port, 3, port2_Dr_Bdr.Item1);
                }
            }


            return null;
        }

        public int getNei(int stateDown, int stateUp) {
            int count = 0;
            foreach (Neighbor n in neighborList)
            {
                if (n.state >= stateDown && n.state <= stateUp)
                {
                    count++;
                }
            }
            return count;
        }

        public int getDrState(IPAddress dr) 
        {
            foreach(Neighbor n in neighborList)
            {
                if (n.ipAddress.Equals(dr))
                {
                    return n.state;
                }
            }
            return 0;
        }

        public void makeLSA(Port port, int flag, IPAddress dr)
        {
            Boolean newRLink = false;
            RouterLink foundLink = null;

            foreach (RouterLink r in routerLinks)
            {
                if ((r.Type == 2 && r.LinkData.Equals(port.ipAddress)) || (r.Type == 3 && r.LinkID.Equals(IPtoNet(port.ipAddress, port.mask))))
                {
                    foundLink = r;
                    break;
                }
            }
            if (foundLink != null)
            {

                if(flag == 3)
                {
                    if (foundLink.Type != flag)
                    {
                        newRLink = true;
                    }
                    else if (!foundLink.LinkID.Equals(IPtoNet(port.ipAddress, port.mask)))
                    {
                        newRLink = true;
                    }
                    else if (!foundLink.LinkData.Equals(port.mask))
                    {
                        newRLink = true;
                    }
                    else if (foundLink.Metric != 10)
                    {
                        newRLink = true;
                    }
                }
                else
                {
                    if (foundLink.Type != flag)
                    {
                        newRLink = true;
                    }
                    else if (!foundLink.LinkID.Equals(dr))
                    {
                        newRLink = true;
                    }
                    else if (!foundLink.LinkData.Equals(port.ipAddress))
                    {
                        newRLink = true;
                    }
                    else if (foundLink.Metric != 10)
                    {
                        newRLink = true;
                    }
                }

                if (newRLink)
                {
                    routerLinks.Remove(foundLink);

                }
            }
            else
            {
                newRLink = true;
            }


            if (newRLink)
            {
                //if ospf on
                if (flag == 3)
                {
                    routerLinks.Add(new RouterLink()
                    {
                        LinkID = IPtoNet(port.ipAddress, port.mask),
                        LinkData = port.mask,
                        Type = (byte)flag,
                        Metric = 10

                    });
                }
                else
                {
                    routerLinks.Add(new RouterLink()
                    {
                        LinkID = dr,
                        LinkData = port.ipAddress,
                        Type = (byte)flag,
                        Metric = 10

                    });
                }

                uint seq = routerLSA == null ? 0x80000000 : routerLSA.LSSequenceNumber;

                routerLSA = new RouterLSA(routerLinks)
                {
                    Options = 2,
                    LSAge = 0,
                    LinkStateID = routerID,
                    AdvertisingRouter = routerID,
                    LSSequenceNumber = seq + 1,
                    Checksum = 0,
                    VBit = 0,
                    EBit = 0,
                    BBit = 0,
                };

                routerLSA.Checksum = Fletcher(routerLSA.Bytes, 2, routerLSA.Length);
                // flood
                flood(routerLSA,null,null);
                //add to dbs
                DatabaseCopy old = getLSas(routerLSA);
                install(routerLSA,old);


            }
            

        }

        public ushort Fletcher(byte[] inputAsBytes, int start, int length)
        {
            int c0 = 0, c1 = 0;
            for (int i = start; i < length; ++i)
            {
                c0 = (c0 + inputAsBytes[i]) % 255;
                c1 = (c1 + c0) % 255;
            }
            int x = ((c1 * -1) + ((length - 15 - start) * c0)) % 255;
            int y = (c1 - ((length - 15 + 1 - start) * c0)) % 255;
            if (x < 0) x += 255;
            if (y < 0) y += 255;
            return (ushort)((x << 8) | y);
        }

        public void install(LSA lsa, DatabaseCopy old)
        {
            if(old != null)
            {
                lock (databaselock)
                {
                    database_of_lsa.Remove(old);
                }
            }
            lock (databaselock)
            {
                database_of_lsa.Add(new DatabaseCopy(lsa));
            }
            foreach(Neighbor n in neighborList)
            {

                if(old != null)
                {
                    LSA todelete = null;
                    foreach (LSA l in n.retransnissionList)
                    {
                        if(l.LSType == old.lsa.LSType && l.AdvertisingRouter.Equals(old.lsa.AdvertisingRouter) && l.LinkStateID.Equals(old.lsa.LinkStateID))
                        {
                            todelete = l;
                            break;
                        }
                    }
                    if(todelete != null)
                    {
                        n.retransnissionList.Remove(todelete);
                    }
                }
            }

            new Thread(() => calculateSPF()).Start();
           
        }

        public Boolean flood(LSA lsa, Neighbor n, Port port)
        {
            Boolean added = false;
            Boolean received = false;

            for(int i = 0 ; i < 2 ; i++)
            {
                if(i == 0)
                {
                    foreach (Neighbor nei in neighborList)
                    {
                        if (nei.port == port1)
                        {
                            if (nei.state < 4)
                            {
                                continue;
                            }
                            else if (nei.state != 6)
                            {
                                LSA neighbor = checkrequestRet(nei, lsa);
                                if (neighbor != null)
                                {
                                    if (GetNewerLSA(lsa, neighbor) == neighbor)
                                    {
                                        continue;
                                    }
                                    else if (GetNewerLSA(lsa, neighbor) == null)
                                    {
                                        nei.requests.Remove(neighbor);
                                        continue;
                                    }
                                    else
                                    {
                                        nei.requests.Remove(neighbor);
                                    }
                                }
                            }
                            if (n != null && nei.ipAddress.Equals(n.ipAddress))
                            {
                                continue;
                            }
                            added = true;
                            nei.retransnissionList.Add(lsa);
                        }
                        if (!added)
                        {
                            continue;
                        }
                       
                        if(port == port1 && (port1_Dr_Bdr.Item1.Equals(n.ipAddress) || port1_Dr_Bdr.Item2.Equals(n.ipAddress)))
                        {
                            continue;
                        }
                        if(port == port1 && (port.ipAddress.Equals(port1_Dr_Bdr.Item2)))
                        {
                            continue;
                        }
                        /*
                (3) If the new LSA was received on this interface, and it was
                    received from either the Designated Router or the Backup
                    Designated Router, chances are that all the neighbors have
                    received the LSA already.  Therefore, examine the next
                    interface.

                (4) If the new LSA was received on this interface, and the
                    interface state is Backup (i.e., the router itself is the
                    Backup Designated Router), examine the next interface.  The
                    Designated Router will do the flooding on this interface.
                    However, if the Designated Router fails the router (i.e.,
                    the Backup Designated Router) will end up retransmitting the
                    updates.*/

                        if (port1 == port) received = true;
                        SendLSU(lsa, port1, nei);
                    }
                }
                else if (i == 1)
                {
                    foreach (Neighbor nei in neighborList)
                    {
                        if (nei.port == port2)
                        {
                            if (nei.state < 4)
                            {
                                continue;
                            }
                            else if (nei.state != 6)
                            {
                                LSA neighbor = checkrequestRet(nei, lsa);
                                if (neighbor != null)
                                {
                                    if (GetNewerLSA(lsa, neighbor) == neighbor)
                                    {
                                        continue;
                                    }
                                    else if (GetNewerLSA(lsa, neighbor) == null)
                                    {
                                        nei.requests.Remove(neighbor);
                                        continue;
                                    }
                                    else
                                    {
                                        nei.requests.Remove(neighbor);
                                    }
                                }
                            }
                            if (n != null && nei.ipAddress.Equals(n.ipAddress))
                            {
                                continue;
                            }
                            added = true;
                            nei.retransnissionList.Add(lsa);
                        }
                        if (!added)
                        {
                            continue;
                        }

                        if (port == port2 && (port2_Dr_Bdr.Item1.Equals(n.ipAddress) || port2_Dr_Bdr.Item2.Equals(n.ipAddress)))
                        {
                            continue;
                        }
                        if (port == port2 && (port.ipAddress.Equals(port2_Dr_Bdr.Item2)))
                        {
                            continue;
                        }
                        /*
                (3) If the new LSA was received on this interface, and it was
                    received from either the Designated Router or the Backup
                    Designated Router, chances are that all the neighbors have
                    received the LSA already.  Therefore, examine the next
                    interface.

                (4) If the new LSA was received on this interface, and the
                    interface state is Backup (i.e., the router itself is the
                    Backup Designated Router), examine the next interface.  The
                    Designated Router will do the flooding on this interface.
                    However, if the Designated Router fails the router (i.e.,
                    the Backup Designated Router) will end up retransmitting the
                    updates.*/

                        if (port2 == port) received = true;
                        SendLSU(lsa, port2, nei);
                    }
                }
            }

            return received;
        }

        public void SendLSU(LSA lsa, Port port, Neighbor neighbor)
        {
            EthernetPacket eth = new EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("01-00-5E-00-00-05"), EthernetPacketType.IPv4);
            IPv4Packet ip;

            if((port == port1 && (port1_Dr_Bdr.Item1.Equals(port.ipAddress) || port1_Dr_Bdr.Item2.Equals(port.ipAddress))) || 
                (port == port2 && (port2_Dr_Bdr.Item1.Equals(port.ipAddress) || port2_Dr_Bdr.Item2.Equals(port.ipAddress))))
            {
                ip = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.5")) {
                    TimeToLive = 1,
                };
            }
            else
            {
                ip = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.6")) {
                    TimeToLive = 1,
                };
            }

            OSPFv2LSUpdatePacket lsu = new OSPFv2LSUpdatePacket(new List<LSA>() { lsa }) {
                AreaID = IPAddress.Parse("0.0.0.0"),
                Checksum = 0,
                RouterID = routerID,
            };

            lsu.Checksum = calculateChecksum(lsu.HeaderData, 0, lsu.HeaderData.Length);
            eth.PayloadPacket = ip;
            ip.PayloadPacket = lsu;
            ip.UpdateIPChecksum();
            port.device.SendPacket(eth);


        }

        public void lsaTimer()
        {
            while (true)
            {
                List<DatabaseCopy> todelete = new List<DatabaseCopy>();
                lock (databaselock)
                {
                    foreach (DatabaseCopy c in database_of_lsa)
                    {
                        c.age++;
                        if (c.lsa.LSAge < 3600)
                            c.lsa.LSAge++;
                        else if (c.lsa.LSAge == 1800)
                        {
                            if (c.lsa.AdvertisingRouter.Equals(routerID) || (c.lsa.LSType == LSAType.Network && (c.lsa.LinkStateID.Equals(port1.ipAddress) || c.lsa.LinkStateID.Equals(port2.ipAddress))))
                            {
                                //generatenew
                                //fake generator :D 
                                c.lsa.LSAge = 0;
                                c.lsa.LSSequenceNumber += 1;
                                flood(c.lsa, null, null);
                            }
                        }
                        else
                        {
                            if (getNei(4, 5) == 0 && !checkretransmission(c.lsa))
                            {
                                todelete.Add(c);
                            }
                        }
                    }
                    foreach (DatabaseCopy c in todelete)
                    {
                        database_of_lsa.Remove(c);
                    }
                }
                todelete.Clear();

                Thread.Sleep(1000);
            }
        }

        public Boolean checkretransmission(LSA lsa) {

            foreach(Neighbor n in neighborList)
            {
                foreach(LSA l in n.retransnissionList)
                {
                    if(l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LSType.Equals(lsa.LSType) && l.LinkStateID.Equals(lsa.LinkStateID))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public List<IPAddress> getrouterIDS(Port port)
        {
            List<IPAddress> routerIDs = new List<IPAddress>();
            foreach(Neighbor n in neighborList)
            {
                if(n.state == 6 && n.port == port)
                {
                    routerIDs.Add(n.neighbor);
                }
            }
            return routerIDs;
        }

        public void networkLSAgenerator(Port port, IPAddress dr)
        {
            List<IPAddress> routerIDs = getrouterIDS(port);
            routerIDs.Add(routerID);

            if(routerIDs.Count <= 1)
            {
                return;
            }

            uint oldseq = 0x80000000;
            NetworkLSA net = null;
            if(networkLSA.Count > 0)
            {
                foreach(NetworkLSA n in networkLSA)
                {
                    if (n.LinkStateID.Equals(port.ipAddress))
                    {
                        net = n;
                        break;
                    }
                }
            }

            if (net != null)
            {
                oldseq = net.LSSequenceNumber;
                networkLSA.Remove(net);
            }

            net = new NetworkLSA(routerIDs) {
                Options = 2,
                LSAge = 0,
                LinkStateID = dr,
                AdvertisingRouter = routerID,
                LSSequenceNumber = oldseq + 1,
                Checksum = 0,
                NetworkMask = port.mask,

            };

            net.Checksum = Fletcher(net.Bytes, 2, net.Length);
            networkLSA.Add(net);
            //FLood
            flood(net, null, null);
            DatabaseCopy old = getLSas(net);
            install(net, old);

        }

        public Boolean checkrequest(Neighbor n, LSA lsa)
        {

            foreach (LSA l in n.requests)
            {
                if (l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LSType.Equals(lsa.LSType) && l.LinkStateID.Equals(lsa.LinkStateID))
                {
                    return true;
                }
            }
            return false;
        }

        public LSA checkrequestRet(Neighbor n, LSA lsa)
        {

            foreach (LSA l in n.requests)
            {
                if (l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LSType.Equals(lsa.LSType) && l.LinkStateID.Equals(lsa.LinkStateID))
                {
                    return l;
                }
            }
            return null;
        }

        public Boolean checkRetrans(Neighbor n, LSA lsa)
        {
            foreach (LSA l in n.retransnissionList)
            {
                if (l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LSType.Equals(lsa.LSType) && l.LinkStateID.Equals(lsa.LinkStateID))
                {
                    return true;
                }
            }
            return false;
        }

        public void delayedACK(Port port, LSA lsa)
        {
            EthernetPacket eth = new EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("01-00-5E-00-00-05"), EthernetPacketType.IPv4);
            IPv4Packet ip;

            if ((port == port1 && (port.ipAddress.Equals(port1_Dr_Bdr.Item1) || port1_Dr_Bdr.Item2.Equals(port.ipAddress))) || 
                    (port == port2 && (port.ipAddress.Equals(port2_Dr_Bdr.Item1) || port2_Dr_Bdr.Item2.Equals(port.ipAddress))))
            {
                    ip = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.5")) {
                        TimeToLive = 1,
                        Checksum = 0,
                    };
            }
            else
            {
                ip = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.6"))
                {
                    TimeToLive = 1,
                    Checksum = 0,
                };
            }

            OSPFv2LSAPacket ack = new OSPFv2LSAPacket(new List<LSA>() { lsa }) {
                AreaID = IPAddress.Parse("0.0.0.0"),
                RouterID = routerID,
                Checksum = 0,
            };

            ack.Checksum = calculateChecksum(ack.HeaderData, 0, ack.HeaderData.Length);
            ip.PayloadPacket = ack;
            eth.PayloadPacket = ip;
            ip.UpdateIPChecksum();
            port.device.SendPacket(eth);
               
        }

        public void calculateSPF()
        {
            lock (spflock)
            {
                //add old ospf routes to old list
                List<Route> old_rt = new List<Route>();

                lock (router.locker)
                {
                    foreach(Route r in router.routingTable)
                    {
                        if(r.type == "O")
                        {
                            old_rt.Add(r);
                        }
                    }

                    //clear ospf routes from routing table
                    foreach (Route r in old_rt)
                    {
                        router.routingTable.Remove(r);
                    }
                }

                List<Vertex> shortTree = new List<Vertex>();
                List<Vertex> candidates = new List<Vertex>();
                Vertex root = new Vertex(routerID, routerLSA, 0, null);
                shortTree.Add(root);

                List<DatabaseCopy> copy = database_of_lsa;

                getCandidates(root, candidates, routerLSA, shortTree, copy, 0);

                Vertex min;
                while(candidates.Count > 0)
                {
                    min = getFirstMinVertex(candidates);
                    shortTree.Add(min);
                    removeCandidate(candidates, min);
                    getCandidates(min, candidates, min.lsa, shortTree, copy, min.cost);
                }

                foreach(Vertex v in shortTree)
                {
                    if (v.lsa.LSType == LSAType.Router)
                    {
                        foreach (RouterLink rlink in ((RouterLSA)v.lsa).RouterLinks) {
                            Console.WriteLine(rlink.LinkID + " vsetky linky");
                            if(rlink.Type == 3)
                            {
                                Vertex aktual = v, previous = v;
                                while (!aktual.id.Equals(routerID))
                                {
                                    if (aktual.lsa.LSType == LSAType.Router)
                                    {
                                        previous = aktual;
                                    }
                                    aktual = aktual.nexthop;
                                }
                                if (previous.lsa.LSType == LSAType.Network) continue;
                                Port port = null;
                                IPAddress nexthop = null;
                                foreach (RouterLink r in ((RouterLSA)previous.lsa).RouterLinks)
                                {
                                    if (IPtoNet(r.LinkData, port1.mask).Equals(IPtoNet(port1.ipAddress, port1.mask)))
                                    {
                                        port = port1;
                                        nexthop = r.LinkData;
                                    }
                                    else if (IPtoNet(r.LinkData, port2.mask).Equals(IPtoNet(port2.ipAddress, port2.mask)))
                                    {
                                        port = port2;
                                        nexthop = r.LinkData;
                                    }
                                }
                                Route ospfroute = new Route();
                                ospfroute.network = IPtoNet(rlink.LinkID, rlink.LinkData);
                                ospfroute.mask = rlink.LinkData;
                                ospfroute.cidr = router.convertMaskToCidr(rlink.LinkData);
                                if (port != null) ospfroute.outputPort = port;
                                if (nexthop != null) ospfroute.nextHop = nexthop;
                                ospfroute.type = "O";
                                ospfroute.metric = v.cost + rlink.Metric;
                                Console.WriteLine("TOTO vypocital spf ako stub" + ospfroute.network.ToString());
                                if (!router.routeIsInTable(ospfroute))
                                {
                                    lock (router.locker)
                                    {
                                        router.routingTable.AddLast(ospfroute);
                                    }
                                }

                            }
                        }
                    }
                    else {
                        Vertex aktual = v, previous = v;
                        while (!aktual.id.Equals(routerID)) {
                            if(aktual.lsa.LSType == LSAType.Router)
                            {
                                previous = aktual;
                            }
                            aktual = aktual.nexthop;
                        }
                        if (previous.lsa.LSType == LSAType.Network) continue;
                        Port port = null;
                        IPAddress nexthop = null;
                        foreach(RouterLink r in ((RouterLSA)previous.lsa).RouterLinks)
                        {
                            if (IPtoNet(r.LinkData, port1.mask).Equals(IPtoNet(port1.ipAddress, port1.mask))) {
                                port = port1;
                                nexthop = r.LinkData;
                            }
                            else if (IPtoNet(r.LinkData, port2.mask).Equals(IPtoNet(port2.ipAddress, port2.mask)))
                            {
                                port = port2;
                                nexthop = r.LinkData;
                            }
                        }
                        Route ospfroute = new Route();
                        ospfroute.network = IPtoNet(((NetworkLSA)v.lsa).LinkStateID, ((NetworkLSA)v.lsa).NetworkMask);
                        ospfroute.mask = ((NetworkLSA)v.lsa).NetworkMask;
                        ospfroute.cidr = router.convertMaskToCidr(((NetworkLSA)v.lsa).NetworkMask);
                        if(port != null) ospfroute.outputPort = port;
                        if (nexthop != null) ospfroute.nextHop = nexthop;
                        ospfroute.type = "O";
                        ospfroute.metric = v.cost;
                        if (!router.routeIsInTable(ospfroute))
                        {
                            lock (router.locker)
                            {
                                router.routingTable.AddLast(ospfroute);
                            }
                        }
                        
                    }
                }
            }
        }

        public void removeCandidate(List<Vertex> candidtes, Vertex lastadded)
        {
            List<Vertex> todel = new List<Vertex>();
            foreach (Vertex v in candidtes) {
                if (v.id.Equals(lastadded.id)) {
                    todel.Add(v);
                }
            }
            foreach (Vertex v in todel) {
                candidtes.Remove(v);
            }
        }

        public Vertex getFirstMinVertex(List<Vertex> candidates) {
            Vertex min = null;
            foreach(Vertex v in candidates)
            {
                if (min == null) min = v;
                else if (min.cost > v.cost) {
                    min = v;
                }
            }
            return min;
        }

        public NetworkLSA getNetworklsa(List<DatabaseCopy> database, IPAddress ip){
            foreach(DatabaseCopy db in database)
            {
                if(db.lsa.LSType == LSAType.Network && db.lsa.LinkStateID.Equals(ip))
                {
                    if (db.lsa.LSAge < 3600)
                    {
                        return (NetworkLSA)db.lsa;
                    }
                    else return null;
                }
            }
            return null;
        }
        public RouterLSA getRouterLSA(List<DatabaseCopy> database, IPAddress ip) {
            foreach(DatabaseCopy db in database)
            {
                if (db.lsa.LSType == LSAType.Router && db.lsa.LinkStateID.Equals(ip))
                {
                    if (db.lsa.LSAge < 3600)
                    {
                        return (RouterLSA)db.lsa;
                    }
                    else return null;
                }
            }
            return null;
        }

        public Boolean intree(LSA lsa, List<Vertex> tree)
        {
            foreach(Vertex v in tree)
            {
                if (v.id.Equals(lsa.LinkStateID) && lsa == v.lsa)
                {
                    return true;
                }
            }
            return false;
        }

        public void getCandidates(Vertex vertex, List<Vertex> candidates, LSA lsa, List<Vertex> tree , List<DatabaseCopy> database, int lastcost) {

            if(lsa.LSType == LSAType.Router)
            {
                foreach(RouterLink rlink in ((RouterLSA)lsa).RouterLinks)
                {
                    if (rlink.Type == 3)
                    {
                        continue;
                    }
                    else if (rlink.Type == 2)
                    {
                        //transit
                        NetworkLSA networklsa = getNetworklsa(database, rlink.LinkID);
                        if(networklsa != null && !intree(networklsa,tree))
                        {
                            Vertex candidate = new Vertex(networklsa.LinkStateID,networklsa,lastcost + rlink.Metric, vertex);
                            candidates.Add(candidate);
                        }
                    }
                    else {
                        //point to point
                        RouterLSA routerlsa = getRouterLSA(database, rlink.LinkID);
                        if (routerlsa != null && !intree(routerlsa, tree))
                        {
                            Vertex candidate = new Vertex(routerlsa.LinkStateID, routerlsa, lastcost + rlink.Metric, vertex);
                            candidates.Add(candidate);
                        }
                    }
                }
            }
            else //if (lsa.LSType == LSAType.Network)
            {
                foreach(IPAddress ip in ((NetworkLSA)lsa).AttachedRouters)
                {
                    RouterLSA routerlsa = getRouterLSA(database, ip);
                    if(routerlsa != null && !intree(routerlsa, tree))
                    {
                        Vertex candidate = new Vertex(routerlsa.LinkStateID, routerlsa,lastcost,vertex);
                        candidates.Add(candidate);
                    }
                }
            }

        }
    }
}
