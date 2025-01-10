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

// Note :
// color
// image refresh rate
// packSize 0x06C7 (1735 bytes)

namespace UsbApp
{
    public partial class MainWindow : Window
    {
        // data speed : 64 bytes per 0.01s (sleep(10000)) should be successfully received and displayed
        private const int AmbientDataSize = 576; // Size of the ambient data
        private const int PacketSize = 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total packet size (should be 1735)
        private const int TotalLines = 105;

        private const int BufferSize = PacketSize * TotalLines * 2; // Increase buffer size for potential board info
        private byte[] _buffer = new byte[BufferSize];
        private int _bufferIndex = 0;

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
        private byte[] _accumulatedBuffer = new byte[PacketSize];
        private int _accumulatedBufferIndex = 0;
        private byte[][] _receivedPackets = new byte[TotalLines][];
        private bool[] _receivedPacketFlags = new bool[TotalLines];
        private Point? _clickedPoint = null;
        private string _topLeftCoordinate;
        private string _topRightCoordinate;
        private string _bottomLeftCoordinate;
        private string _bottomRightCoordinate;
        private int _lastPsn = -1;

        public string TopLeftCoordinate
        {
            get => _topLeftCoordinate;
            set
            {
                _topLeftCoordinate = value;
                OnPropertyChanged();
            }
        }

        public string TopRightCoordinate
        {
            get => _topRightCoordinate;
            set
            {
                _topRightCoordinate = value;
                OnPropertyChanged();
            }
        }

        public string BottomLeftCoordinate
        {
            get => _bottomLeftCoordinate;
            set
            {
                _bottomLeftCoordinate = value;
                OnPropertyChanged();
            }
        }

        public string BottomRightCoordinate
        {
            get => _bottomRightCoordinate;
            set
            {
                _bottomRightCoordinate = value;
                OnPropertyChanged();
            }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            UpdateCoordinateLabels();
            PopulateSerialPortComboBox();
            InitializeBitmap();
            InitializeTimer();
            _rand = new Random(); // Initialize the random number generator

            CompositionTarget.Rendering += CompositionTarget_Rendering;
        } // MainWindow

        private void ClearTextButton_Click(object sender, RoutedEventArgs e)
        {
            DataTextBox.Clear();
        } // ClearTextButton_Click

        private void SaveLogButton_Click(object sender, RoutedEventArgs e)
        {
            SaveLog();
        } // SaveLogButton_Click

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
        } // SaveLog

