using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SharpPcap;
using SharpPcap.WinPcap;
using System.Diagnostics;
using PcapDotNet.Core;
using System.Threading;
using System.Data;
using System.ComponentModel;
using System.Net;

namespace WpfApplication1
{
    /// <summary>
    /// Interaction logic for RouterWindow.xaml
    /// </summary>
    /// 
    public class Arp
    {
        public string Mac { get; set; }
        public string ipAddress { get; set; }
        public int Timer { get; set; }
    }

    public class RoutingTable
    {
        public string Network { get; set; }
        public string NextHop { get; set; }
        public int OutputPort { get; set; }
        public string Type { get; set; }
        public int Metric { get; set; }
        public int Mask { get; set; }
    }

    public partial class RouterWindow : Window
    {

        Port port1 = new Port();
        Port port2 = new Port();

        SwRouter router;

        IPAddress arpRequestedIP;
      
        public RouterWindow(WinPcapDevice dev1, WinPcapDevice dev2)
        {
            InitializeComponent();

            DataGridTextColumn textColumn10 = new DataGridTextColumn();
            textColumn10.Header = "Type";
            textColumn10.Binding = new Binding("Type");
            textColumn10.Width = 50;
            dataGrid1.Columns.Add(textColumn10);

            DataGridTextColumn textColumn11 = new DataGridTextColumn();
            textColumn11.Header = "Network";
            textColumn11.Binding = new Binding("Network");
            textColumn11.Width = 160;
            dataGrid1.Columns.Add(textColumn11);

            DataGridTextColumn textColumn12 = new DataGridTextColumn();
            textColumn12.Header = "Mask";
            textColumn12.Binding = new Binding("Mask");
            textColumn12.Width = 90;
            dataGrid1.Columns.Add(textColumn12);

            DataGridTextColumn textColumn13 = new DataGridTextColumn();
            textColumn13.Header = "OutputPort";
            textColumn13.Binding = new Binding("OutputPort");
            textColumn13.Width = 80;
            dataGrid1.Columns.Add(textColumn13);

            DataGridTextColumn textColumn14 = new DataGridTextColumn();
            textColumn14.Header = "NextHop";
            textColumn14.Binding = new Binding("NextHop");
            textColumn14.Width = 160;
            dataGrid1.Columns.Add(textColumn14);

            DataGridTextColumn textColumn15 = new DataGridTextColumn();
            textColumn15.Header = "Metric";
            textColumn15.Binding = new Binding("Metric");
            textColumn15.Width = 60;
            dataGrid1.Columns.Add(textColumn15);


            DataGridTextColumn textColumn1 = new DataGridTextColumn();
            textColumn1.Header = "Mac";
            textColumn1.Binding = new Binding("Mac");
            textColumn1.Width = 110;
            dataGrid.Columns.Add(textColumn1);

            DataGridTextColumn textColumn2 = new DataGridTextColumn();
            textColumn2.Header = "ipAddress";
            textColumn2.Binding = new Binding("ipAddress");
            textColumn2.Width = 110;
            dataGrid.Columns.Add(textColumn2);

            DataGridTextColumn textColumn = new DataGridTextColumn();
            textColumn.Header = "Timer";
            textColumn.Binding = new Binding("Timer");
            textColumn.Width = 100;
            dataGrid.Columns.Add(textColumn);

            ipAddress1TextBox.Text = "0.0.0.0";
            ipAddress2TextBox.Text = "0.0.0.0";
            mask1TextBox.Text = "255.255.255.255";
            mask2TextBox.Text = "255.255.255.255";

            textBoxTimer.Text = "10";

            this.port1.device = dev1;
            this.port2.device = dev2;

            comboBoxStaticRoute.Items.Add("Any");
            comboBoxStaticRoute.Items.Add("Port 1");
            comboBoxStaticRoute.Items.Add("Port 2");

            textBoxMaskStatic.Text = "";
            textBoxNetworkStatic.Text = "";
            textBoxNextHopStatic.Text = "";
            textBoxArpRequest.Text = "";
            textBoxOSPFNetwork.Text = "";
            textBoxOSPFMask.Text = "";
            textBoxRouterID.Text = "";


            Thread t1 = new Thread(() => updateArpTable());
            t1.Start();
            Thread t2 = new Thread(() => updateRoutingTable());
            t2.Start();
            router = new SwRouter(this.port1, this.port2);
            Thread t3 = new Thread(() => updateNeighbor());
            t3.Start();
        }

