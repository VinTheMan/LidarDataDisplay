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
using System.Text;
using System.Threading.Tasks;

// Note :
// image refresh rate
// packSize 0x06C7 (1735 bytes)

namespace UsbApp
{
    public partial class MainWindow : Window
    {
        // data speed : 64 bytes per 0.01s (sleep(10000)) should be successfully received and displayed
        public const int AmbientDataSize = 1560; // Size of the ambient data

        // each udp packet has 1 byte udp_num, 4 udp packets form a vertical line on my bitmap.
        public const int PacketSize = 4 + 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total packet size (should be 4691)

        public const int TotalLines = 105;

        public const int BufferSize = PacketSize * TotalLines * 2; // Increase buffer size for potential board info
        public byte[] _buffer = new byte[BufferSize];
        public int _bufferIndex = 0;

        private UdpClient udpClient;
        private bool isListening;

        public SerialPort _serialPort;
        public WriteableBitmap _bitmap;
        public int _currentLine = 0;
        public byte[] _imageData;
        public DispatcherTimer _timer;
        public int _packetIndex = 0;
        public Random _rand;
        public Point clickPosition;
        public byte[] _accumulatedBuffer = new byte[PacketSize];
        public int _accumulatedBufferIndex = 0;
        public byte[][] _receivedPackets = new byte[TotalLines][];
        public bool[] _receivedPacketFlags = new bool[TotalLines];
        public Point? _clickedPoint = null;

        public int _lastPsn = -1;
        public DebugWindow _debugWindow;
        public int CanvasWidth => TotalLines;
        public int CanvasHeight => AmbientDataSize;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            // Populate the serial port combo box
            // PopulateSerialPortComboBox();
            InitializeBitmap();
            InitializeTimer();

            _rand = new Random(); // Initialize the random number generator
            ImageDimensionsTextBlock.Text = $"Image Dimensions: {_bitmap.PixelWidth}x{_bitmap.PixelHeight}";

            // Open the debug window
            DebugWindow.Instance.Show();
        } // MainWindow