        private void UpdateCoordinateLabels()
        {
            TopLeftCoordinate = "( 0, 0 )";
            TopRightCoordinate = $"( {TotalLines}, 0 )";
            BottomLeftCoordinate = $"( 0, {AmbientDataSize} )";
            BottomRightCoordinate = $"( {TotalLines}, {AmbientDataSize} )";
        } // UpdateCoordinateLabels

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        } // OnPropertyChanged

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
            int bytesToRead = _serialPort.BytesToRead;
            byte[] buffer = new byte[bytesToRead];
            _serialPort.Read(buffer, 0, bytesToRead);

            // Copy the received data into the buffer
            Array.Copy(buffer, 0, _buffer, _bufferIndex, bytesToRead);
            _bufferIndex += bytesToRead;

            // Try to parse the buffer for valid packets
            ParseBuffer();
        } // SerialPort_DataReceived

        private void ParseBuffer()
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

        private void ParseMipiPacket(byte[] data)
        {
            // Ensure the data length matches the expected packet size
            if (data.Length != PacketSize)
            {
                DataTextBox.AppendText("Invalid packet size.\n");
                // print the data for debug
                DataTextBox.AppendText($"Data: {BitConverter.ToString(data)}\n");
                return;
            }

            // Extract fields from the data
            ushort header = (ushort)(data[0] | (data[1] << 8)); // 2 bytes
            byte psn = data[2]; // 1~254 for now, 0 ~ 104 in the future // 1 byte
            ushort packSize = (ushort)(data[3] | (data[4] << 8)); // 2 bytes
            byte[] ambientData = new byte[AmbientDataSize * 3]; // 1728 bytes
            Array.Copy(data, 5, ambientData, 0, AmbientDataSize * 3);
            ushort checksum = (ushort)(data[5 + (AmbientDataSize * 3)] | (data[6 + (AmbientDataSize * 3)] << 8)); // 2 bytes

            bool hasInvalidData = false;
            // Print extracted data to the text box
            DataTextBox.AppendText($"----------------------------------------------------------------------\n");
            DataTextBox.AppendText($"-------- Header: 0x{header:X4}\n");
            DataTextBox.AppendText($"-------- PSN: 0x{psn:X2}\n");
            DataTextBox.AppendText($"-------- Packet Size: 0x{packSize:X4}\n");
            DataTextBox.AppendText($"-------- Checksum: 0x{checksum:X4}\n");

            // Verify the packet size
            if (packSize != PacketSize)
            {
                DataTextBox.AppendText($"Invalid packet size field: 0x{packSize:X4}\n");
                hasInvalidData = true;
            } // if

            // Calculate and verify the checksum
            ushort calculatedChecksum = CalculateChecksum(data, data.Length - 2);
            if (checksum != calculatedChecksum)
            {
                DataTextBox.AppendText($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}\n");
                hasInvalidData = true;
            } // if

            DataTextBox.AppendText($"----------------------------------------------------------------------\n");

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


        private ushort CalculateChecksum(byte[] data, int length)
        {
            return 0x44A0; // 0x44A0 is every packet's checksum for testing
            ushort checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        } // CalculateChecksum

        private void VerifyBitmapDimensions()
        {
            int expectedWidth = TotalLines;
            int expectedHeight = AmbientDataSize;

            if (_bitmap.PixelWidth != expectedWidth || _bitmap.PixelHeight != expectedHeight)
            {
                MessageBox.Show($"Bitmap dimensions are incorrect. Expected: {expectedWidth}x{expectedHeight}, Actual: {_bitmap.PixelWidth}x{_bitmap.PixelHeight}");
            } // if

            ImageDimensionsTextBlock.Text = $"Image Dimensions: {_bitmap.PixelWidth}x{_bitmap.PixelHeight}";
        } // VerifyBitmapDimensions

        private DrawingVisual CreateAxesVisual()
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

        private void DrawAxes()
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


        private void InitializeBitmap()
        {
            _bitmap = new WriteableBitmap(TotalLines, AmbientDataSize, 96, 96, PixelFormats.Gray8, null);
            ImageCanvas.Background = new ImageBrush(_bitmap);
            _imageData = new byte[TotalLines * AmbientDataSize * 3];
            VerifyBitmapDimensions();
            DrawAxes(); // Draw the axes on the image
        } // InitializeBitmap


        private void UpdateBitmap()
        {
            VerifyBitmapDimensions();

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
        }

        private void ImageCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _lastMousePosition = e.GetPosition(ImageCanvas);
            _isDragging = true;
            ImageCanvas.CaptureMouse();

            // Retrieve the coordinate of the clicked point
            Point clickedPoint = e.GetPosition(ImageCanvas);
            int x = (int)clickedPoint.X;
            int y = (int)clickedPoint.Y;

            // Ensure the coordinates are within the image bounds
            if (x >= 0 && x < TotalLines && y >= 0 && y < AmbientDataSize)
            {
                // Calculate the index in the ambient data array
                int packetIndex = x;
                int dataIndex = y * 3;

                if (_receivedPacketFlags[packetIndex])
                {
                    byte r = _receivedPackets[packetIndex][dataIndex];
                    byte g = _receivedPackets[packetIndex][dataIndex + 1];
                    byte b = _receivedPackets[packetIndex][dataIndex + 2];
                    byte ambientDataValue = (byte)((r + g + b) / 3);

                    // Display the coordinate and ambient data value
                    CoordinateDataTextBlock.Text = $"({x}, {y}): Ambient Data {ambientDataValue} ({r:X2} {g:X2} {b:X2})";

                    // Store the clicked point and redraw the axes
                    _clickedPoint = clickedPoint;
                    DrawAxes();
                }
                else
                {
                    CoordinateDataTextBlock.Text = "No data available for the clicked point.";
                }
            }
            else
            {
                CoordinateDataTextBlock.Text = "Clicked point is outside the image bounds.";
            }
        } // ImageCanvas_MouseLeftButtonDown


        private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isDragging)
            {
                Point currentPosition = e.GetPosition(ImageCanvas);
                double offsetX = currentPosition.X - _lastMousePosition.X;
                double offsetY = currentPosition.Y - _lastMousePosition.Y;

                // Calculate the new translation values
                double newTranslateX = _translateX + offsetX;
                double newTranslateY = _translateY + offsetY;

                // Get the bounds of the ImageCanvas
                double canvasWidth = ImageCanvas.ActualWidth;
                double canvasHeight = ImageCanvas.ActualHeight;

                // Get the bounds of the image
                double imageWidth = _bitmap.PixelWidth * ImageScaleTransform.ScaleX;
                double imageHeight = _bitmap.PixelHeight * ImageScaleTransform.ScaleY;

                // Ensure the image stays within the bounds of the ImageCanvas
                if (newTranslateX > 0)
                {
                    newTranslateX = 0;
                }
                else if (newTranslateX < canvasWidth - imageWidth)
                {
                    newTranslateX = canvasWidth - imageWidth;
                }

                if (newTranslateY > 0)
                {
                    newTranslateY = 0;
                }
                else if (newTranslateY < canvasHeight - imageHeight)
                {
                    newTranslateY = canvasHeight - imageHeight;
                }

                // Update the translation values
                _translateX = newTranslateX;
                _translateY = newTranslateY;
                _isTranslationUpdated = true;

                _lastMousePosition = currentPosition;
            }
        } // ImageCanvas_MouseMove


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
        private void ClearBuffer()
        {
            Array.Clear(_buffer, 0, _buffer.Length);
            _bufferIndex = 0;
        } // ClearBuffer
        private void ResetData()
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