        private void button_Click(object sender, RoutedEventArgs e)
        {
            this.Hide();
            this.Close();
        }

        private void buttonSetPort1_Click(object sender, RoutedEventArgs e)
        {
            if (!ipAddress1TextBox.Text.Equals("") && !mask1TextBox.Text.Equals(""))
            {
                port1.ipAddress = IPAddress.Parse(ipAddress1TextBox.Text);
                port1.mask = IPAddress.Parse(mask1TextBox.Text);
                port1.idPort = 1;

                router.updateIpAndMask(port1.ipAddress, port1.mask, 1);
                router.setDirectRoute(port1);
            }

        }

        private void buttonSetPort2_Click(object sender, RoutedEventArgs e)
        {
            if (!ipAddress2TextBox.Text.Equals("") && !mask2TextBox.Text.Equals(""))
            {
                port2.ipAddress = IPAddress.Parse(ipAddress2TextBox.Text);
                port2.mask = IPAddress.Parse(mask2TextBox.Text);
                port2.idPort = 2;

                router.updateIpAndMask(port2.ipAddress, port2.mask, 2);
                router.setDirectRoute(port2);
            }
        }

        private void buttonAddStatic_Click(object sender, RoutedEventArgs e)
        {
            Route staticRoute = new Route();

            staticRoute.id = router.id;
            staticRoute.metric = 1;
            staticRoute.network = IPAddress.Parse(textBoxNetworkStatic.Text);
            staticRoute.cidr = router.convertMaskToCidr(IPAddress.Parse(textBoxMaskStatic.Text));
            staticRoute.mask = IPAddress.Parse(textBoxMaskStatic.Text);
            if (!textBoxNextHopStatic.Text.Equals("")) {
                staticRoute.nextHop = IPAddress.Parse(textBoxNextHopStatic.Text);
            }
            else
                staticRoute.nextHop = null;
            if (comboBoxStaticRoute.Text.Equals("Any"))
            {
                staticRoute.outputPort = null;
            }
            else if (comboBoxStaticRoute.Text.Equals("Port 1"))
            {
                staticRoute.outputPort = port1;
            }
            else if (comboBoxStaticRoute.Text.Equals("Port 2"))
            {
                staticRoute.outputPort = port2;
            }
            staticRoute.type = "S";

            if (!(router.routeIsInTable(staticRoute)))
            {
                router.routingTable.AddLast(staticRoute);

                comboBoxDeleteStatic.Items.Add(router.id.ToString());

                router.id++;
            }

        }

        private void buttonSendArp_Click(object sender, RoutedEventArgs e)
        {
            arpRequestedIP = IPAddress.Parse(textBoxArpRequest.Text);

            router.sendArpRequest(arpRequestedIP);
        }

        private void updateArpTable()
        {
            while (true)
            {
                this.Dispatcher.Invoke(() =>
                {

                    List<ArpTable> list = new List<ArpTable>();
                    list = router.sendTable();

                    dataGrid.Items.Clear();
                    foreach (var i in list)
                    {
                       // Console.WriteLine(i.MacAddress + " Ahoj");
                        dataGrid.Items.Add(new Arp() { Mac = i.MacAddress, ipAddress = i.ipAddress.ToString(), Timer = i.timer });
                    }

                });

                System.Threading.Thread.Sleep(1000);
            }
        }

        private void buttonClearArp_Click(object sender, RoutedEventArgs e)
        {
            this.router.deleteTable();
        }

