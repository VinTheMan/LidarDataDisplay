using System;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls; // Add this using directive
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Net;
using System.Net.Sockets;

namespace UsbApp
{
    public partial class DebugWindow : Window
    {
        private static DebugWindow _instance;
        private DispatcherTimer _timer;
        private int _packetIndex = 0;
        private Random _rand;

        private DebugWindow()
        {
            InitializeComponent();
            InitializeTimer();
            _rand = new Random();
        }

        public static DebugWindow Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new DebugWindow();
                }
                return _instance;
            }
        }

        private void TestSimulationButton_Click(object sender, RoutedEventArgs e)
        {
            if (_timer.IsEnabled)
            {
                // Stop the timer
                _timer.Stop();
                ((Button)sender).Content = "Start Simulation";
            }
            else
            {
                // Start the timer
                _timer.Start();
                ((Button)sender).Content = "Stop Simulation";
            }
        }

        private void ClearTextButton_Click(object sender, RoutedEventArgs e)
        {
            DataTextBox.Clear();
        }

        private void SaveLogButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLog();
        }

        private void SaveLog()
        {
            // Create a file dialog to save the log
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = "Log"; // Default file name
            dlg.DefaultExt = ".log"; // Default file extension
            dlg.Filter = "Log Files (*.log)|*.log|All Files (*.*)|*.*"; // Filter files by extension

            // Show save file dialog box
            bool? result = dlg.ShowDialog();

            // Process save file dialog box results
            if (result == true)
            {
                // Save the log
                string filename = dlg.FileName;
                File.WriteAllText(filename, DataTextBox.Text);
            }
        }

        private void InitializeTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            // Update the random seed for each frame
            _rand = new Random();

            // Simulate 105 packets per second
            for (int i = 0; i < 105; i++)
            {
                byte[] packet = GenerateMockUdpPacket(_packetIndex, Scenario.Valid);
                Dispatcher.Invoke(() => ((MainWindow)Application.Current.MainWindow).ParseUdpPacket(packet));
                _packetIndex = (_packetIndex + 1) % 105;
            }
        }

        private enum Scenario
        {
            Valid,
            InvalidValidDataSize,
            ChecksumMismatch,
            RandomData
        }

        private byte[] GenerateMockUdpPacket(int psn, Scenario scenario)
        {
            int UdpPacketSize = (((MainWindow)Application.Current.MainWindow).CurrentTab == 1560) ? MainWindow.UdpPacketSize_1560 : MainWindow.UdpPacketSize_520;
            byte[] packet = new byte[UdpPacketSize];

            if (((MainWindow)Application.Current.MainWindow).CurrentTab == 1560)
            {
                // RXAA format
                packet[0] = (byte)(_packetIndex % 4); // UDP number (0 to 3)
                packet[1] = (byte)psn; // PSN (0 to 104)

                // AmbientData
                for (int i = 2; i < UdpPacketSize; i++)
                {
                    packet[i] = (byte)_rand.Next(256);
                }
            }
            else
            {
                // BSAA format
                packet[0] = (byte)psn; // PSN (0 to 104)

                // AmbientData
                for (int i = 1; i < UdpPacketSize; i++)
                {
                    packet[i] = (byte)_rand.Next(256);
                }
            }

            // Modify packet based on scenario
            switch (scenario)
            {
                case Scenario.InvalidValidDataSize:
                    packet[3] = 0x00; // Invalid packet size
                    packet[4] = 0x00;
                    break;
                case Scenario.ChecksumMismatch:
                    // Do not calculate checksum correctly
                    packet[UdpPacketSize - 2] = 0x00;
                    packet[UdpPacketSize - 1] = 0x00;
                    return packet;
                case Scenario.RandomData:
                    // Randomize entire packet
                    _rand.NextBytes(packet);
                    return packet;
            }

            // Checksum
            ushort checksum = ((MainWindow)Application.Current.MainWindow).CalculateChecksum(packet, UdpPacketSize - 2);
            packet[UdpPacketSize - 2] = (byte)(checksum & 0xFF);
            packet[UdpPacketSize - 1] = (byte)((checksum >> 8) & 0xFF);

            return packet;
        } // End of method GenerateMockUdpPacket

        protected override void OnClosed(EventArgs e)
        {
            _instance = null;
            base.OnClosed(e);
        }
    } // End of class DebugWindow
} // End of namespace UsbApp