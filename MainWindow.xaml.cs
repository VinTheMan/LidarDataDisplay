using System;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls; // Add this using directive
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace UsbApp
{
    public partial class MainWindow : Window
    {
        private SerialPort _serialPort;
        private WriteableBitmap _bitmap;
        private int _currentLine = 0;
        private byte[] _imageData;
        private DispatcherTimer _timer;
        private int _packetIndex = 0;
        private Random _rand;

        public MainWindow()
        {
            InitializeComponent();
            PopulateSerialPortComboBox();
            InitializeBitmap();
            InitializeTimer();
            _rand = new Random(); // Initialize the random number generator
        }

        private void PopulateSerialPortComboBox()
        {
            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                SerialPortComboBox.Items.Add(port);
            }

            if (SerialPortComboBox.Items.Count > 0)
            {
                SerialPortComboBox.SelectedIndex = 0;
            }
            else
            {
                MessageBox.Show("No USB serial ports found.");
            }
        }

        private bool IsPortAvailable(string portName)
        {
            string[] ports = SerialPort.GetPortNames();
            foreach (string port in ports)
            {
                if (port.Equals(portName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void ReadDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (SerialPortComboBox.SelectedItem == null)
            {
                MessageBox.Show("Please select a serial port.");
                return;
            }

            string selectedPort = SerialPortComboBox.SelectedItem.ToString();
            if (!IsPortAvailable(selectedPort))
            {
                MessageBox.Show($"The selected port {selectedPort} is not available.");
                return;
            }

            _serialPort = new SerialPort(selectedPort, 9600); // Adjust baud rate as needed
            _serialPort.DataReceived += SerialPort_DataReceived;
            _serialPort.Open();
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

        private void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            byte[] buffer = new byte[_serialPort.BytesToRead];
            _serialPort.Read(buffer, 0, buffer.Length);
            Dispatcher.Invoke(() => ParseMipiPacket(buffer));
        }

        private void ParseMipiPacket(byte[] data)
        {
            // Ensure the data length matches the expected packet size
            const int expectedPacketSize = 2 + 1 + 2 + (576 * 3) + 2;
            if (data.Length != expectedPacketSize)
            {
                DataTextBox.AppendText("Invalid packet size.\n");
                return;
            }

            // Extract fields from the data
            ushort header = (ushort)(data[0] | (data[1] << 8));
            byte psn = data[2];
            ushort packSize = (ushort)(data[3] | (data[4] << 8));
            byte[] ambientData = new byte[576 * 3];
            Array.Copy(data, 5, ambientData, 0, 576 * 3);
            ushort checksum = (ushort)(data[5 + (576 * 3)] | (data[6 + (576 * 3)] << 8));

            // Print extracted data to the text box
            DataTextBox.AppendText($"Header: 0x{header:X4}\n");
            DataTextBox.AppendText($"PSN: 0x{psn:X2}\n");
            DataTextBox.AppendText($"Packet Size: 0x{packSize:X4}\n");
            DataTextBox.AppendText($"Checksum: 0x{checksum:X4}\n");

            // Verify the header
            if (header != 0xAA55)
            {
                DataTextBox.AppendText($"Invalid header: 0x{header:X4}\n");
                return;
            }

            // Verify the packet size
            if (packSize != 0x06C5)
            {
                DataTextBox.AppendText($"Invalid packet size field: 0x{packSize:X4}\n");
                return;
            }

            // Calculate and verify the checksum
            ushort calculatedChecksum = CalculateChecksum(data, data.Length - 2);
            if (checksum != calculatedChecksum)
            {
                DataTextBox.AppendText($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}\n");
                return;
            }

            // Store the ambient data
            Array.Copy(ambientData, 0, _imageData, _currentLine * 576 * 3, 576 * 3);
            _currentLine++;

            // If we have received all 105 lines, update the bitmap
            if (_currentLine >= 105)
            {
                UpdateBitmap();
                _currentLine = 0;
            }
        }

        private ushort CalculateChecksum(byte[] data, int length)
        {
            ushort checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        }

        private void InitializeBitmap()
        {
            _bitmap = new WriteableBitmap(105, 576, 96, 96, PixelFormats.Gray8, null);
            ImageCanvas.Background = new ImageBrush(_bitmap);
            _imageData = new byte[105 * 576 * 3];
        }

        private void UpdateBitmap()
        {
            byte[] grayData = new byte[105 * 576];
            for (int i = 0; i < 105 * 576; i++)
            {
                int r = _imageData[i * 3];
                int g = _imageData[i * 3 + 1];
                int b = _imageData[i * 3 + 2];
                grayData[i] = (byte)((r + g + b) / 3);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, 105, 576), grayData, 105, 0);
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
                Dispatcher.Invoke(() => ParseMipiPacket(packet));
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
            const int packetSize = 2 + 1 + 2 + (576 * 3) + 2;
            byte[] packet = new byte[packetSize];

            // Header
            packet[0] = 0x55;
            packet[1] = 0xAA;

            // PSN
            packet[2] = (byte)psn;

            // PackSize
            packet[3] = 0xC5;
            packet[4] = 0x06;

            // AmbientData
            for (int i = 5; i < 5 + (576 * 3); i++)
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
                    packet[packetSize - 2] = 0x00;
                    packet[packetSize - 1] = 0x00;
                    return packet;
                case Scenario.RandomData:
                    // Randomize entire packet
                    _rand.NextBytes(packet);
                    return packet;
            }

            // Checksum
            ushort checksum = CalculateChecksum(packet, packetSize - 2);
            packet[packetSize - 2] = (byte)(checksum & 0xFF);
            packet[packetSize - 1] = (byte)((checksum >> 8) & 0xFF);

            return packet;
        }

        protected override void OnClosed(EventArgs e)
        {
            _serialPort?.Close();
            base.OnClosed(e);
        }
    }
}