        private void buttonSetTime_Click(object sender, RoutedEventArgs e)
        {
            this.router.setTimer(Int32.Parse(textBoxTimer.Text));
            this.router.updateTable();
        }

        private void updateRoutingTable()
        {
            while (true)
            {
                this.Dispatcher.Invoke(() =>
                {

                    List<Route> list = new List<Route>();
                    list = router.sendTableR();

                    dataGrid1.Items.Clear();
                    foreach (var i in list)
                    {
                        if (i.nextHop == null)
                            dataGrid1.Items.Add(new RoutingTable()
                            {
                                Type = i.type,
                                Mask = i.cidr,
                                Metric = i.metric
                            ,
                                Network = i.network.ToString(),
                                NextHop = "-",
                                OutputPort = i.outputPort.idPort
                            });
                        else if (i.outputPort == null) {
                            dataGrid1.Items.Add(new RoutingTable()
                            {
                                Type = i.type,
                                Mask = i.cidr,
                                Metric = i.metric,
                                Network = i.network.ToString(),
                                NextHop = i.nextHop.ToString(),
                                OutputPort = 0
                            });
                        }
                        else
                            dataGrid1.Items.Add(new RoutingTable()
                            {
                                Type = i.type,
                                Mask = i.cidr,
                                Metric = i.metric,
                                Network = i.network.ToString(),
                                NextHop = i.nextHop.ToString(),
                                OutputPort = i.outputPort.idPort
                            });

                    }

                });

                System.Threading.Thread.Sleep(2000);
            }
        }

        private void button_Click_deleteStatic(object sender, RoutedEventArgs e)
        {
            Console.WriteLine("Delete rule nb. " + comboBoxDeleteStatic.SelectedItem.ToString());

            int idDelete = Int32.Parse(comboBoxDeleteStatic.SelectedItem.ToString());
            Route j = null;
            foreach (var i in this.router.routingTable)
            {

                // Console.WriteLine("som vo foreach " + i.ID);
                if (i.id == idDelete)
                {
                    // Console.WriteLine("Mam deletnut list");
                    j = i;
                }
            }
            dataGrid1.Items.Clear();
            comboBoxDeleteStatic.Items.Clear();
            lock (router.locker)
            {
                if(j != null)
                    this.router.routingTable.Remove(j);

                foreach (Route i in router.routingTable) {
                    if (i.type.Equals("S"))
                        comboBoxDeleteStatic.Items.Add(i.id.ToString());
                }
            }
        }

        private void buttonOSPF_Click(object sender, RoutedEventArgs e)
        {

            router.turnOnOspf(IPAddress.Parse(textBoxRouterID.Text));
//            router.ospfProces.routerID = IPAddress.Parse(textBoxRouterID.Text);
            router.ospfProces.generateLSA(port1);
            router.ospfProces.generateLSA(port2);
            label14.Content = "OSPF is running";
            Thread t = new Thread(() => updateDatabase());
            t.Start();
        }

        private void buttonAddOSPFNetwork_Click(object sender, RoutedEventArgs e)
        {
            if (router.ospfProces != null) {

                OspfNetwork net = new OspfNetwork();

                net.network = IPAddress.Parse(textBoxOSPFNetwork.Text);
                net.wildMask = IPAddress.Parse(textBoxOSPFMask.Text);

                if (router.ospfProces.ospfNetList != null)
                {
                    foreach (var i in router.ospfProces.ospfNetList) {
                        if (i.wildMask.Equals(net.wildMask) && i.network.Equals(net.network))
                            return;
                    }

                    router.ospfProces.ospfNetList.AddLast(net);
                    listBoxOSPFNetworks.Items.Add(" " + net.network + "    " + net.wildMask);
                }
                

            }
        }