        private void MainWindow_Closed(object sender, System.EventArgs e)
        {
            StopListening();
            Application.Current.Shutdown();
        } // MainWindow_Closed

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        } // OnPropertyChanged

        public void PopulateSerialPortComboBox()
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

        public bool IsPortAvailable(string portName)
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

        public void ReadDataButton_Click(object sender, RoutedEventArgs e)
        {
            if (_serialPort != null && _serialPort.IsOpen)
            {
                // Close the serial port and update the button content
                _serialPort.DiscardInBuffer(); // Clear the input buffer
                _serialPort.DiscardOutBuffer(); // Clear the output buffer
                _serialPort.Close();
                ((Button)sender).Content = "Start Reading";
                ResetData();
                ClearBuffer(); // Clear the buffer when stopping reading
            }
            else
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

                _serialPort = new SerialPort(selectedPort, 115200); // Adjust baud rate as needed
                _serialPort.Handshake = Handshake.None; // Turn off XON/XOFF control
                _serialPort.DataReceived += SerialPort_DataReceived;
                _serialPort.Open();
                _serialPort.DiscardInBuffer(); // Clear the input buffer
                _serialPort.DiscardOutBuffer(); // Clear the output buffer
                ((Button)sender).Content = "Stop Reading";
            }
        } // ReadDataButton_Click

        public void TestSimulationButton_Click(object sender, RoutedEventArgs e)
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

        public void SaveImageButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImage();
        } // SaveImageButton_Click

        public void SaveImage()
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

        private async void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = (Button)sender;

            if (isListening)
            {
                StopListening();
                button.Content = "Start Listening";
            }
            else
            {
                isListening = true;
                udpClient = new UdpClient(7000); // Use the appropriate port number
                UdpDataTextBlock.Text = "Listening for UDP packets...";
                button.Content = "Stop Listening";

                await Task.Run(() => ListenForUdpPackets());
            }
        } // StartListeningButton_Click

        private void StopListening()
        {
            if (udpClient != null)
                udpClient.Close();
            isListening = false;
            UdpDataTextBlock.Text = "Stopped listening.";
        } // StopListening

        private async void ListenForUdpPackets()
        {
            try
            {
                while (isListening)
                {
                    var result = await udpClient.ReceiveAsync();
                    byte[] receivedData = result.Buffer;
                    ParseUdpPacket(receivedData);
                }
            }
            catch (ObjectDisposedException)
            {
                // Handle the case when the UDP client is closed
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = $"Error: {ex.Message}");
            }
        } // ListenForUdpPackets

        private void ParseUdpPacket(byte[] data)
        {
            if (data.Length != 1200)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = "Invalid UDP packet size.");
                return;
            }

            byte udpNumber = data[0]; // 0 ~ 3
            ushort header = (ushort)(data[1] | (data[2] << 8)); // 2 bytes
            byte psn = data[3]; // 0 ~ 104
            ushort packSize = (ushort)(data[4] | (data[5] << 8)); // 2 bytes
            byte[] ambientData = new byte[AmbientDataSize * 3 / 4]; // 1170 bytes
            Array.Copy(data, 6, ambientData, 0, AmbientDataSize * 3 / 4);
            ushort checksum = (ushort)(data[6 + (AmbientDataSize * 3 / 4)] | (data[7 + (AmbientDataSize * 3 / 4)] << 8)); // 2 bytes

            // Verify the packet size
            if (packSize != 1200)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = "Invalid packet size field.");
                return;
            }

            // Calculate and verify the checksum
            ushort calculatedChecksum = CalculateChecksum(data, data.Length - 2);
            if (checksum != calculatedChecksum)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = "Checksum mismatch.");
                return;
            }

            // Combine the data from 4 UDP packets
            if (_receivedPackets[psn] == null)
            {
                _receivedPackets[psn] = new byte[AmbientDataSize * 3];
            }

            Array.Copy(ambientData, 0, _receivedPackets[psn], udpNumber * (AmbientDataSize * 3 / 4), AmbientDataSize * 3 / 4);
            _receivedPacketFlags[psn] = true;

            // Check if all 4 UDP packets for this PSN have been received
            bool allPacketsReceived = true;
            for (int i = 0; i < 4; i++)
            {
                if (_receivedPackets[psn][i * (AmbientDataSize * 3 / 4)] == 0)
                {
                    allPacketsReceived = false;
                    break;
                }
            }

            if (allPacketsReceived)
            {
                Dispatcher.Invoke(() => UpdateBitmap());
            }
        } // ParseUdpPacket


        public void SerialPort_DataReceived(object sender, SerialDataReceivedEventArgs e)
        {
            int bytesToRead = _serialPort.BytesToRead;
            byte[] buffer = new byte[bytesToRead];
            _serialPort.Read(buffer, 0, bytesToRead);

            // Copy the received data into the buffer
            Array.Copy(buffer, 0, _buffer, _bufferIndex, bytesToRead);
            _bufferIndex += bytesToRead;

            // Try to parse the buffer for valid packets
            ParseBuffer();
        } // SerialPort_DataReceived

        public void ParseBuffer()
        {
            int startIndex = 0;

            while (startIndex <= _bufferIndex - PacketSize)
            {
                // Check for the start of a packet
                if (_buffer[startIndex] == 0x55 && _buffer[startIndex + 1] == 0xAA)
                {
                    byte[] packet = new byte[PacketSize];
                    Array.Copy(_buffer, startIndex, packet, 0, PacketSize);

                    // Verify the packet size
                    ushort packSize = (ushort)(packet[3] | (packet[4] << 8));
                    if (packSize == PacketSize)
                    {
                        // Process the complete packet
                        Dispatcher.Invoke(() => ParseMipiPacket(packet));

                        // Move the start index to the next potential packet
                        startIndex += PacketSize;
                    }
                    else
                    {
                        // Invalid packet size, move to the next byte
                        startIndex++;
                    }
                }
                else
                {
                    // Not the start of a packet, move to the next byte
                    startIndex++;
                }
            }

            // Print any data before the first valid header byte as a string
            if (startIndex > 0)
            {
                string initialData = System.Text.Encoding.ASCII.GetString(_buffer, 0, startIndex);
                //Dispatcher.Invoke(() => DataTextBox.AppendText($"{initialData}\n"));
            } // if

            // Print the remaining data after the last valid packet as a string
            if (_bufferIndex > startIndex)
            {
                string remainingData = System.Text.Encoding.ASCII.GetString(_buffer, startIndex, _bufferIndex - startIndex);
                //Dispatcher.Invoke(() => DataTextBox.AppendText($"{remainingData}\n"));
            } // if

            // Remove the processed data from the buffer
            int remainingBytes = _bufferIndex - startIndex;
            Array.Copy(_buffer, startIndex, _buffer, 0, remainingBytes);
            _bufferIndex = remainingBytes;
        } // ParseBuffer

        public void ParseMipiPacket(byte[] data)
        {
            // Ensure the data length matches the expected packet size
            if (data.Length != PacketSize)
            {
                DebugWindow.Instance.DataTextBox.AppendText("Invalid packet size.\n");
                // print the data for debug
                DebugWindow.Instance.DataTextBox.AppendText($"Data: {BitConverter.ToString(data)}\n");
                return;
            }

            // Extract fields from the data
            byte udp_number = data[0]; // 0 ~ 3 // 1 byte
            ushort header = (ushort)(data[1] | (data[2] << 8)); // 2 bytes
            byte psn = data[3]; // 0 ~ 104 // 1 byte
            ushort packSize = (ushort)(data[4] | (data[5] << 8)); // 2 bytes
            byte[] ambientData = new byte[AmbientDataSize * 3]; // 4680 bytes
            Array.Copy(data, 6, ambientData, 0, AmbientDataSize * 3);
            ushort checksum = (ushort)(data[6 + (AmbientDataSize * 3)] | (data[7 + (AmbientDataSize * 3)] << 8)); // 2 bytes

            bool hasInvalidData = false;
            // Print extracted data to the text box
            DebugWindow.Instance.DataTextBox.AppendText($"----------------------------------------------------------------------\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Header: 0x{header:X4}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- PSN: 0x{psn:X2}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Packet Size: 0x{packSize:X4}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Checksum: 0x{checksum:X4}\n");

            // Verify the packet size
            if (packSize != PacketSize)
            {
                DebugWindow.Instance.DataTextBox.AppendText($"Invalid packet size field: 0x{packSize:X4}\n");
                hasInvalidData = true;
            } // if

            // Calculate and verify the checksum
            ushort calculatedChecksum = CalculateChecksum(data, data.Length - 2);
            if (checksum != calculatedChecksum)
            {
                DebugWindow.Instance.DataTextBox.AppendText($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}\n");
                hasInvalidData = true;
            } // if

            DebugWindow.Instance.DataTextBox.AppendText($"----------------------------------------------------------------------\n");

            // Check if the PSN value decreases, indicating a new frame
            if (_lastPsn != -1 && psn < _lastPsn)
            {
                ResetData();
            } // if

            // Update the last received PSN value
            _lastPsn = psn;

            // Store the packet data regardless of checksum verification
            if (psn < TotalLines)
            {
                _receivedPackets[psn] = ambientData;
                _receivedPacketFlags[psn] = true;
            } // if

            UpdateBitmap();
        } // ParseMipiPacket

        public ushort CalculateChecksum(byte[] data, int length)
        {
            ushort checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        } // CalculateChecksum

        public DrawingVisual CreateAxesVisual()
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                int width = _bitmap.PixelWidth;
                int height = _bitmap.PixelHeight;

                // Draw the horizontal axis
                context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, height / 2), new Point(width, height / 2));

                // Draw the vertical axis
                context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, 0), new Point(width / 2, height));

                // Draw the blue dot at the clicked point
                if (_clickedPoint.HasValue)
                {
                    context.DrawEllipse(Brushes.Blue, null, _clickedPoint.Value, 3, 3);
                }
            }
            return visual;
        } // CreateAxesVisual

        public void DrawAxes()
        {
            DrawingVisual axesVisual = CreateAxesVisual();
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(_bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(axesVisual);

            // Combine the axes with the existing bitmap
            DrawingVisual combinedVisual = new DrawingVisual();
            using (DrawingContext context = combinedVisual.RenderOpen())
            {
                context.DrawImage(_bitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
                context.DrawImage(renderBitmap, new Rect(0, 0, _bitmap.PixelWidth, _bitmap.PixelHeight));
            }

            RenderTargetBitmap combinedBitmap = new RenderTargetBitmap(_bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            combinedBitmap.Render(combinedVisual);

            // Update the ImageCanvas with the combined image
            ImageCanvas.Background = new ImageBrush(combinedBitmap);
        }
        // DrawAxes

        private void ImagePanel_Loaded(object sender, RoutedEventArgs e)
        {
            ResizeBitmapToFitStackPanel();
        }
        public void InitializeBitmap()
        {
            _bitmap = new WriteableBitmap(TotalLines, AmbientDataSize, 96, 96, PixelFormats.Gray8, null);
            ImageCanvas.Background = new ImageBrush(_bitmap);
            _imageData = new byte[TotalLines * AmbientDataSize * 3];
            DrawAxes(); // Draw the axes on the image
        } // InitializeBitmap

        public void ResizeBitmapToFitStackPanel()
        {
            double panelHeight = ImagePanel.ActualHeight;
            double panelWidth = ImagePanel.ActualWidth;
            double imageHeight = _bitmap.PixelHeight;
            double imageWidth = _bitmap.PixelWidth;

            if (panelHeight > 0 && imageHeight > 0)
            {
                double scale = (panelHeight / imageHeight) * 0.95;
                ImageScaleTransform.ScaleX = scale;
                ImageScaleTransform.ScaleY = scale;

                // Center the image horizontally and vertically
                double scaledImageWidth = imageWidth * scale;
                double scaledImageHeight = imageHeight * scale;
                double offsetX = (panelWidth - scaledImageWidth)/2;
                double offsetY = (panelHeight - scaledImageHeight) / 2;

                ImageCanvas.RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection {
                        new ScaleTransform(scale, scale),
                        new TranslateTransform(offsetX, offsetY)
                    }
                };
            }
        } // ResizeBitmapToFitStackPanel

        public void UpdateBitmap()
        {
            byte[] grayData = new byte[TotalLines * AmbientDataSize];
            for (int i = 0; i < TotalLines; i++)
            {
                if (_receivedPacketFlags[i])
                {
                    for (int j = 0; j < AmbientDataSize; j++)
                    {
                        int r = _receivedPackets[i][j * 3];
                        int g = _receivedPackets[i][j * 3 + 1];
                        int b = _receivedPackets[i][j * 3 + 2];
                        grayData[j * TotalLines + i] = (byte)((r + g + b) / 3);
                    }
                }
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, TotalLines, AmbientDataSize), grayData, TotalLines, 0);
            DrawAxes(); // Redraw the axes after updating the bitmap
        } // UpdateBitmap


        public void InitializeTimer()
        {
            _timer = new DispatcherTimer();
            _timer.Interval = TimeSpan.FromSeconds(1);
            _timer.Tick += Timer_Tick;
        }

        public void Timer_Tick(object sender, EventArgs e)
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

        public enum Scenario
        {
            Valid,
            InvalidPacketSize,
            ChecksumMismatch,
            RandomData
        }

        public byte[] GenerateMockPacket(int psn, Scenario scenario)
        {
            byte[] packet = new byte[PacketSize];

            // Header
            packet[0] = 0x55;
            packet[1] = 0xAA;

            // PSN
            packet[2] = (byte)psn;

            // PackSize
            packet[3] = 0xC7;
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
        } // OnClosed

        public void ClearBuffer()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _bufferIndex = 0;
        } // ClearBuffer
        public void ResetData()
        {
            _lastPsn = -1; // Reset the last PSN value
            _currentLine = 0;
            _accumulatedBufferIndex = 0;
            Array.Clear(_accumulatedBuffer, 0, _accumulatedBuffer.Length);
            Array.Clear(_receivedPackets, 0, _receivedPackets.Length);
            Array.Clear(_receivedPacketFlags, 0, _receivedPacketFlags.Length);
            InitializeBitmap(); // Reinitialize the bitmap
        } // ResetData
    } // class MainWindow
} // namespace UsbApp
