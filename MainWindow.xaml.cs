using System;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Controls; // Add this using directive
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

// Note :
// color
// image refresh rate
// packSize 0x06C7 (1735)

namespace UsbApp
{
    public partial class MainWindow : Window
    {
        private const int AmbientDataSize = 576; // Size of the ambient data
        private const int PacketSize = 2 + 1 + 2 + (AmbientDataSize * 3) + 2; // Total packet size (should be 1735)

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
        private const int TotalLines = 105;
        private byte[][] _receivedPackets = new byte[TotalLines][];
        private bool[] _receivedPacketFlags = new bool[TotalLines];
        private Point? _clickedPoint = null;

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
            if (_serialPort != null && _serialPort.IsOpen)
            {
                // Close the serial port and update the button content
                _serialPort.Close();
                ((Button)sender).Content = "Start Reading";
                ResetData();
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

            for (int i = 0; i < bytesToRead; i++)
            {
                byte currentByte = buffer[i];

                // State machine to detect the start of the packet
                switch (_accumulatedBufferIndex)
                {
                    case 0:
                        if (currentByte == 0x55)
                        {
                            _accumulatedBuffer[_accumulatedBufferIndex++] = currentByte;
                        }
                        break;
                    case 1:
                        if (currentByte == 0xAA)
                        {
                            _accumulatedBuffer[_accumulatedBufferIndex++] = currentByte;
                        }
                        else
                        {
                            _accumulatedBufferIndex = 0; // Reset if the second byte is not 0xAA
                        }
                        break;
                    default:
                        _accumulatedBuffer[_accumulatedBufferIndex++] = currentByte;
                        break;
                }

                // Check if we have a complete packet
                if (_accumulatedBufferIndex >= PacketSize)
                {
                    byte[] completePacket = new byte[PacketSize];
                    Array.Copy(_accumulatedBuffer, completePacket, PacketSize);

                    // Reset the accumulated buffer index
                    _accumulatedBufferIndex = 0;

                    // Process the complete packet
                    Dispatcher.Invoke(() => ParseMipiPacket(completePacket));
                }
            } // for
        } // SerialPort_DataReceived

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

            // Print extracted data to the text box
            DataTextBox.AppendText($"Header: 0x{header:X4}\n");
            DataTextBox.AppendText($"PSN: 0x{psn:X2}\n");
            DataTextBox.AppendText($"Packet Size: 0x{packSize:X4}\n");
            DataTextBox.AppendText($"Checksum: 0x{checksum:X4}\n");
            DataTextBox.AppendText($"---------------------------------------\n");

            // Verify the header
            if (header != 0xAA55)
            {
                // Print the device info
                DataTextBox.AppendText($"Device Info: {BitConverter.ToString(data)}\n");
                return;
            }

            // Verify the packet size
            if (packSize != PacketSize)
            {
                DataTextBox.AppendText($"Invalid packet size field: 0x{packSize:X4}\n");
                return;
            } // if

            // Calculate and verify the checksum
            ushort calculatedChecksum = CalculateChecksum(data, data.Length - 2);
            if (checksum != calculatedChecksum)
            {
                DataTextBox.AppendText($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}\n");
                return;
            } // if

            // Ignore packets with psn 106~254
            if (psn >= 105)
            {
                return;
            } // if

            // Store the packet data
            if (psn < TotalLines)
            {
                _receivedPackets[psn] = ambientData;
                _receivedPacketFlags[psn] = true;
            }

            // Check if we have received all packets
            if (psn >= TotalLines - 1)
            {
                UpdateBitmap();
                Array.Clear(_receivedPacketFlags, 0, _receivedPacketFlags.Length);
            }
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
                // Calculate the index in the grayData array
                int index = y * TotalLines + x;

                // Retrieve the ambient data value
                byte[] pixelData = new byte[4]; // Ensure the buffer is at least 4 bytes
                Int32Rect rect = new Int32Rect(x, y, 1, 1);
                _bitmap.CopyPixels(rect, pixelData, 4, 0); // Use a stride of 4
                byte ambientDataValue = pixelData[0];

                // Display the coordinate and ambient data value
                CoordinateDataTextBlock.Text = $"Coordinate: ({x}, {y}), Ambient Data Value: {ambientDataValue}";

                // Store the clicked point and redraw the axes
                _clickedPoint = clickedPoint;
                DrawAxes();
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

        private void ResetData()
        {
            _currentLine = 0;
            _accumulatedBufferIndex = 0;
            Array.Clear(_accumulatedBuffer, 0, _accumulatedBuffer.Length);
            Array.Clear(_receivedPackets, 0, _receivedPackets.Length);
            Array.Clear(_receivedPacketFlags, 0, _receivedPacketFlags.Length);
        } // ResetData
    } // class MainWindow
} // namespace UsbApp
