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
    class SwRouter
    {
        Port port1, port2;

        public readonly object locker = new object();
        public readonly object locker_lsu = new object();
        public LinkedList<ArpTable> arpTable = new LinkedList<ArpTable>();
        public LinkedList<Route> routingTable = new LinkedList<Route>();
        public int id = 1;
        public int time = 60;

        public Route directRoute1 = new Route();
        public Route directRoute2 = new Route();
        public DynamicOSPF ospfProces = null;
        public Boolean ospfOn = false;



        public SwRouter(Port port1, Port port2)
        {
            this.port1 = port1;
            this.port2 = port2;

            port1.ipAddress = IPAddress.Parse("192.168.0.1");
            port1.mask = IPAddress.Parse("255.255.255.0");
            port2.ipAddress = IPAddress.Parse("192.168.1.1");
            port2.mask = IPAddress.Parse("255.255.255.0");

            port1.device.OnPacketArrival += new SharpPcap.PacketArrivalEventHandler(device_OnPacketArrival);
            ////dev1.Open(DeviceMode.Normal, 1000);
            port1.device.Open(OpenFlags.Promiscuous | OpenFlags.NoCaptureLocal, 10);
            port1.device.StartCapture();
            port2.device.OnPacketArrival += new SharpPcap.PacketArrivalEventHandler(device_OnPacketArrival);
            ////dev2.Open(DeviceMode.Normal, 1000);
            port2.device.Open(OpenFlags.Promiscuous | OpenFlags.NoCaptureLocal, 10);
            port2.device.StartCapture();

            Console.WriteLine(port1.device.MacAddress.ToString());

            Thread t1 = new Thread(() => timer());

            t1.Start();
        }

        private void device_OnPacketArrival(object sender, CaptureEventArgs packet)
        {
            int portNumber = -1;
            Port port;

            if ((WinPcapDevice)sender == port1.device)
                port = port1;
            else port = port2;

            String data = packet.Packet.ToString();

            var packet2 = PacketDotNet.Packet.ParsePacket(packet.Packet.LinkLayerType, packet.Packet.Data);

            var ethernetPacket = (PacketDotNet.EthernetPacket)packet2;

        if (ethernetPacket != null)
        {
            //lock (locker)
            //{
                // if (ethernetPacket.DestinationHwAddress.ToString() == port.device.MacAddress.ToString())
                //{
                    string protokol = findProtocol(ethernetPacket);

                    if (protokol != null && protokol == "ARP")
                    {

                        var ArpPacket = (ARPPacket)ethernetPacket.Extract(typeof(ARPPacket));

                    //Console.WriteLine(ArpPacket.Operation.ToString());

                    if (ArpPacket.TargetProtocolAddress.Equals(port.ipAddress) && ArpPacket.Operation.ToString().Equals("Response"))
                    {
                        Console.WriteLine("Pridaj do ARP tabulky");

                        if (checkARPTable(ArpPacket.SenderHardwareAddress.ToString(),ArpPacket.SenderProtocolAddress) == null)
                        {
                            // add new mac to table
                            ArpTable arp = new ArpTable();

                            arp.timer = time;
                            arp.ipAddress = ArpPacket.SenderProtocolAddress;
                            arp.MacAddress = ArpPacket.SenderHardwareAddress.ToString();
                            arp.outgoingInt = 0;

                            arpTable.AddLast(arp);

                        }
                    }
                    else if (ArpPacket.TargetProtocolAddress.Equals(port.ipAddress) && ArpPacket.Operation.ToString().Equals("Request"))
                    {
                       // Console.WriteLine("Send reply");
                        sendArpReply(port, ArpPacket);
                    }
                    else if (ArpPacket.Operation.ToString().Equals("Request"))
                    {
                        Tuple<IPAddress, Port> proxyArp = recursiveLookup(ArpPacket.TargetProtocolAddress, ArpPacket.TargetProtocolAddress);
                  //  Console.WriteLine("Proxy arp");
                        if (proxyArp == null || proxyArp.Item2 == port)
                        {
                      //  Console.WriteLine("Proxy arp drop");
                        return;
                        }
                        else if (proxyArp != null)
                        {
                      //  Console.WriteLine("Proxy arp send ");
                        sendProxyArpReply(port, ArpPacket);
                        }            
                    }
                    }

                    else if (protokol != null && protokol == "IP") {
                            
                            

              //  Console.WriteLine("Ip packet to send");

                        var IPv4packet = (IPv4Packet)ethernetPacket.Extract(typeof(IPv4Packet));

                var OSPFpacket = (OSPFv2Packet)IPv4packet.Extract(typeof(OSPFv2Packet));

                    if (OSPFpacket != null) {
                       
                        if (!ospfOn) return;

                        if (port.idPort == 1)
                            if (!ospfProces.port1_Dr_Bdr.Item1.Equals(port.ipAddress) && !ospfProces.port1_Dr_Bdr.Item2.Equals(port.ipAddress) && IPv4packet.DestinationAddress.Equals(IPAddress.Parse("224.0.0.6")))
                                return;
                        else if (port.idPort == 2)
                                if (!ospfProces.port2_Dr_Bdr.Item1.Equals(port.ipAddress) && !ospfProces.port2_Dr_Bdr.Item2.Equals(port.ipAddress) && IPv4packet.DestinationAddress.Equals(IPAddress.Parse("224.0.0.6")))
                                    return;
                        if (OSPFpacket.Type == OSPFPacketType.Hello)
                        {
                            //add neighbor
                            ospfProces.addNeighbor((OSPFv2HelloPacket)OSPFpacket, IPv4packet.SourceAddress, port);
                            var hello = (OSPFv2HelloPacket)OSPFpacket;
                            if (port.idPort == 1 && ospfProces.port1_Dr_Bdr.Item1.Equals(IPAddress.Parse("0.0.0.0")) && ospfProces.port1_Dr_Bdr.Item2.Equals(IPAddress.Parse("0.0.0.0")))
                            {
                                if ((hello.BackupRouterID.Equals(IPv4packet.SourceAddress) ||
								      (hello.DesignatedRouterID.Equals(IPv4packet.SourceAddress) && 
								       hello.BackupRouterID.Equals(IPAddress.Parse("0.0.0.0")))) && 
								    hello.NeighborID.Contains(ospfProces.routerID))
                                {
                                    if (hello.NeighborID.Contains(ospfProces.routerID))
                                    {
                                        lock (ospfProces.locker)
                                        {
                                            if (!ospfProces.initCalculation)
                                            {
                                                ospfProces.initCalculation = true;
                                                ospfProces.chooseDRBDR(port, ospfProces.port1_Dr_Bdr);
                                            }
                                        }
                                    }
                                }
                            }
                            else if (port.idPort == 2 && ospfProces.port2_Dr_Bdr.Item1.Equals(IPAddress.Parse("0.0.0.0")) && ospfProces.port2_Dr_Bdr.Item2.Equals(IPAddress.Parse("0.0.0.0")))
                            {
								if ((hello.BackupRouterID.Equals(IPv4packet.SourceAddress) || 
								     (hello.DesignatedRouterID.Equals(IPv4packet.SourceAddress) && 
								      hello.BackupRouterID.Equals(IPAddress.Parse("0.0.0.0")))) && 
								    hello.NeighborID.Contains(ospfProces.routerID))
                                {
                                        lock (ospfProces.locker)
                                        {
                                            if (!ospfProces.initCalculation)
                                            {
                                                ospfProces.initCalculation = true;
                                                ospfProces.chooseDRBDR(port, ospfProces.port2_Dr_Bdr);
                                            }
                                        }
                                }
                            }
                        }
                        else if (OSPFpacket.Type == OSPFPacketType.DatabaseDescription)
                        {
                            OSPFv2DDPacket databasePacket = (OSPFv2DDPacket)OSPFpacket;

                            Console.WriteLine("DB exchange");

                            Neighbor n = ospfProces.getNeighbor(OSPFpacket.RouterID, IPv4packet.SourceAddress);

                            if (n != null)
                            {
                                if (n.state == 3)
                                {
                                    if (databasePacket.DBDescriptionBits == 7 && databasePacket.LSAHeader.Count == 0 && n.master)
                                    {
                                        n.lastDatabase_received = databasePacket;
                                        n.database_seq = databasePacket.DDSequence;
                                        n.state = 4;
                                    }
                                    else if (databasePacket.DBDescriptionBits == 2 && databasePacket.DDSequence == n.lastDatabase_sent.DDSequence && !n.master)
                                    {
                                        n.lastDatabase_received = databasePacket;
                                        n.database_seq++;
                                        n.state = 4;
                                        ospfProces.processDatabasePacket(n, databasePacket.LSAHeader);
                                    }
                                    else
                                    {
                                        return;
                                    }
                                }
                                else if (n.state == 4)
                                {
                                    if (!n.master && n.lastDatabase_received.DBDescriptionBits == databasePacket.DBDescriptionBits &&
                                        n.lastDatabase_received.DBDescriptionOptions == databasePacket.DBDescriptionOptions &&
                                        n.lastDatabase_received.DDSequence == databasePacket.DDSequence)
                                    {
                                        return;
                                    }
                                    Boolean masterBit = getBitFromBite(databasePacket.DBDescriptionBits, 1);
                                    Boolean initBit = getBitFromBite(databasePacket.DBDescriptionBits, 3);

                                    if (masterBit != n.master || initBit || n.lastDatabase_received.DBDescriptionOptions != databasePacket.DBDescriptionOptions)
                                    {
                                        n.database_seq++;
                                        n.state = 3;
                                        //extart thread
                                        Thread tt = new Thread(() => ospfProces.initDatabaseExchange(n));
                                        tt.Start();
                                        return;
                                    }

                                    if ((!n.master && databasePacket.DDSequence == n.lastDatabase_sent.DDSequence) ||
                                        (n.master && n.lastDatabase_sent.DDSequence + 1 == databasePacket.DDSequence))
                                    {
                                        ospfProces.processDatabasePacket(n, databasePacket.LSAHeader);

                                        if (n.master)
                                        {
                                            if (n.lastDatabase_received.DBDescriptionBits == databasePacket.DBDescriptionBits &&
                                                n.lastDatabase_received.DBDescriptionOptions == databasePacket.DBDescriptionOptions &&
                                                n.lastDatabase_received.DDSequence == databasePacket.DDSequence)
                                            {
                                                EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, ethernetPacket.SourceHwAddress, EthernetPacketType.IPv4);
                                                IPv4Packet ipPacket = new IPv4Packet(port.ipAddress, n.ipAddress)
                                                {
                                                    Checksum = 0,
                                                    TimeToLive = 1
                                                };

                                                ethpacket.PayloadPacket = ipPacket;
                                                ipPacket.PayloadPacket = n.lastDatabase_sent;
                                                ipPacket.UpdateIPChecksum();
                                                port.device.SendPacket(ethpacket);

                                            }
                                            else
                                            {
                                                n.database_seq = databasePacket.DDSequence;
                                                n.lastDatabase_received = databasePacket;
                                                if (n.lastDatabase_sent.DBDescriptionBits == 0 && databasePacket.DBDescriptionBits == 1)
                                                {
                                                    n.state = 5;

                                                }
                                            }
                                        }
                                        else
                                        {
                                            n.database_seq++;
                                            n.lastDatabase_received = databasePacket;
                                            if (databasePacket.DBDescriptionBits == 0 && n.lastDatabase_sent.DBDescriptionBits == 1)
                                            {
                                                n.state = 5;
                                            }
                                        }
                                    }
                                    else
                                    {
                                        n.database_seq++;
                                        n.state = 3;
                                        //extart thread
                                        Thread tt = new Thread(() => ospfProces.initDatabaseExchange(n));
                                        tt.Start();
                                        return;
                                    }

                                }
                                else if (n.state == 5 || n.state == 6)
                                {
                                    Boolean ibit = getBitFromBite(databasePacket.DBDescriptionBits, 3);

                                    if (ibit)
                                    {
                                        if (n.state == 6)
                                        {
                                            n.state = 3;
                                            ospfProces.generateLSA(port);
                                            if ((port == port1 && ospfProces.port1_Dr_Bdr.Item1.Equals(port.ipAddress)) || (port == port2 && ospfProces.port2_Dr_Bdr.Item1.Equals(port.ipAddress)))
                                            {
                                                ospfProces.networkLSAgenerator(port, port.ipAddress);
                                            }
                                            n.database_seq++;
                                            n.state = 3;
                                            //extart thread
                                            Thread tt = new Thread(() => ospfProces.initDatabaseExchange(n));
                                            tt.Start();
                                            return;
                                        }
                                    }
                                }

                            }
                        }
                        else if ((OSPFpacket.Type == OSPFPacketType.LinkStateRequest) && IPv4packet.DestinationAddress.Equals(port.ipAddress))
                        {
                            OSPFv2LSRequestPacket requestPacket = (OSPFv2LSRequestPacket)OSPFpacket;

                            Console.WriteLine("DB request");

                            Neighbor n = ospfProces.getNeighbor(OSPFpacket.RouterID, IPv4packet.SourceAddress);

                            List<LSA> lsalist = new List<LSA>();
                            LSA lsa;

                            foreach (LinkStateRequest req in requestPacket.LinkStateRequests)
                            {
                                lsa = ospfProces.getLSArequest(req);
                                if (lsa == null)
                                {
                                    n.database_seq++;
                                    n.state = 3;
                                    //extart thread
                                    Thread tt = new Thread(() => ospfProces.initDatabaseExchange(n));
                                    tt.Start();
                                    return;
                                }
                                else
                                {
                                    lsalist.Add(lsa);
                                }
                            }
                            EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, ethernetPacket.SourceHwAddress, EthernetPacketType.IPv4);
                            IPv4Packet ipPacket = new IPv4Packet(port.ipAddress, IPv4packet.SourceAddress);
                            ipPacket.TimeToLive = 1;
                            ipPacket.Checksum = 0;

                            OSPFv2LSUpdatePacket updatepacket = new OSPFv2LSUpdatePacket(lsalist);
                            updatepacket.AreaID = IPAddress.Parse("0.0.0.0");
                            updatepacket.RouterID = ospfProces.routerID;
                            updatepacket.Checksum = 0;

                            updatepacket.Checksum = ospfProces.calculateChecksum(updatepacket.HeaderData, 0, updatepacket.HeaderData.Length);
                            ethpacket.PayloadPacket = ipPacket;
                            ipPacket.PayloadPacket = updatepacket;
                            ipPacket.UpdateIPChecksum();

                            n.port.device.SendPacket(ethpacket);
                        }
                        else if (OSPFpacket.Type == OSPFPacketType.LinkStateAcknowledgment)
                        {

                            OSPFv2LSAPacket ack = (OSPFv2LSAPacket)OSPFpacket;

                            Console.WriteLine("Ack");

                            Neighbor n = ospfProces.getNeighbor(OSPFpacket.RouterID, IPv4packet.SourceAddress);

                            if (n == null || n.state < 4)
                            {
                                return;
                            }

                            foreach (LSA lsa in ack.LSAAcknowledge)
                            {
                                LSA delete = null;
                                foreach (LSA l in n.retransnissionList)
                                {
                                    if (l.LSType == lsa.LSType && l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LinkStateID.Equals(lsa.LinkStateID))
                                    {
                                        delete = l;
                                    }
                                }
                                if (delete != null)
                                    n.retransnissionList.Remove(delete);
                            }
                        }
                        else if (OSPFpacket.Type == OSPFPacketType.LinkStateUpdate)
                        {
                            lock (locker_lsu) {
                                OSPFv2LSUpdatePacket lsu = (OSPFv2LSUpdatePacket)OSPFpacket;

                                Neighbor nei = ospfProces.getNeighbor(OSPFpacket.RouterID, IPv4packet.SourceAddress);

                                if (nei == null || nei.state <= 4)
                                {
                                    return;
                                }
                                foreach (LSA lsa in lsu.LSAUpdates)
                                {
                                    if (lsa.LSType == LSAType.Router || lsa.LSType == LSAType.Network)
                                    {
                                        if (lsa.LSAge == 3600 && ospfProces.getLSas(lsa) == null && ospfProces.getNei(4, 5) == 0)
                                        {
                                            //ack
                                            EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, ethernetPacket.SourceHwAddress, EthernetPacketType.IPv4);
                                            IPv4Packet ip = new IPv4Packet(port.ipAddress, nei.ipAddress);
                                            OSPFv2LSAPacket ack = new OSPFv2LSAPacket(new List<LSA>() { lsa })
                                            {
                                                AreaID = IPAddress.Parse("0.0.0.0"),
                                                RouterID = ospfProces.routerID,
                                                Checksum = 0,
                                            };

                                            ip.TimeToLive = 1;
                                            ip.Checksum = 0;

                                            ack.Checksum = ospfProces.calculateChecksum(ack.HeaderData, 0, ack.HeaderData.Length);
                                            ethpacket.PayloadPacket = ip;
                                            ip.PayloadPacket = ack;
                                            ip.UpdateIPChecksum();
                                            port.device.SendPacket(ethpacket);

                                            continue;
                                        }

                                        DatabaseCopy getlsa = ospfProces.getLSas(lsa);

                                        if (getlsa == null || ospfProces.GetNewerLSA(getlsa.lsa, lsa) == lsa)
                                        {
                                            if (getlsa != null && getlsa.age <= 1)
                                            {

                                                continue;
                                            }
                                            bool ret = ospfProces.flood(lsa, nei, port);

                                            foreach (Neighbor n in ospfProces.neighborList)
                                            {
                                                if (getlsa != null)
                                                    n.retransnissionList.Remove(getlsa.lsa);
                                            }
                                            ospfProces.install(lsa, getlsa);

                                            //possible ack
                                            if (!ret)
                                            {
                                                //ack2
                                                if (port == port1)
                                                {
                                                    if (ospfProces.port1_Dr_Bdr.Item2.Equals(port.ipAddress))
                                                    {
                                                        if (ospfProces.port1_Dr_Bdr.Item1.Equals(nei.ipAddress))
                                                        {
                                                            //delayed ack
                                                            ospfProces.delayedACK(port, lsa);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        //delayed ack
                                                        ospfProces.delayedACK(port, lsa);
                                                    }
                                                }
                                                else
                                                {
                                                    if (ospfProces.port2_Dr_Bdr.Item2.Equals(port.ipAddress))
                                                    {
                                                        if (ospfProces.port2_Dr_Bdr.Item1.Equals(nei.ipAddress))
                                                        {
                                                            //delayed ack
                                                            ospfProces.delayedACK(port, lsa);
                                                        }
                                                    }
                                                    else
                                                    {
                                                        //delayed ack
                                                        ospfProces.delayedACK(port, lsa);
                                                    }
                                                }
                                            }


                                            if (lsa.AdvertisingRouter.Equals(ospfProces.routerID) || (lsa.LSType == LSAType.Network && (lsa.LinkStateID.Equals(port1.ipAddress) || lsa.LinkStateID.Equals(port2.ipAddress))))
                                            {
                                                //selforiginated
                                                if (lsa.LSType == LSAType.Router)
                                                {
                                                    if (ospfProces.GetNewerLSA(lsa, ospfProces.routerLSA).Equals(lsa))
                                                    {
                                                        RouterLSA newRlsa = new RouterLSA(ospfProces.routerLinks)
                                                        {
                                                            Options = 2,
                                                            LSAge = 0,
                                                            LinkStateID = ospfProces.routerID,
                                                            AdvertisingRouter = ospfProces.routerID,
                                                            LSSequenceNumber = lsa.LSSequenceNumber + 1,
                                                            Checksum = 0,
                                                            VBit = 0,
                                                            EBit = 0,
                                                            BBit = 0,
                                                        };
                                                        newRlsa.Checksum = ospfProces.Fletcher(newRlsa.Bytes, 2, newRlsa.Length);
                                                        ospfProces.routerLSA = newRlsa;
                                                        //flooooooood
                                                        ospfProces.flood(newRlsa, null, null);
                                                        DatabaseCopy old = ospfProces.getLSas(lsa);
                                                        ospfProces.install(newRlsa, old);
                                                    }

                                                }
                                                else if (lsa.LSType == LSAType.Network && (port.ipAddress.Equals(ospfProces.port1_Dr_Bdr.Item1) || port.ipAddress.Equals(ospfProces.port2_Dr_Bdr.Item1)))
                                                {
                                                    IPAddress dr = port.idPort == 1 ? ospfProces.port1_Dr_Bdr.Item1 : ospfProces.port2_Dr_Bdr.Item1;
                                                    NetworkLSA netlsa = null;
                                                    List<IPAddress> routerID = ospfProces.getrouterIDS(port);
                                                    if (routerID.Count == 0) continue;
                                                    routerID.Add(ospfProces.routerID);
                                                    NetworkLSA newLSA = new NetworkLSA(routerID)
                                                    {
                                                        Options = 2,
                                                        LSAge = 0,
                                                        LinkStateID = dr,
                                                        AdvertisingRouter = ospfProces.routerID,
                                                        LSSequenceNumber = lsa.LSSequenceNumber + 1,
                                                        Checksum = 0,
                                                        NetworkMask = port.mask,
                                                    };

                                                    if (ospfProces.networkLSA.Count > 0)
                                                    {
                                                        foreach (NetworkLSA l in ospfProces.networkLSA)
                                                        {
                                                            if (lsa.LinkStateID.Equals(port.ipAddress))
                                                                netlsa = l;
                                                        }
                                                    }
                                                    if (netlsa != null)
                                                    {
                                                        ospfProces.networkLSA.Remove(netlsa);
                                                    }
                                                    newLSA.Checksum = ospfProces.Fletcher(newLSA.Bytes, 2, newLSA.Length);
                                                    ospfProces.networkLSA.Add(newLSA);
                                                    // Flood
                                                    ospfProces.flood(newLSA, null, null);
                                                    DatabaseCopy old = ospfProces.getLSas(netlsa);
                                                    ospfProces.install(newLSA, old);

                                                }
                                                else if (lsa.LSType == LSAType.Network)
                                                {
                                                    lsa.LSAge = 3600;
                                                    //flooood
                                                    ospfProces.flood(lsa, null, null);
                                                }
                                            }
                                        }
                                        else if (ospfProces.checkrequest(nei, lsa))
                                        {
                                            nei.database_seq++;
                                            nei.state = 3;
                                            //extart thread
                                            Thread tt = new Thread(() => ospfProces.initDatabaseExchange(nei));
                                            tt.Start();
                                            return;
                                        }


                                        else if (ospfProces.GetNewerLSA(lsa, getlsa.lsa) == null)
                                        {
                                            LSA lsatodelete = null;
                                            if (ospfProces.checkRetrans(nei, lsa))
                                            {
                                                foreach (LSA l in nei.retransnissionList)
                                                {
                                                    if (l.AdvertisingRouter.Equals(lsa.AdvertisingRouter) && l.LSType.Equals(lsa.LSType) && l.LinkStateID.Equals(lsa.LinkStateID))
                                                    {
                                                        lsatodelete = l;
                                                        break;
                                                    }
                                                }
                                                nei.retransnissionList.Remove(lsatodelete);

                                                if ((ospfProces.port1_Dr_Bdr.Item2.Equals(port.ipAddress) && ospfProces.port1_Dr_Bdr.Item1.Equals(nei.ipAddress)) ||
                                                    (ospfProces.port2_Dr_Bdr.Item2.Equals(port.ipAddress) && ospfProces.port2_Dr_Bdr.Item1.Equals(nei.ipAddress)))
                                                {
                                                    //ack
                                                    EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("01-00-5E-00-00-05"), EthernetPacketType.IPv4);
                                                    IPv4Packet ip = new IPv4Packet(port.ipAddress, IPAddress.Parse("224.0.0.6"));
                                                    OSPFv2LSAPacket ack = new OSPFv2LSAPacket(new List<LSA>() { lsa })
                                                    {
                                                        AreaID = IPAddress.Parse("0.0.0.0"),
                                                        RouterID = ospfProces.routerID,
                                                        Checksum = 0,
                                                    };

                                                    ip.TimeToLive = 1;
                                                    ip.Checksum = 0;

                                                    ack.Checksum = ospfProces.calculateChecksum(ack.HeaderData, 0, ack.HeaderData.Length);
                                                    ethpacket.PayloadPacket = ip;
                                                    ip.PayloadPacket = ack;
                                                    ip.UpdateIPChecksum();
                                                    port.device.SendPacket(ethpacket);

                                                }
                                            }
                                            else
                                            {
                                                EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, ethernetPacket.SourceHwAddress, EthernetPacketType.IPv4);
                                                IPv4Packet ip = new IPv4Packet(port.ipAddress, nei.ipAddress);
                                                OSPFv2LSAPacket ack = new OSPFv2LSAPacket(new List<LSA>() { lsa })
                                                {
                                                    AreaID = IPAddress.Parse("0.0.0.0"),
                                                    RouterID = ospfProces.routerID,
                                                    Checksum = 0,
                                                };

                                                ip.TimeToLive = 1;
                                                ip.Checksum = 0;

                                                ack.Checksum = ospfProces.calculateChecksum(ack.HeaderData, 0, ack.HeaderData.Length);
                                                ethpacket.PayloadPacket = ip;
                                                ip.PayloadPacket = ack;
                                                ip.UpdateIPChecksum();
                                                port.device.SendPacket(ethpacket);
                                            }
                                        }
                                        else
                                        {
                                            if (getlsa.lsa.LSAge == 3600 && getlsa.lsa.LSSequenceNumber == 0x7fffffff)
                                            {
                                                continue;
                                            }
                                            EthernetPacket ethpacket = new EthernetPacket(port.device.MacAddress, ethernetPacket.SourceHwAddress, EthernetPacketType.IPv4);
                                            IPv4Packet ip = new IPv4Packet(port.ipAddress, nei.ipAddress);
                                            ip.TimeToLive = 1;

                                            OSPFv2LSUpdatePacket lsu2 = new OSPFv2LSUpdatePacket()
                                            {
                                                AreaID = IPAddress.Parse("0.0.0.0"),
                                                Checksum = 0,
                                                RouterID = ospfProces.routerID,
                                            };
                                            lsu2.Checksum = ospfProces.calculateChecksum(lsu2.HeaderData, 0, lsu2.HeaderData.Length);
                                            ethpacket.PayloadPacket = ip;
                                            ip.PayloadPacket = lsu2;
                                            ip.UpdateIPChecksum();
                                            port.device.SendPacket(ethpacket);
                                        }
                                    }
                                }
                            }
                        }


                        return;
                    }

                        if (IPv4packet.DestinationAddress.ToString() == port.ipAddress.ToString()) {
                            Console.WriteLine("Packet was for me");
                        }
                        else
                        {

                  //  Console.WriteLine("Packet to send");
                            Tuple<IPAddress, Port> sendPacketTo = recursiveLookup(IPv4packet.DestinationAddress, IPv4packet.DestinationAddress);

                            if (sendPacketTo != null)
                            {

                        Console.WriteLine("I am gonna send that shit");
                        // if mac not found arp request
                        String destMac = findDestMac(sendPacketTo.Item1);

                                if (destMac != null) sendRoutedPacket(sendPacketTo.Item2, IPv4packet, destMac);

                                else
                                {
                                    sendArpRequestRouting(sendPacketTo.Item1,sendPacketTo.Item2);
                                    int turns = 0;

                                    //while (destMac == null && turns != 5)
                                    //{
                                    //    destMac = findDestMac(sendPacketTo.Item1);

                                    //    System.Threading.Thread.Sleep(500);
                                    //    turns++;
                                    //}
                                    if (destMac == null)
                                    {
                                        Console.WriteLine("Host is unreachable, drop packet");
                                        return;
                                    }
                                    sendRoutedPacket(sendPacketTo.Item2, IPv4packet, destMac);
                                }
                                // change dest mac
                                // send packet
                            }
                            else return;
                        }
                    // }
                // }
            }
        }
        }

        private void timer()
        {

            while (true)
            {
              //  Console.WriteLine("I am Timer");
                lock (locker)
                {
                    if (arpTable.First != null)
                    {
                        /*foreach (var i in tabulka)
                        {
                            i.timer--;
                        }*/
                        var j = arpTable.First;
                        while (j != null)
                        {
                            //Console.WriteLine(j.Value.ipAddress.ToString() + " " + j.Value.timer);
                            j.Value.timer--;
                            if (j.Value.timer == 0)
                                arpTable.Remove(j);
                            j = j.Next;
                        }
                    }
                }
                System.Threading.Thread.Sleep(1000);
            }

        }

        public void sendArpRequest(IPAddress destIP) {

            Port port = findPortToSendArp(destIP);

            Console.WriteLine(port.idPort);
           // Console.WriteLine(port.device.Addresses[1].Addr.hardwareAddress.ToString());

            var ethernetPacket = new PacketDotNet.EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"), PacketDotNet.EthernetPacketType.Arp);

            var arpPacket = new PacketDotNet.ARPPacket(PacketDotNet.ARPOperation.Request, PhysicalAddress.Parse("00-00-00-00-00-00"), destIP, port.device.MacAddress, port.ipAddress);

            ethernetPacket.PayloadPacket = arpPacket;

            //sendARP
            port.device.SendPacket(ethernetPacket);
        }

        public void sendArpRequestRouting(IPAddress destIP, Port port) {

            var ethernetPacket = new PacketDotNet.EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse("FF-FF-FF-FF-FF-FF"), PacketDotNet.EthernetPacketType.Arp);

            var arpPacket = new PacketDotNet.ARPPacket(PacketDotNet.ARPOperation.Request, PhysicalAddress.Parse("00-00-00-00-00-00"), destIP, port.device.MacAddress, port.ipAddress);

            ethernetPacket.PayloadPacket = arpPacket;

            //sendARP
            port.device.SendPacket(ethernetPacket);

        }

        public void updateIpAndMask(IPAddress newIp,IPAddress mask, int portNumber) {

            if (portNumber == 1)
            {
                this.port1.ipAddress = newIp;
                this.port1.mask = mask;
                this.port1.idPort = 1;

                Console.WriteLine("New IP set for: " + this.port1.ipAddress.ToString());
            }
            else if (portNumber == 2) {
                this.port2.ipAddress = newIp;
                this.port2.mask = mask;
                this.port2.idPort = 2;

                Console.WriteLine("New IP set for: " + this.port2.ipAddress.ToString());
            }
        }

        public Port findPortToSendArp(IPAddress requestedIP) {

            foreach (var i in routingTable) {
                if (i.network.Equals(IPtoNet(requestedIP, i.outputPort.mask))) {
                    return i.outputPort;
                }
            }
            return null;
        }

        public IPAddress IPtoNet(IPAddress ip, IPAddress mask)
        {
            byte[] bip = ip.GetAddressBytes();
            byte[] bmask = mask.GetAddressBytes();
            byte[] result = new byte[4];

            for (int i = 0; i < 4; i++) {
                result[i] = (byte)((int)bip[i] & (int)bmask[i]);
            } 

            return new IPAddress(result);
        }

        public int convertMaskToCidr(IPAddress mask)
        {
            return Convert.ToString(BitConverter.ToInt32(mask.GetAddressBytes(),0),2)
                .ToCharArray().Count(x => x == '1');
        }

        public void sendArpReply(Port port , ARPPacket packet)
        {
            var ethernetPacket = new PacketDotNet.EthernetPacket(port.device.MacAddress, packet.SenderHardwareAddress, PacketDotNet.EthernetPacketType.Arp);

            var arpPacket = new PacketDotNet.ARPPacket(PacketDotNet.ARPOperation.Response,packet.SenderHardwareAddress,packet.SenderProtocolAddress, port.device.MacAddress, port.ipAddress);

            ethernetPacket.PayloadPacket = arpPacket;

            //sendARP
            port.device.SendPacket(ethernetPacket);
        }

        private string findProtocol(EthernetPacket packet)
        {
            if (packet != null)
            {
                //Console.WriteLine("-------------------------------");
               // System.Diagnostics.Trace.WriteLine("{0}", packet.Type.ToString());
                //Console.WriteLine("-------------------------------");

                if (packet.Type.ToString().Equals("Arp"))
                {
                    //System.Diagnostics.Trace.WriteLine("{0}", packet.Type.ToString());
                    // Console.WriteLine("Som v ARP");
                    return "ARP";
                }
                else if (packet.Type.ToString().Equals("IPv4"))
                {
                    return "IP";
                }
                else
                    // System.Diagnostics.Trace.WriteLine("{0}", packet.Type.ToString());
                    return null;
            }
            return null;
        }

        private string checkARPTable(String lookedMAC, IPAddress ipaddr) {

            lock (locker)
            {
                foreach (var address in arpTable)
                {
                    if (address.MacAddress.Equals(lookedMAC))
                    {
                        address.timer = time;
                        address.ipAddress = ipaddr;
                        return address.MacAddress;
                    }
                }
            }
            return null;
        }

        public List<ArpTable> sendTable()
        {

            lock (locker)
            {

                List<ArpTable> list = new List<ArpTable>();
                foreach (var i in this.arpTable)
                {
                    list.Add(new ArpTable() { MacAddress = i.MacAddress, ipAddress = i.ipAddress, timer = i.timer, outgoingInt = i.outgoingInt });
                    // Console.WriteLine(i.port + " vypis z tabulky");
                }
                //foreach (var i in list)
                //  Console.WriteLine(i.sourceMac);


                return list;
            }
        }

        public void deleteTable()
        {
            lock (locker)
            {
                if (arpTable.First != null)
                {
                    arpTable.Clear();
                }
            }
        }

        public void setTimer(int newTime)
        {
            lock (locker)
            {
                time = newTime;
            }

        }

        public void updateTable()
        {
            lock (locker)
            {
                foreach (var i in this.arpTable)
                {
                    i.timer = time;
                }
            }
        }

        public List<Route> sendTableR()
        {
            List<Route> list = new List<Route>();
            lock (locker)
            {
                foreach (var i in this.routingTable)
                {
                    list.Add(new Route()
                    {
                        network = i.network,
                        cidr = i.cidr,
                        id = i.id,
                        type = i.type,
                        metric = i.metric
                    ,
                        nextHop = i.nextHop,
                        outputPort = i.outputPort
                    });
                    //Console.WriteLine(i.outputPort.idPort + " vypis z tabulky");
                }
                //foreach (var i in list)
                //  Console.WriteLine(i.sourceMac);

            }
            return list;
            
        }

        public void setDirectRoute(Port port) {

            if (port.idPort == 1)
            {
                var j = routingTable.First;

                lock (locker)
                {
                    while (j != null && j.Next != null)
                    {
                        if (j.Value.type == "C" && j.Value.outputPort.idPort == 1)
                        {
                            routingTable.Remove(j);
                        }
                        j = j.Next;
                    }
                    if (j != null && j.Value.type == "C" && j.Value.outputPort.idPort == 1)
                    {
                        routingTable.Remove(j);
                    }
                }

                this.directRoute1.cidr = convertMaskToCidr(port.mask);
                this.directRoute1.id = this.id++;
                this.directRoute1.metric = 1;
                this.directRoute1.network = IPtoNet(port.ipAddress, port.mask);
                this.directRoute1.nextHop = null;
                this.directRoute1.outputPort = port;
                this.directRoute1.type = "C";
                this.directRoute1.mask = port.mask;

                lock (locker)
                {
                    routingTable.AddFirst(directRoute1);
                }
            }
            else if(port.idPort == 2)
            {
                var j = routingTable.First;

                lock (locker)
                {
                    while (j != null && j.Next != null)
                    {
                        if (j.Value.type == "C" && j.Value.outputPort.idPort == 2)
                        {
                            routingTable.Remove(j);
                        }
                        j = j.Next;
                    }
                    if (j != null && j.Value.type == "C" && j.Value.outputPort.idPort == 2)
                    {
                        routingTable.Remove(j);
                    }
                }

                this.directRoute2.cidr = convertMaskToCidr(port.mask);
                this.directRoute2.id = this.id++;
                this.directRoute2.metric = 1;
                this.directRoute2.network = IPtoNet(port.ipAddress, port.mask);
                this.directRoute2.nextHop = null;
                this.directRoute2.outputPort = port;
                this.directRoute2.type = "C";
                this.directRoute2.mask = port.mask;

                lock (locker)
                {
                    routingTable.AddFirst(directRoute2);
                }
            }

            //foreach (var i in routingTable) {
            //   // Console.WriteLine(i.network.ToString());
            //}
        }

        public Boolean routeIsInTable(Route route) {

            lock (locker)
            {
                foreach (var i in this.routingTable)
                {
                    if (i.cidr == route.cidr && i.network.Equals(route.network))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        public Tuple<IPAddress, Port> recursiveLookup(IPAddress packetIP, IPAddress copyOriginal)
        {

            Route bestRoute = null;

            lock (locker)
            {
                foreach (Route route in routingTable)
                {
                    if (IPtoNet(packetIP, route.mask).Equals(route.network) && (bestRoute == null || 
                        bestRoute.cidr < route.cidr || 
                        (bestRoute.cidr == route.cidr && bestRoute.metric > route.metric)))
                        bestRoute = route;
                }

                if (bestRoute == null) return null;

                if (bestRoute.outputPort != null)
                {
                    if (bestRoute.nextHop != null) return Tuple.Create(bestRoute.nextHop, bestRoute.outputPort);

                    else
                    {
                        if (bestRoute.type.Equals("C")) return Tuple.Create(packetIP, bestRoute.outputPort);

                        else return Tuple.Create(copyOriginal, bestRoute.outputPort);
                    }
                }
                else if (bestRoute.nextHop != null)
                {
                    return recursiveLookup(bestRoute.nextHop, copyOriginal);
                }
                else return null;
            }



        }

        public String findDestMac(IPAddress lookedIP)
        {
            lock (locker)
            {
                foreach (ArpTable arp in arpTable)
                {
                    if (arp.ipAddress.Equals(lookedIP)) return arp.MacAddress;
                }
            }
            return null;
        }

        public void sendRoutedPacket(Port port, IPv4Packet packet, String destMac) {

            var ethernetPacket = new EthernetPacket(port.device.MacAddress, PhysicalAddress.Parse(destMac), PacketDotNet.EthernetPacketType.IPv4);
            packet.TimeToLive--;
            packet.UpdateIPChecksum();
            ethernetPacket.PayloadPacket = packet;

            port.device.SendPacket(ethernetPacket);

        }

        public void sendProxyArpReply(Port port, ARPPacket packet)
        {
            var ethernetPacket = new PacketDotNet.EthernetPacket(port.device.MacAddress, packet.SenderHardwareAddress, PacketDotNet.EthernetPacketType.Arp);

            var arpPacket = new PacketDotNet.ARPPacket(PacketDotNet.ARPOperation.Response, packet.SenderHardwareAddress, packet.SenderProtocolAddress, port.device.MacAddress, packet.TargetProtocolAddress);

            ethernetPacket.PayloadPacket = arpPacket;

            //sendProxyARP
            port.device.SendPacket(ethernetPacket);
        }

        public void turnOnOspf(IPAddress ip) {

            ospfProces = new DynamicOSPF(this.port1, this.port2,this,ip);
            ospfOn = true;
        }

        public Boolean getBitFromBite(Byte b ,int position) {
            return (b & (1 << position - 1)) != 0 ;
        }
    }
}
