﻿using System;
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
                byte[] packet = GenerateMockPacket(_packetIndex, Scenario.Valid);
                Dispatcher.Invoke(() => ((MainWindow)Application.Current.MainWindow).ParseMipiPacket(packet));
                _packetIndex = (_packetIndex + 1) % 105;
            }
        }

        private enum Scenario
        {
            Valid,
            InvalidPacketSize,
            ChecksumMismatch,
            RandomData
        }

        private byte[] GenerateMockPacket(int psn, Scenario scenario)
        {
            byte[] packet = new byte[MainWindow.PacketSize];

            // Header
            packet[0] = 0x55;
            packet[1] = 0xAA;

            // PSN
            packet[2] = (byte)psn;

            // PackSize
            packet[3] = 0xC7;
            packet[4] = 0x06;

            // AmbientData
            for (int i = 5; i < 5 + (MainWindow.AmbientDataSize * 3); i++)
            {
                packet[i] = (byte)_rand.Next(256);
            }

            // Modify packet based on scenario
            switch (scenario)
            {
                case Scenario.InvalidPacketSize:
                    packet[3] = 0x00; // Invalid packet size
                    packet[4] = 0x00;
                    break;
                case Scenario.ChecksumMismatch:
                    // Do not calculate checksum correctly
                    packet[MainWindow.PacketSize - 2] = 0x00;
                    packet[MainWindow.PacketSize - 1] = 0x00;
                    return packet;
                case Scenario.RandomData:
                    // Randomize entire packet
                    _rand.NextBytes(packet);
                    return packet;
            }

            // Checksum
            ushort checksum = ((MainWindow)Application.Current.MainWindow).CalculateChecksum(packet, MainWindow.PacketSize - 2);
            packet[MainWindow.PacketSize - 2] = (byte)(checksum & 0xFF);
            packet[MainWindow.PacketSize - 1] = (byte)((checksum >> 8) & 0xFF);

            return packet;
        }

        protected override void OnClosed(EventArgs e)
        {
            _instance = null;
            base.OnClosed(e);
        }
    }
}