        private void buttonDeleteOSPFNetwork_Click(object sender, RoutedEventArgs e)
        {
            while (listBoxOSPFNetworks.SelectedItems.Count > 0) {

                OspfNetwork j = null;
                foreach (var i in router.ospfProces.ospfNetList) {
                    if (listBoxOSPFNetworks.SelectedItems[0].ToString().Contains(i.network.ToString()) && listBoxOSPFNetworks.SelectedItems[0].ToString().Contains(i.wildMask.ToString()))
                        j = i;
                }

                router.ospfProces.ospfNetList.Remove(j);

                listBoxOSPFNetworks.Items.Remove(listBoxOSPFNetworks.SelectedItems[0]);
            }
        }

        private void updateNeighbor()
        {
            List<Neighbor> neiToDelete = new List<Neighbor>();
            while (router.ospfProces == null) {
                
            }
            while (true)
            {
         
                    listBoxNeighbor.Dispatcher.Invoke(() =>
                    {
                        listBoxNeighbor.Items.Clear();
                        
                        
                    });

                foreach (Neighbor n in router.ospfProces.neighborList)
                {
                    n.deadInterval--;

                    if (n.deadInterval > 0 && n.state != 0)
                        listBoxNeighbor.Dispatcher.Invoke(() =>
                        {
                            listBoxNeighbor.Items.Add(n.neighbor.ToString() + "\t" + n.deadInterval + "\t" + n.state + "\t" + n.type);

                        });
                    
                    else
                        neiToDelete.Add(n);
                }

                foreach (Neighbor n in neiToDelete)
                {
                    Console.WriteLine("Deleting neighbors");
                    router.ospfProces.neighborList.Remove(n);
                    if (n.state > 1)
                        if (n.port.idPort == 1)
                        {
                            router.ospfProces.chooseDRBDR(n.port, router.ospfProces.port1_Dr_Bdr);
                            router.ospfProces.neighbor1.Remove(n.neighbor);
                            router.ospfProces.neighborList.Remove(n);
                        }
                        else
                        {
                            router.ospfProces.chooseDRBDR(n.port, router.ospfProces.port2_Dr_Bdr);
                            router.ospfProces.neighbor2.Remove(n.neighbor);
                            router.ospfProces.neighborList.Remove(n);
                        }

                }
                foreach (Neighbor n in neiToDelete) {
                    if(n.state == 6)
                    {
                        router.ospfProces.generateLSA(n.port);
                        if (n.port.ipAddress.Equals(router.ospfProces.port1_Dr_Bdr.Item1))
                        {
                            router.ospfProces.networkLSAgenerator(n.port, router.ospfProces.port1_Dr_Bdr.Item1);
                        }
                        else if (n.port.ipAddress.Equals(router.ospfProces.port2_Dr_Bdr.Item1))
                        {
                            router.ospfProces.networkLSAgenerator(n.port, router.ospfProces.port2_Dr_Bdr.Item1);
                        }
                    }
                }
                neiToDelete.Clear();

                Thread.Sleep(1000);
            }
        }

        private void button_Click_1(object sender, RoutedEventArgs e)
        {
            router.ospfProces.routerID = IPAddress.Parse(textBoxRouterID.Text);
            Console.WriteLine(router.ospfProces.routerID.ToString());
        }

        private void updateDatabase()
        {
            while (true)
            {
                    listBoxOSPFNetworks.Dispatcher.Invoke(() =>
                    {
                        listBoxOSPFNetworks.Items.Clear();
                    });

                lock (router.ospfProces.locker)
                {
                    foreach (DatabaseCopy c in router.ospfProces.database_of_lsa)
                    {

                        listBoxNeighbor.Dispatcher.Invoke(() =>
                        {
                            listBoxOSPFNetworks.Items.Add(c.lsa.LSAge + "\t" + c.lsa.LinkStateID + "\t" + c.lsa.AdvertisingRouter + "\t" + c.lsa.LSType + "\t" + c.lsa.LSSequenceNumber.ToString("X"));

                        });
                    }
                }

                Thread.Sleep(1000);

            }
        }
    }
}
