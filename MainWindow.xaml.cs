﻿using System;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls; // Add this using directive
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// TO-DO :
// color
// image refresh rate
// ask Jay baud rate

namespace UsbApp
{
    public partial class MainWindow : Window
    {
        private const int AmbientDataSize = 576; // Size of the ambient data
        private const int PacketSize = 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total packet size

        private SerialPort _serialPort;
        private WriteableBitmap _bitmap;
        private int _currentLine = 0;
        private byte[] _imageData;
        private DispatcherTimer _timer;
        private int _packetIndex = 0;
        private Random _rand;
        private Point _lastMousePosition;
        private bool _isDragging;
        private double _translateX = 0;
        private double _translateY = 0;
        private bool _isTranslationUpdated = false;

        public MainWindow()
        {
            InitializeComponent();
            PopulateSerialPortComboBox();
            InitializeBitmap();
            InitializeTimer();
            _rand = new Random(); // Initialize the random number generator

            CompositionTarget.Rendering += CompositionTarget_Rendering;
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

        private void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImage();
        }

        private void SaveImage()
        {
            // Create a file dialog to save the image
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = "Image"; // Default file name
            dlg.DefaultExt = ".png"; // Default file extension
            dlg.Filter = "PNG Files (*.png)|*.png|All Files (*.*)|*.*"; // Filter files by extension

            // Show save file dialog box
            bool? result = dlg.ShowDialog();

            // Process save file dialog box results
            if (result == true)
            {
                // Save the image
                string filename = dlg.FileName;
                using (FileStream stream = new FileStream(filename, FileMode.Create))
                {
                    PngBitmapEncoder encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(_bitmap));
                    encoder.Save(stream);
                }
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
            if (data.Length != PacketSize)
            {
                DataTextBox.AppendText("Invalid packet size.\n");
                return;
            }

            // Extract fields from the data
            ushort header = (ushort)(data[0] | (data[1] << 8));
            byte psn = data[2];
            ushort packSize = (ushort)(data[3] | (data[4] << 8));
            byte[] ambientData = new byte[AmbientDataSize * 3];
            Array.Copy(data, 5, ambientData, 0, AmbientDataSize * 3);
            ushort checksum = (ushort)(data[5 + (AmbientDataSize * 3)] | (data[6 + (AmbientDataSize * 3)] << 8));

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
            Array.Copy(ambientData, 0, _imageData, _currentLine * AmbientDataSize * 3, AmbientDataSize * 3);
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
            _bitmap = new WriteableBitmap(105, AmbientDataSize, 96, 96, PixelFormats.Gray8, null);
            ImageCanvas.Background = new ImageBrush(_bitmap);
            _imageData = new byte[105 * AmbientDataSize * 3];
        }

        private void UpdateBitmap()
        {
            byte[] grayData = new byte[105 * AmbientDataSize];
            for (int i = 0; i < 105 * AmbientDataSize; i++)
            {
                int r = _imageData[i * 3];
                int g = _imageData[i * 3 + 1];
                int b = _imageData[i * 3 + 2];
                grayData[i] = (byte)((r + g + b) / 3);
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, 105, AmbientDataSize), grayData, 105, 0);
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
            byte[] packet = new byte[PacketSize];

            // Header
            packet[0] = 0x55;
            packet[1] = 0xAA;

            // PSN
            packet[2] = (byte)psn;

            // PackSize
            packet[3] = 0xC5;
            packet[4] = 0x06;

            // AmbientData
            for (int i = 5; i < 5 + (AmbientDataSize * 3); i++)
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
                    packet[PacketSize - 2] = 0x00;
                    packet[PacketSize - 1] = 0x00;
                    return packet;
                case Scenario.RandomData:
                    // Randomize entire packet
                    _rand.NextBytes(packet);
                    return packet;
            }

            // Checksum
            ushort checksum = CalculateChecksum(packet, PacketSize - 2);
            packet[PacketSize - 2] = (byte)(checksum & 0xFF);
            packet[PacketSize - 1] = (byte)((checksum >> 8) & 0xFF);

            return packet;
        }

        protected override void OnClosed(EventArgs e)
        {
            _serialPort?.Close();
            base.OnClosed(e);
        }

        private void ImageCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastMousePosition = e.GetPosition(ImageCanvas);
            _isDragging = true;
            ImageCanvas.CaptureMouse();
        }

        private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPosition = e.GetPosition(ImageCanvas);
                double offsetX = currentPosition.X - _lastMousePosition.X;
                double offsetY = currentPosition.Y - _lastMousePosition.Y;

                _translateX += offsetX;
                _translateY += offsetY;
                _isTranslationUpdated = true;

                _lastMousePosition = currentPosition;
            }
        }

        private void ImageCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _isDragging = false;
            ImageCanvas.ReleaseMouseCapture();
        }

        private void ImageCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoomLevel = ImageScaleTransform.ScaleX + (e.Delta > 0 ? 0.1 : -0.1);
            zoomLevel = Math.Max(1, zoomLevel); // Ensure the zoom level is at least 1
            ImageScaleTransform.ScaleX = zoomLevel;
            ImageScaleTransform.ScaleY = zoomLevel;
        }

        private void CompositionTarget_Rendering(object sender, EventArgs e)
        {
            if (_isTranslationUpdated)
            {
                ImageTranslateTransform.X = _translateX;
                ImageTranslateTransform.Y = _translateY;
                _isTranslationUpdated = false;
            }
        }
    }
}
