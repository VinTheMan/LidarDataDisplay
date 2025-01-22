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
using System.Windows.Documents;
using System.Collections.Generic;

namespace UsbApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // UART data speed : 64 bytes per 0.01s (sleep(10000)) should be successfully received and displayed
        public const int AmbientDataSize = 1560; // Size of the ambient data
        public const int UdpPacketSize = 1200; // Size of each UDP packet
        // each udp packet has 1 byte udp_num, 4 udp packets form a vertical line on my bitmap.
        public const int ValidDataSize = 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total valid bytes (should be 4687 without udp numbers(1 byte per UDP packet so 4 bytes in total))
        public const int ValidDataSize_uart = 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total valid bytes coming from UART (should be 4687)

        public const int TotalPacketSize = 4800; // 0x12C0 (4800 bytes)(4 UDP packet, each is 1200 bytes, but the valid data would be the first 4691 bytes.)
        public const int TotalLines = 105;

        public const int BufferSize = TotalPacketSize * TotalLines * 2; // Increase buffer size for potential board info
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

        public Point clickPosition;
        public byte[] _accumulatedBuffer = new byte[ValidDataSize];
        public int _accumulatedBufferIndex = 0;
        public byte[][] _receivedPackets = new byte[TotalLines][];
        public bool[] _receivedPacketFlags = new bool[TotalLines];
        public Point? _clickedPoint = null;

        public int _lastPsn = -1;
        public DebugWindow _debugWindow;
        public int CanvasWidth => TotalLines;
        public int CanvasHeight => AmbientDataSize;

        private bool _isCheckboxChecked;
        public bool IsCheckboxChecked
        {
            get => _isCheckboxChecked;
            set
            {
                if (_isCheckboxChecked != value)
                {
                    _isCheckboxChecked = value;
                    OnPropertyChanged();
                } // if
            } // set
        } // IsCheckboxChecked

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            // Populate the serial port combo box
            // PopulateSerialPortComboBox();
            InitializeBitmap();
            ImageDimensionsTextBlock.Text = $"Image Dimensions: {_bitmap.PixelWidth}x{_bitmap.PixelHeight}";

            // Open the debug window
            DebugWindow.Instance.Show();
            this.SizeChanged += MainWindow_SizeChanged;
        } // MainWindow

        private void MainWindow_Closed(object sender, System.EventArgs e)
        {
            StopListening();
            Application.Current.Shutdown();
        } // MainWindow_Closed

        private void MainWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            ResizeBitmapToFitDockPanel();
        } // MainWindow_SizeChanged

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

        private Dictionary<int, byte[][]> _receivedPacketsDict = new Dictionary<int, byte[][]>();
        private Dictionary<int, int> _receivedPacketFlagsDict = new Dictionary<int, int>();
        private readonly object _lock = new object();

        public void ParseUdpPacket(byte[] data)
        {
            if (data.Length != UdpPacketSize)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = "Invalid UDP packet size.");
                return;
            }

            byte udpNumber = data[0]; // 0 ~ 3 // 1 byte
            int psn = data[2]; // 0 ~ 104 // 1 byte

            lock (_lock)
            {
                // Check if the packet with the same udpNumber and psn has already been received
                if (_receivedPacketsDict.ContainsKey(psn) && (_receivedPacketFlagsDict[psn] & (1 << udpNumber)) != 0)
                {
                    // Parse the combined data and update the bitmap with available lines
                    byte[] combinedData = new byte[ValidDataSize * TotalLines];
                    for (int i = 0; i < TotalLines; i++)
                    {
                        if (_receivedPacketFlagsDict.ContainsKey(i))
                        {
                            for (int j = 0; j < 4; j++)
                            {
                                if (_receivedPacketsDict[i][j] != null)
                                {
                                    Array.Copy(_receivedPacketsDict[i][j], 0, combinedData, i * ValidDataSize + 1 + j * (UdpPacketSize - 2), UdpPacketSize - 2);
                                } // if
                            } // if
                        } // if
                    } // for

                    Task.Run(() => ParseCombinedData(combinedData));
                    Dispatcher.Invoke(() => UpdateBitmap());

                    // Clear the dictionaries for the next frame
                    _receivedPacketFlagsDict.Clear();
                    _receivedPacketsDict.Clear();
                    ResetData();
                } // if

                // Initialize the storage for the packets if not already done
                if (!_receivedPacketsDict.ContainsKey(psn))
                {
                    _receivedPacketsDict[psn] = new byte[4][];
                    _receivedPacketFlagsDict[psn] = 0;
                } // if

                // Store the received packet
                _receivedPacketsDict[psn][udpNumber] = new byte[UdpPacketSize - 2];
                Array.Copy(data, 2, _receivedPacketsDict[psn][udpNumber], 0, UdpPacketSize - 2);
                _receivedPacketFlagsDict[psn] |= (1 << udpNumber);

                // Check if all 4 UDP packets for this PSN have been received
                if (_receivedPacketFlagsDict[psn] == 0x0F)
                {
                    // All packets received for this PSN, now check if we have all PSNs for the frame
                    bool allPacketsReceived = true;
                    for (int i = 0; i < TotalLines; i++)
                    {
                        if (!_receivedPacketFlagsDict.ContainsKey(i) || _receivedPacketFlagsDict[i] != 0x0F)
                        {
                            allPacketsReceived = false;
                            break;
                        } // if
                    } // for

                    if (allPacketsReceived)
                    {
                        // Process the complete frame
                        byte[] combinedData = new byte[ValidDataSize * TotalLines];
                        for (int i = 0; i < TotalLines; i++)
                        {
                            for (int j = 0; j < 4; j++)
                            {
                                Array.Copy(_receivedPacketsDict[i][j], 0, combinedData, i * ValidDataSize + 1 + j * (UdpPacketSize - 2), UdpPacketSize - 2);
                            } // for
                        } // for

                        // Parse the combined data
                        Task.Run(() => ParseCombinedData(combinedData));

                        // Clear the flags and buffer for the next frame
                        _receivedPacketFlagsDict.Clear();
                        _receivedPacketsDict.Clear();
                        ResetData();
                    } // if
                } // if
            } // lock
        } // ParseUdpPacket

        private void ParseCombinedData(byte[] combinedData)
        {
            for (int i = 0; i < TotalLines; i++)
            {
                int offset = i * ValidDataSize;
                ushort header = (ushort)(combinedData[offset] | (combinedData[offset + 1] << 8)); // 2 bytes
                byte psn = combinedData[offset + 2]; // 0 ~ 104 // 1 byte
                ushort packSize = (ushort)(combinedData[offset + 3] | (combinedData[offset + 4] << 8)); // 2 bytes
                byte[] ambientData = new byte[AmbientDataSize * 3]; // 4680 bytes
                Array.Copy(combinedData, offset + 5, ambientData, 0, AmbientDataSize * 3);
                ushort checksum = (ushort)(combinedData[offset + 5 + (AmbientDataSize * 3)] | (combinedData[offset + 6 + (AmbientDataSize * 3)] << 8)); // 2 bytes

                bool hasInvalidData = false;
                // Print extracted data to the text box
                Dispatcher.Invoke(() =>
                {
                    DebugWindow.Instance.DataTextBox.AppendText($"----------------------------------------------------------------------\n");
                    DebugWindow.Instance.DataTextBox.AppendText($"-------- Header: 0x{header:X4}\n");
                    DebugWindow.Instance.DataTextBox.AppendText($"-------- PSN: 0x{psn:X2}\n");
                    DebugWindow.Instance.DataTextBox.AppendText($"-------- Packet Size: 0x{packSize:X4}\n");
                    DebugWindow.Instance.DataTextBox.AppendText($"-------- Checksum: 0x{checksum:X4}\n");

                    // Verify the packet size
                    if (packSize != ValidDataSize)
                    {
                        DebugWindow.Instance.DataTextBox.AppendText($"Invalid packet size field: 0x{packSize:X4}\n");
                        hasInvalidData = true;
                    }

                    // Calculate and verify the checksum
                    ushort calculatedChecksum = CalculateChecksum(combinedData, offset, ValidDataSize - 2);
                    if (checksum != calculatedChecksum)
                    {
                        DebugWindow.Instance.DataTextBox.AppendText($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}\n");
                        hasInvalidData = true;
                    }

                    DebugWindow.Instance.DataTextBox.AppendText($"----------------------------------------------------------------------\n");
                });

                // Store the packet data
                if (psn < TotalLines)
                {
                    _receivedPackets[psn] = ambientData;
                    _receivedPacketFlags[psn] = true;
                }
            }

            Dispatcher.Invoke(() => UpdateBitmap());
        } // ParseCombinedData

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

            while (startIndex <= _bufferIndex - ValidDataSize_uart)
            {
                // Check for the start of a packet
                if (_buffer[startIndex] == 0x55 && _buffer[startIndex + 1] == 0xAA)
                {
                    byte[] packet = new byte[ValidDataSize_uart];
                    Array.Copy(_buffer, startIndex, packet, 0, ValidDataSize_uart);

                    // Verify the packet size
                    ushort packSize = (ushort)(packet[3] | (packet[4] << 8));
                    if (packSize == ValidDataSize_uart)
                    {
                        // Process the complete packet
                        Dispatcher.Invoke(() => ParseMipiPacket(packet, false));

                        // Move the start index to the next potential packet
                        startIndex += ValidDataSize_uart;
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

        public void ParseMipiPacket(byte[] data, bool isUdp = false)
        {
            int validDataSize = isUdp ? ValidDataSize : ValidDataSize_uart;

            // Ensure the data length matches the expected packet size
            if (data.Length != validDataSize)
            {
                DebugWindow.Instance.DataTextBox.AppendText("Invalid packet size.\n");
                // print the data for debug
                DebugWindow.Instance.DataTextBox.AppendText($"Data: {BitConverter.ToString(data)}\n");
                return;
            }

            // Extract fields from the data
            int offset = isUdp ? 1 : 0; // Offset for UDP packets to skip udp_number
            ushort header = (ushort)(data[offset] | (data[offset + 1] << 8)); // 2 bytes
            byte psn = data[offset + 2]; // 0 ~ 104 // 1 byte
            ushort packSize = (ushort)(data[offset + 3] | (data[offset + 4] << 8)); // 2 bytes
            byte[] ambientData = new byte[AmbientDataSize * 3]; // 4680 bytes
            Array.Copy(data, offset + 5, ambientData, 0, AmbientDataSize * 3);
            ushort checksum = (ushort)(data[offset + 5 + (AmbientDataSize * 3)] | (data[offset + 6 + (AmbientDataSize * 3)] << 8)); // 2 bytes

            bool hasInvalidData = false;
            // Print extracted data to the text box
            DebugWindow.Instance.DataTextBox.AppendText($"----------------------------------------------------------------------\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Header: 0x{header:X4}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- PSN: 0x{psn:X2}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Packet Size: 0x{packSize:X4}\n");
            DebugWindow.Instance.DataTextBox.AppendText($"-------- Checksum: 0x{checksum:X4}\n");

            // Verify the packet size
            if (packSize != validDataSize)
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
            return 0x1234; // for now
            ushort checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        } // CalculateChecksum

        private ushort CalculateChecksum(byte[] data, int offset, int length)
        {
            return 0x1234; // for now
            ushort checksum = 0;
            for (int i = offset; i < offset + length; i++)
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
            if (IsCheckboxChecked)
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
        } // DrawAxes

        private void ImagePanel_Loaded(object sender, RoutedEventArgs e)
        {
            ResizeBitmapToFitDockPanel();
        } // ImagePanel_Loaded
        public void InitializeBitmap()
        {
            _bitmap = new WriteableBitmap(TotalLines, AmbientDataSize, 96, 96, PixelFormats.Gray8, null);
            ImageCanvas.Background = new ImageBrush(_bitmap);
            _imageData = new byte[TotalLines * AmbientDataSize * 3];
            //DrawAxes(); // Draw the axes on the image
        } // InitializeBitmap

        public void ResizeBitmapToFitDockPanel()
        {
            //return;
            double panelHeight = ImagePanel.ActualHeight;
            double panelWidth = ImagePanel.ActualWidth;
            double imageHeight = _bitmap.PixelHeight;
            double imageWidth = _bitmap.PixelWidth;

            if (panelHeight > 0 && imageHeight > 0)
            {
                double scale = (panelHeight / imageHeight) * 0.95;
                ImageScaleTransform.ScaleX = scale;
                ImageScaleTransform.ScaleY = scale;

                double scaledImageWidth = imageWidth * scale;
                double scaledImageHeight = imageHeight * scale;
                double offsetX = (imageWidth - scaledImageWidth) / 2;
                double offsetY = (panelHeight - scaledImageHeight) / 2;

                ImageCanvas.RenderTransform = new TransformGroup
                {
                    Children = new TransformCollection
                    {
                        new ScaleTransform(scale, scale),
                        new TranslateTransform(offsetX, offsetY)
                    } // Children
                };
            } // if
        } // ResizeBitmapToFitDockPanel

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
            //DrawAxes(); // Redraw the axes after updating the bitmap
            CalculateCentroids();
        } // UpdateBitmap

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
            // InitializeBitmap(); // Reinitialize the bitmap
        } // ResetData


        // --------------------------- Calculation of the Centroids ---------------------------
        public void CalculateCentroids()
        {
            int topBottomSegmentHeight = 313;
            int middleSegmentHeight = 315;
            int dontCareHeight = (AmbientDataSize - (2 * topBottomSegmentHeight + middleSegmentHeight)) / 2; // Adjusted to ensure the total height is 1560
            var centroids = new List<Point>();
            var originalCentroids = new List<Point>();

            for (int segment = 0; segment < 3; segment++)
            {
                int startY;
                int endY;
                int segmentHeight;

                if (segment == 1)
                {
                    // Middle segment
                    startY = topBottomSegmentHeight + dontCareHeight;
                    endY = startY + middleSegmentHeight;
                    segmentHeight = middleSegmentHeight;
                }
                else
                {
                    // Top and bottom segments
                    startY = segment * (topBottomSegmentHeight + dontCareHeight);
                    endY = startY + topBottomSegmentHeight;
                    segmentHeight = topBottomSegmentHeight;
                }

                if (segment == 2)
                {
                    // For the bottom segment, adjust the end Y value
                    endY = AmbientDataSize;
                }

                double sumX = 0;
                double sumY = 0;
                double sumValue = 0;

                for (int y = startY; y < endY; y++)
                {
                    for (int x = 0; x < TotalLines; x++)
                    {
                        int index = y * TotalLines + x;
                        byte[] packet = _receivedPackets[x];
                        if (packet != null)
                        {
                            int packetIndex = y * 3;
                            int value = (packet[packetIndex] << 16) | (packet[packetIndex + 1] << 8) | packet[packetIndex + 2]; // Combine RGB values into a single integer
                            if (value == 0)
                            {
                                value = 1; // Avoid division by zero
                            } // if
                            // Adjust x and y to have (0,0) at the center of the segment
                            double adjustedX = x - (TotalLines / 2.0);
                            double adjustedY = y - (startY + segmentHeight / 2.0);

                            sumX += adjustedX * value;
                            sumY += adjustedY * value;
                            sumValue += value;
                        }
                    }
                }

                double centroidX = sumX / sumValue;
                double centroidY = sumY / sumValue;

                // Store the original centroid coordinates
                originalCentroids.Add(new Point(centroidX + (TotalLines / 2.0), centroidY + (startY + segmentHeight / 2.0)));

                // Adjust coordinates to have (0,0) at the center of the segment
                centroids.Add(new Point(centroidX, centroidY));
            } // for

            // Output the centroids
            for (int i = 0; i < centroids.Count; i++)
            {
                DebugWindow.Instance.DataTextBox.AppendText($"Centroid of segment {i + 1}: ({centroids[i].X}, {centroids[i].Y})\n");
            }

            // Draw the centroids and segment boundaries on the bitmap using original coordinates
            DrawCentroidsAndSegments(originalCentroids);
        } // CalculateCentroids

        public void DrawCentroidsAndSegments(List<Point> centroids)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                // Draw the segment boundaries
                int topBottomSegmentHeight = 313;
                int middleSegmentHeight = 315;
                int dontCareHeight = (AmbientDataSize - (2 * topBottomSegmentHeight + middleSegmentHeight)) / 2;

                // Top segment boundary
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, 0, TotalLines, topBottomSegmentHeight));

                // Middle segment boundary
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, topBottomSegmentHeight + dontCareHeight, TotalLines, middleSegmentHeight));

                // Bottom segment boundary
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, 2 * (topBottomSegmentHeight + dontCareHeight), TotalLines, topBottomSegmentHeight));

                // Draw the centroids
                foreach (var centroid in centroids)
                {
                    // Draw a circle at the centroid location
                    context.DrawEllipse(Brushes.Red, null, centroid, 5, 5);
                }
            }

            // Render the visual to a bitmap
            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(_bitmap.PixelWidth, _bitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Combine the centroids and segment boundaries with the existing bitmap
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
        } // DrawCentroidsAndSegments
    } // class MainWindow
} // namespace UsbApp
