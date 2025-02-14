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
using System.Linq;
using System.Diagnostics;

namespace UsbApp
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        // UART data speed : 64 bytes per 0.01s (sleep(10000)) should be successfully received and displayed

        public const int AmbientDataSize_520 = 520; // Size of the ambient data
        public const int AmbientDataSize_1560 = 1560; // Size of the ambient data
        public const int UdpPacketSize_520 = 1047; // Size of each UDP packet for BSAA, should be 1047 bytes // each udp packet has 1 byte udp_num, 1 udp packet forms a vertical line on my bitmap.
        public const int UdpPacketSize_1560 = 1200; // Size of each UDP packet for RXAA, should be 1200 bytes // each udp packet has 1 byte udp_num and 1 byte udp_psn, 4 udp packets form a vertical line on my bitmap.

        public const int ValidDataSize_520 = (int)(AmbientDataSize_520 * 2); // Total valid bytes from BSAA
        // (should be 1040 without udp numbers (1 byte per UDP packet, and 1 packet is enough to form a vertical line on bitmap))

        public const int ValidDataSize_1560 = 2 + 1 + 2 + (AmbientDataSize_1560 * 3) + 2; // Total valid bytes coming from RXAA
        // (should be 4687 without udp numbers & udp_psn (2 byte per UDP packet so 8 bytes in total))

        public const int TotalPacketSize_1560 = 4800; // 0x12C0 (4800 bytes)(4 UDP packet, each is 1200 bytes, but the valid data would be the first 4695 bytes.)
        public const int TotalPacketSize_520 = 1047; // 0x0417 (1047 bytes)(1 UDP packet with 1047 bytes, but the valid data would be the first 1040 bytes.)
        public const int TotalLines = 105;

        private UdpClient udpClient;
        private bool isListening;

        public SerialPort _serialPort;
        public WriteableBitmap _bitmap_1560;
        public WriteableBitmap _bitmap_520;
        public int _currentLine = 0;
        public DispatcherTimer _timer;
        public int _packetIndex = 0;

        public Point clickPosition;
        public byte[][] _receivedPackets = new byte[TotalLines][];
        public bool[] _receivedPacketFlags = new bool[TotalLines];
        public byte[][] _receivedPackets_save = new byte[TotalLines][];
        public bool[] _receivedPacketFlags_save = new bool[TotalLines];
        public Point? _clickedPoint = null;

        public int _lastPsn = -1;
        public DebugWindow _debugWindow;
        public int CanvasWidth => TotalLines;
        public int CanvasHeight_1560 => AmbientDataSize_1560;
        public int CanvasHeight_520 => AmbientDataSize_520;

        // Dictionary to store the EnlargedSegmentWindow instances
        public Dictionary<int, EnlargedSegmentWindow> _enlargedSegmentWindows = new Dictionary<int, EnlargedSegmentWindow>();

        private int _currentTab;
        public int CurrentTab
        {
            get => _currentTab;
            set
            {
                if (_currentTab != value)
                {
                    _currentTab = value;
                    OnPropertyChanged();
                } // if
            } // set
        } // CurrentTab

        private bool _isCheckboxChecked; // shared
        public bool IsCheckboxChecked // shared
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

        // Add fields to store the all-time max value coordinates
        private Point[] _allTimeMaxCoordinates = new Point[3];
        public ulong[] _allTimeMaxValue = new ulong[3];

        private Point[] _currentFrameMaxCoordinates = new Point[3];
        private ulong[] _currentFrameMaxValues = new ulong[3];

        public List<Point> centroid_for_segmentWindow = new List<Point>();

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            // Initialize the all-time max coordinates
            for (int i = 0; i < _allTimeMaxCoordinates.Length; i++)
            {
                _allTimeMaxCoordinates[i] = new Point(0, 0);
                _allTimeMaxValue[i] = 0;
                _currentFrameMaxCoordinates[i] = new Point(0, 0);
                _currentFrameMaxValues[i] = 0;
            } // for
            // Populate the serial port combo box
            // PopulateSerialPortComboBox();
            InitializeBitmap();
            ImageDimensionsTextBlock.Text = $"Image Dimensions: 105x1560";
            ImageDimensionsTextBlock2.Text = $"Image Dimensions: 105x520";

            // Open the debug window
            DebugWindow.Instance.Show();
        } // MainWindow

        private void MainWindow_Closed(object sender, System.EventArgs e)
        {
            StopListening();
            System.Windows.Application.Current.Shutdown();
        } // MainWindow_Closed

        public event PropertyChangedEventHandler PropertyChanged;

        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        } // OnPropertyChanged

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (MainTabControl.SelectedItem is TabItem selectedTab)
            {
                CurrentTab = (selectedTab.Name.ToString() == "Height_1560") ? 1560 : 520;
                Dispatcher.Invoke(() =>
                {
                    DebugWindow.Instance.DataTextBox.AppendText($"Current tab: {selectedTab.Name}\n");
                });

                // Stop listening for UDP packets and update button text
                if (isListening)
                {
                    StopListening();
                } // if
            } // if
        } // MainTabControl_SelectionChanged


        public void SaveDataButton_Click(object sender, RoutedEventArgs e)
        {
            SaveImage();
            SaveDataToCsv();
        } // SaveDataButton_Click

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
                    if (CurrentTab == 1560)
                        encoder.Frames.Add(BitmapFrame.Create(_bitmap_1560));
                    else
                        encoder.Frames.Add(BitmapFrame.Create(_bitmap_520));

                    encoder.Save(stream);
                } // using
            } // if
        } // SaveImage

        private void SaveDataToCsv()
        {
            // Create a file dialog to save the CSV file
            Microsoft.Win32.SaveFileDialog dlg = new Microsoft.Win32.SaveFileDialog();
            dlg.FileName = "Data"; // Default file name
            dlg.DefaultExt = ".csv"; // Default file extension
            dlg.Filter = "CSV Files (*.csv)|*.csv|All Files (*.*)|*.*"; // Filter files by extension

            int AmbientDataSize = (CurrentTab == 1560) ? AmbientDataSize_1560 : AmbientDataSize_520;
            // Show save file dialog box
            bool? result = dlg.ShowDialog();

            // Process save file dialog box results
            if (result == true)
            {
                // Save the data to a CSV file
                string filename = dlg.FileName;
                using (StreamWriter writer = new StreamWriter(filename))
                {
                    for (int row = 0; row < AmbientDataSize; row++)
                    {
                        List<string> rowData = new List<string>();
                        for (int col = 0; col < TotalLines; col++)
                        {
                            if (_receivedPacketFlags_save[col])
                            {
                                if (CurrentTab == 1560)
                                {
                                    byte r = _receivedPackets_save[col][row * 3];
                                    byte g = _receivedPackets_save[col][row * 3 + 1];
                                    byte b = _receivedPackets_save[col][row * 3 + 2];
                                    rowData.Add($"0x{b:X2}{g:X2}{r:X2}");
                                    //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"({col},{row}):{b:X2}\n"));
                                } // if
                                else
                                {
                                    ushort value = (ushort)(_receivedPackets_save[col][row * 2] << 8 | _receivedPackets[col][row * 2 + 1]);
                                    rowData.Add($"0x{value:X4}");
                                } // else
                            } // if
                            else
                            {
                                rowData.Add("NA"); // Default value if packet is not received
                            } // else
                        } // for
                        writer.WriteLine(string.Join(",", rowData));
                    } // for
                } // using
            } // if
        } // SaveDataToCsv

        private async void StartListeningButton_Click(object sender, RoutedEventArgs e)
        {
            Button button = (Button)sender;

            if (isListening)
            {
                StopListening();
                button.Content = "Start Listening";
            } // if
            else
            {
                isListening = true;
                // Reset current frame max values and all-time max values and coordinates
                for (int i = 0; i < _currentFrameMaxValues.Length; i++)
                {
                    _currentFrameMaxValues[i] = 0;
                    _currentFrameMaxCoordinates[i] = new Point(0, 0);
                    _allTimeMaxCoordinates[i] = new Point(0, 0);
                    _allTimeMaxValue[i] = 0;
                } // for
                udpClient = new UdpClient(7000); // Use the appropriate port number
                UdpDataTextBlock.Text = "Listening for UDP packets...";
                UdpDataTextBlock2.Text = "Listening for UDP packets...";
                button.Content = "Stop Listening";

                await Task.Run(() => ListenForUdpPackets());
            } // else
        } // StartListeningButton_Click

        private void StopListening()
        {
            if (udpClient != null)
                udpClient.Close();
            isListening = false;
            ListeningBtn.Content = "Start Listening";
            ListeningBtn2.Content = "Start Listening";
            UdpDataTextBlock.Text = "Stopped listening.";
            UdpDataTextBlock2.Text = "Stopped listening.";
        } // StopListening

        private async void ListenForUdpPackets()
        {
            try
            {
                while (isListening)
                {
                    var result = await udpClient.ReceiveAsync();
                    byte[] receivedData = result.Buffer;
                    //Dispatcher.Invoke(() => UdpDataTextBlock.Text = $"{receivedData[0]}");
                    //return;
                    ParseUdpPacket(receivedData);
                } // while
            } // try
            catch (ObjectDisposedException)
            {
                // Handle the case when the UDP client is closed
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = $"UDP client is closed.");
            } // catch
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = $"Error: {ex}");
            } // catch
        } // ListenForUdpPackets

        private Dictionary<int, byte[][]> _receivedPacketsDict = new Dictionary<int, byte[][]>();
        private Dictionary<int, int> _receivedPacketFlagsDict = new Dictionary<int, int>();
        private readonly object _lock = new object();

        public void ParseUdpPacket(byte[] data)
        {
            // print the data for debug
            //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"Data: {BitConverter.ToString(data)}\n"));
            //return;

            int UdpPacketSize = (CurrentTab == 1560) ? UdpPacketSize_1560 : UdpPacketSize_520;
            int ValidDataSize = (CurrentTab == 1560) ? ValidDataSize_1560 : ValidDataSize_520;

            if (data.Length != UdpPacketSize)
            {
                Dispatcher.Invoke(() => UdpDataTextBlock.Text = $"Invalid UDP packet size. {data.Length} \n");
                return;
            } // if

            if (CurrentTab == 1560) // RXAA
            {
                byte udpNumber = data[0]; // 0 ~ 3 // 1 byte
                int psn = data[1]; // 0 ~ 104 // 1 byte
                                   // print the data for debug

                lock (_lock)
                {
                    // Check if the packet with the same udpNumber and psn has already been received
                    if (_receivedPacketsDict.ContainsKey(psn) && (_receivedPacketFlagsDict[psn] & (1 << udpNumber)) != 0)
                    {
                        // Parse the combined data and update the bitmap with available lines
                        byte[] combinedData = new byte[(ValidDataSize + 1) * TotalLines];
                        for (int i = 0; i < TotalLines; i++)
                        {
                            if (_receivedPacketFlagsDict.ContainsKey(i))
                            {
                                for (int j = 0; j < 4; j++)
                                {
                                    if (_receivedPacketsDict[i][j] != null)
                                    {
                                        int combinedDataOffset = j * (UdpPacketSize - 2);
                                        // Store the received packet
                                        _receivedPackets[i] = new byte[AmbientDataSize_1560 * 3];
                                        _receivedPacketFlags[i] = true;
                                        if (j == 3)
                                        {
                                            Array.Copy(_receivedPacketsDict[i][j], 0, _receivedPackets[i], combinedDataOffset, 1086);

                                        } // if
                                        else
                                        {
                                            Array.Copy(_receivedPacketsDict[i][j], 0, _receivedPackets[i], combinedDataOffset, UdpPacketSize - 2);
                                        } // else

                                        // print the data for debug
                                        //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"_receivedPacketsDict[{i}][{j}]: {combinedData[combinedDataOffset]}\n"));
                                    } // if
                                } // for
                            } // if
                        } // for

                        //ParseCombinedData(combinedData);
                        Dispatcher.Invoke(() =>
                        {
                            UpdateBitmap();
                            // Clear the dictionaries for the next frame
                            _receivedPacketFlagsDict.Clear();
                            _receivedPacketsDict.Clear();
                            ResetData();
                        });
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
                    //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"test: {BitConverter.ToString(_receivedPacketsDict[psn][udpNumber])}, psn: {psn}\n"));
                    //return;

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
                            for (int i = 0; i < TotalLines; i++)
                            {
                                _receivedPacketFlags[i] = true;
                                byte[] wholeVerticalLine = new byte[AmbientDataSize_1560 * 3];

                                for (int j = 0; j < 4; j++)
                                {
                                    int combinedDataOffset = j * (UdpPacketSize - 2);
                                    // Store the received packet
                                    if (j == 3)
                                    {
                                        Array.Copy(_receivedPacketsDict[i][j], 0, wholeVerticalLine, combinedDataOffset, 1086);
                                    } // if
                                    else
                                    {
                                        Array.Copy(_receivedPacketsDict[i][j], 0, wholeVerticalLine, combinedDataOffset, _receivedPacketsDict[i][j].Length);
                                    } // else

                                    // print the data for debug
                                    //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"OG: {i}, {j} :: {_receivedPacketsDict[i][j][0]:X2}-{_receivedPacketsDict[i][j][1]:X2}---{_receivedPacketsDict[i][j][1197]:X2}\n" +
                                    //    $"DE: {i},{j} ::{wholeVerticalLine[2395]:X2}-{wholeVerticalLine[2396]:X2}-{wholeVerticalLine[2397]:X2}\n"));
                                    //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"{combinedDataOffset}\n"));
                                } // for

                                // Paste wholeVerticalLine to _receivedPackets[i]
                                _receivedPackets[i] = wholeVerticalLine.ToArray();

                                //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"udp: {_receivedPackets[i].Length}, psn: {_receivedPacketsDict[i][0][1]:X2}\n"));
                            } // for

                            // Parse the combined data
                            //ParseCombinedData(combinedData);
                            Dispatcher.Invoke(() =>
                            {
                                UpdateBitmap();
                                // Clear the flags and buffer for the next frame
                                _receivedPacketFlagsDict.Clear();
                                _receivedPacketsDict.Clear();
                                ResetData();
                            });
                        } // if
                    } // if
                } // lock
            } // if
            else // BSAA
            {
                int psn = data[0]; // 0 ~ 104 // 1 byte
                                   // Print the data for debug
                                   // Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"psn: {psn}\n"));
                                   // return;

                lock (_lock)
                {
                    // Store the received packet
                    _receivedPackets[psn] = new byte[UdpPacketSize - 7];
                    Array.Copy(data, 1, _receivedPackets[psn], 0, UdpPacketSize - 7);
                    _receivedPacketFlags[psn] = true;

                    // Check if all packets for the frame have been received
                    bool allPacketsReceived = true;
                    for (int i = 0; i < TotalLines; i++)
                    {
                        if (!_receivedPacketFlags[i])
                        {
                            allPacketsReceived = false;
                            break;
                        }
                    }

                    if (allPacketsReceived)
                    {
                        // Process the complete frame
                        Dispatcher.Invoke(() =>
                        {
                            // print the whole _receivedPackets
                            var debugMessages = new StringBuilder();
                            for (int i = 0; i < TotalLines; i++)
                            {
                                debugMessages.AppendLine($"_receivedPackets[{i}]: {BitConverter.ToString(_receivedPackets[i])}");
                            } // for

                            DebugWindow.Instance.DataTextBox.AppendText(debugMessages.ToString());
                            UpdateBitmapV2();
                        });

                        // Clear the flags and buffer for the next frame
                        ResetData();
                    } // if
                } // lock
            } // else
        } // ParseUdpPacket

        private void ParseCombinedData(byte[] combinedData)
        {
            var debugMessages = new StringBuilder();
            var invalidDataMessages = new List<string>();
            int AmbientDataSize = (CurrentTab == 1560) ? AmbientDataSize_1560 : AmbientDataSize_520;
            int ValidDataSize = (CurrentTab == 1560) ? ValidDataSize_1560 : ValidDataSize_520;
            for (int i = 0; i < TotalLines; i++)
            {
                int offset = i * (ValidDataSize + 1);
                //ushort header = (ushort)(combinedData[offset] | (combinedData[offset + 1] << 8)); // 2 bytes
                byte psn = combinedData[offset]; // 0 ~ 104 // 1 byte
                //ushort packSize = (ushort)(combinedData[offset + 3] | (combinedData[offset + 4] << 8)); // 2 bytes
                //byte[] ambientData = new byte[AmbientDataSize * 3]; // 4680 bytes
                byte[] ambientData = (CurrentTab == 1560) ? new byte[AmbientDataSize * 3] : new byte[AmbientDataSize * 2]; //4680 / 1040 bytes
                //Array.Copy(combinedData, offset + 1, ambientData, 0, AmbientDataSize * 3);
                if (CurrentTab == 1560)
                    Array.Copy(combinedData, offset + 1, ambientData, 0, AmbientDataSize * 3);
                else
                    Array.Copy(combinedData, offset + 1, ambientData, 0, AmbientDataSize * 2);
                //ushort checksum = (ushort)(combinedData[offset + 5 + (AmbientDataSize * 3)] | (combinedData[offset + 6 + (AmbientDataSize * 3)] << 8)); // 2 bytes
                bool hasInvalidData = false;

                // Collect debug messages
                debugMessages.AppendLine("----------------------------------------------------------------------");
                //debugMessages.AppendLine($"-------- Header: 0x{header:X4}");
                debugMessages.AppendLine($"-------- PSN: 0x{psn:X2}");
                //debugMessages.AppendLine($"-------- Packet Size: 0x{packSize:X4}");
                //debugMessages.AppendLine($"-------- Checksum: 0x{checksum:X4}");

                // Verify the packet size
                //if (packSize != ValidDataSize)
                //{
                //    invalidDataMessages.Add($"Invalid packet size field: 0x{packSize:X4}");
                //    hasInvalidData = true;
                //} // if

                // Calculate and verify the checksum
                //ushort calculatedChecksum = CalculateChecksum(combinedData, offset, ValidDataSize - 2);
                //if (checksum != calculatedChecksum)
                //{
                //    invalidDataMessages.Add($"Checksum mismatch: expected 0x{calculatedChecksum:X4}, got 0x{checksum:X4}");
                //    hasInvalidData = true;
                //} // if

                debugMessages.AppendLine("----------------------------------------------------------------------");

                // Store the packet data
                if (psn < TotalLines)
                {
                    _receivedPackets[psn] = ambientData;
                    _receivedPacketFlags[psn] = true;
                } // if
            } // for

            // Update the UI in a single Dispatcher.Invoke call
            Dispatcher.Invoke(() =>
            {
                //DebugWindow.Instance.DataTextBox.AppendText(debugMessages.ToString());
                foreach (var message in invalidDataMessages)
                {
                    //DebugWindow.Instance.DataTextBox.AppendText(message + "\n");
                } // foreach
                // print out the combinedData for debug
                //DebugWindow.Instance.DataTextBox.AppendText($"Combined Data: {combinedData[1]}\n");
                UpdateBitmap();
            });
        } // ParseCombinedData

        public ushort CalculateChecksum(byte[] data, int length)
        {
            ushort checksum = 0;
            for (int i = 0; i < length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        } // CalculateChecksum

        private ushort CalculateChecksum(byte[] data, int offset, int length)
        {
            ushort checksum = 0;
            for (int i = offset; i < offset + length; i++)
            {
                checksum += data[i];
            }
            return checksum;
        } // CalculateChecksum

        public void InitializeBitmap()
        {
            _bitmap_1560 = new WriteableBitmap(TotalLines, AmbientDataSize_1560, 96, 96, PixelFormats.Gray8, null);
            _bitmap_520 = new WriteableBitmap(TotalLines, AmbientDataSize_520, 96, 96, PixelFormats.Gray8, null);

            ImageCanvas_1560.Background = new ImageBrush(_bitmap_1560);
            ImageCanvas_520.Background = new ImageBrush(_bitmap_520);
        } // InitializeBitmap

        public ulong maxValue = 0;

        public void UpdateBitmap()
        {
            int AmbientDataSize = (CurrentTab == 1560) ? AmbientDataSize_1560 : AmbientDataSize_520;
            byte[] grayData = new byte[TotalLines * AmbientDataSize];
            for (int i = 0; i < TotalLines; i++)
            {
                if (_receivedPacketFlags[i])
                {
                    for (int j = 0; j < AmbientDataSize; j++)
                    {
                        byte r = _receivedPackets[i][j * 3];
                        byte g = _receivedPackets[i][j * 3 + 1];
                        byte b = _receivedPackets[i][j * 3 + 2];
                        // Combine 3 bytes to form an unsigned int value
                        ulong value = (ulong)((b << 16) | (g << 8) | r);
                        if (value > maxValue) // for debug
                        {
                            maxValue = value;
                        } // if

                        //Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"({i},{j}):{b:X2}-{g:X2}-{r:X2} == {value}\n"));
                        // NewValue = (((OldValue - OldMin) * (NewMax - NewMin)) / (OldMax - OldMin)) + NewMin
                        //value = (ulong)((((value - 0) * (255 - 0)) / (4194304 - 0) + 0));
                        value = (ulong)((((value - 0) * (255 - 0)) / (2048 - 0) + 0));

                        value = Math.Min(value, 255); // Ensure value does not exceed 255
                        grayData[j * TotalLines + i] = Convert.ToByte(value);
                        //grayData[j * TotalLines + i] = (byte)((r + g + b) / 3);
                    } // for
                } // if
            } // for

            Dispatcher.Invoke(() => DebugWindow.Instance.DataTextBox.AppendText($"({maxValue})\n"));

            _bitmap_1560.WritePixels(new Int32Rect(0, 0, TotalLines, AmbientDataSize), grayData, TotalLines, 0);

            CalculateCentroids();
            DrawGraphs();

            // Update each EnlargedSegmentWindow
            foreach (var window in _enlargedSegmentWindows.Values)
            {
                window.UpdateImage(_bitmap_1560);
            } // foreach

            FlashGreenLight(); // Flash the green light
        } // UpdateBitmap

        public void UpdateBitmapV2()
        {
            int AmbientDataSize = (CurrentTab == 1560) ? AmbientDataSize_1560 : AmbientDataSize_520;
            byte[] grayData = new byte[TotalLines * AmbientDataSize];
            for (int i = 0; i < TotalLines; i++)
            {
                if (_receivedPacketFlags[i])
                {
                    for (int j = 0; j < AmbientDataSize; j++)
                    {
                        // Combine two bytes to form a 16-bit value
                        ushort value = (ushort)(_receivedPackets[i][j * 2] << 8 | _receivedPackets[i][j * 2 + 1]);
                        // What should we do with the value????
                        // Convert the 16-bit value to grayscale
                        grayData[j * TotalLines + i] = (byte)(value >> 8);
                    }
                }
            } // 

            _bitmap_520.WritePixels(new Int32Rect(0, 0, TotalLines, AmbientDataSize), grayData, TotalLines, 0);
        } // UpdateBitmapV2

        protected override void OnClosed(EventArgs e)
        {
            _serialPort?.Close();
            base.OnClosed(e);
        } // OnClosed

        public void ResetData()
        {
            _lastPsn = -1; // Reset the last PSN value
            _currentLine = 0;
            // Save the data before resetting
            Array.Copy(_receivedPackets, _receivedPackets_save, _receivedPackets.Length);
            Array.Copy(_receivedPacketFlags, _receivedPacketFlags_save, _receivedPacketFlags.Length);
            Array.Clear(_receivedPackets, 0, _receivedPackets.Length);
            Array.Clear(_receivedPacketFlags, 0, _receivedPacketFlags.Length);
            // InitializeBitmap(); // Reinitialize the bitmap
        } // ResetData


        // ------------------------------------------ Calculation of the Centroids ---------------------------------
        // theta angle (top segment centroid pos & bottom centroid connected line) = atan2(y, x) * 180 / PI
        public void CalculateCentroids()
        {
            int topBottomSegmentHeight = 312;
            int middleSegmentHeight = 312;
            int dontCareHeight = 312;
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

                double sumX = 0;
                double sumY = 0;
                double sumValue = 0;

                _currentFrameMaxCoordinates[segment] = new Point();
                _currentFrameMaxValues[segment] = 0;

                for (int y = startY; y < endY; y++)
                {
                    for (int x = 0; x < TotalLines; x++)
                    {
                        int index = y * TotalLines + x;
                        byte[] packet = _receivedPackets[x];
                        if (packet != null)
                        {
                            int packetIndex = y * 3;
                            byte r = packet[packetIndex];
                            byte g = packet[packetIndex + 1];
                            byte b = packet[packetIndex + 2];
                            // Combine 3 bytes to form an unsigned int value
                            ulong value = (ulong)((b << 16) | (g << 8) | r);

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

                            // Update the current frame max value coordinate
                            if (value > _currentFrameMaxValues[segment])
                            {
                                _currentFrameMaxValues[segment] = value;
                                _currentFrameMaxCoordinates[segment] = new Point(x, y);
                            } // if
                        } // if
                    } // for
                } // for

                double centroidX = sumX / sumValue;
                double centroidY = sumY / sumValue;

                // Store the original centroid coordinates
                originalCentroids.Add(new Point(centroidX + (TotalLines / 2.0), centroidY + (startY + segmentHeight / 2.0)));

                // Adjust coordinates to have (0,0) at the center of the segment
                centroids.Add(new Point(centroidX, centroidY));

                // Calculate D4-sigma for the segment
                double d4SigmaX, d4SigmaY;
                CalculateD4Sigma(startY, endY, centroidX, centroidY, out d4SigmaX, out d4SigmaY);

                // Update the all-time max value coordinate if necessary
                if (_currentFrameMaxValues[segment] > _allTimeMaxValue[segment])
                {
                    _allTimeMaxValue[segment] = _currentFrameMaxValues[segment];
                    _allTimeMaxCoordinates[segment] = _currentFrameMaxCoordinates[segment];
                } // if

                // Output the D4-sigma values
                Dispatcher.Invoke(() =>
                {
                    DebugWindow.Instance.DataTextBox.AppendText($"D4-sigma for segment {segment + 1}: σx = {d4SigmaX:F2}, σy = {d4SigmaY:F2}\n");
                    switch (segment)
                    {
                        case 0:
                            D4SigmaTextBlock1.Text = $"D4σx = {d4SigmaX:F2}\nD4σy = {d4SigmaY:F2}";
                            CoordinateMaxTextBlock1.Text = $"All-time Max: {_allTimeMaxValue[segment]}";
                            CoordinateCurrentTextBlock1.Text = $"Current Max: {_currentFrameMaxValues[segment]}";
                            break;
                        case 1:
                            D4SigmaTextBlock2.Text = $"D4σx = {d4SigmaX:F2}\nD4σy = {d4SigmaY:F2}";
                            CoordinateMaxTextBlock2.Text = $"All-time Max: {_allTimeMaxValue[segment]}";
                            CoordinateCurrentTextBlock2.Text = $"Current Max: {_currentFrameMaxValues[segment]}";
                            break;
                        case 2:
                            D4SigmaTextBlock3.Text = $"D4σx = {d4SigmaX:F2}\nD4σy = {d4SigmaY:F2}";
                            CoordinateMaxTextBlock3.Text = $"All-time Max: {_allTimeMaxValue[segment]}";
                            CoordinateCurrentTextBlock3.Text = $"Current Max: {_currentFrameMaxValues[segment]}";
                            break;
                    }
                });
            } // for

            // Calculate the θ angle between the top and bottom segment centroids
            double theta = Math.Atan2(centroids[2].Y - centroids[0].Y, centroids[2].X - centroids[0].X) * 180 / Math.PI;

            // Output the centroids
            Dispatcher.Invoke(() =>
            {
                for (int i = 0; i < centroids.Count; i++)
                {
                    DebugWindow.Instance.DataTextBox.AppendText($"Centroid of segment {i + 1}: ({centroids[i].X}, {centroids[i].Y})\n");
                } // for
            });

            // Format the centroid positions for display
            string centroidPositions1 = $"Centroid A ({centroids[0].X:F2}, {centroids[0].Y:F2})";
            string centroidPositions2 = $"Centroid B ({centroids[1].X:F2}, {centroids[1].Y:F2})";
            string centroidPositions3 = $"Centroid C ({centroids[2].X:F2}, {centroids[2].Y:F2})";
            string thetaAngle = $"θ = {theta:F2}°";

            // Update the CoordinateDataTextBlock with the formatted text
            Dispatcher.Invoke(() =>
            {
                CoordinateDataTextBlock1.Text = centroidPositions1;
                CoordinateDataTextBlock2.Text = centroidPositions2;
                CoordinateDataTextBlock3.Text = centroidPositions3;
                ThetaAngleTextBlock.Text = thetaAngle;
            });

            // copy the centroid positions to the segment window
            centroid_for_segmentWindow = originalCentroids;

            // Draw the centroids and segment boundaries on the bitmap using original coordinates
            DrawAxesAndCentroids(originalCentroids, topBottomSegmentHeight, middleSegmentHeight, dontCareHeight);
        } // CalculateCentroids

        public void CalculateCentroidsOneSet()
        {
            int imageHeight = (CurrentTab == 1560) ? AmbientDataSize_1560 : AmbientDataSize_520;
            double sumX = 0;
            double sumY = 0;
            double sumValue = 0;

            for (int y = 0; y < imageHeight; y++)
            {
                for (int x = 0; x < TotalLines; x++)
                {
                    int index = y * TotalLines + x;
                    byte[] packet = _receivedPackets[x];
                    if (packet != null)
                    {
                        int packetIndex = (CurrentTab == 1560) ? y * 3 : y * 2;
                        int value = (CurrentTab == 1560)
                            ? (packet[packetIndex] << 16) | (packet[packetIndex + 1] << 8) | packet[packetIndex + 2]
                            : (packet[packetIndex] << 8) | packet[packetIndex + 1];
                        if (value == 0)
                        {
                            value = 1; // Avoid division by zero
                        }
                        // Adjust x and y to have (0,0) at the center of the image
                        double adjustedX = x - (TotalLines / 2.0);
                        double adjustedY = y - (imageHeight / 2.0);

                        sumX += adjustedX * value;
                        sumY += adjustedY * value;
                        sumValue += value;
                    }
                }
            }

            double centroidX = sumX / sumValue;
            double centroidY = sumY / sumValue;

            // Adjust coordinates to have (0,0) at the center of the image
            Point centroid = new Point(centroidX, centroidY);

            // Output the centroid
            Dispatcher.Invoke(() =>
            {
                DebugWindow.Instance.DataTextBox.AppendText($"Centroid of the whole image: ({centroid.X}, {centroid.Y})\n");
            });

            // Format the centroid position for display
            string centroidPosition = $"Centroid ({centroid.X:F2}, {centroid.Y:F2})";

            // Update the CoordinateDataTextBlock with the formatted text
            Dispatcher.Invoke(() =>
            {
                CoordinateDataTextBlock1.Text = centroidPosition;
            });

            // Draw the centroid on the bitmap
            DrawAxesAndCentroidOneSet(centroid, imageHeight);
        } // CalculateCentroidsOneSet

        private void CalculateD4Sigma(int startY, int endY, double centroidX, double centroidY, out double d4SigmaX, out double d4SigmaY)
        {
            double sumXX = 0;
            double sumYY = 0;
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
                        byte r = packet[packetIndex];
                        byte g = packet[packetIndex + 1];
                        byte b = packet[packetIndex + 2];
                        // Combine 3 bytes to form an unsigned int value
                        ulong value = (ulong)((b << 16) | (g << 8) | r);

                        if (value == 0)
                        {
                            value = 1; // Avoid division by zero
                        } // if
                          // Adjust x and y to have (0,0) at the center of the segment
                        double adjustedX = x - (TotalLines / 2.0);
                        double adjustedY = y - (startY + (endY - startY) / 2.0);

                        sumXX += (adjustedX - centroidX) * (adjustedX - centroidX) * value;
                        sumYY += (adjustedY - centroidY) * (adjustedY - centroidY) * value;
                        sumValue += value;
                    } // if
                } // for
            } // for

            d4SigmaX = 4 * Math.Sqrt(sumXX / sumValue);
            d4SigmaY = 4 * Math.Sqrt(sumYY / sumValue);
        } // CalculateD4Sigma

        public void DrawAxesAndCentroids(List<Point> centroids, int topBottomSegmentHeight, int middleSegmentHeight, int dontCareHeight)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                int width = (CurrentTab == 1560) ? _bitmap_1560.PixelWidth : _bitmap_520.PixelWidth;
                int height = (CurrentTab == 1560) ? _bitmap_1560.PixelHeight : _bitmap_520.PixelHeight;

                if (IsCheckboxChecked)
                {
                    // Draw axes for the top segment
                    int topSegmentMiddleY = topBottomSegmentHeight / 2;
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, topSegmentMiddleY), new Point(width, topSegmentMiddleY));
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, 0), new Point(width / 2, topBottomSegmentHeight));

                    // Draw axes for the middle segment
                    int middleSegmentStartY = topBottomSegmentHeight + dontCareHeight;
                    int middleSegmentMiddleY = middleSegmentStartY + middleSegmentHeight / 2;
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, middleSegmentMiddleY), new Point(width, middleSegmentMiddleY));
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, middleSegmentStartY), new Point(width / 2, middleSegmentStartY + middleSegmentHeight));

                    // Draw axes for the bottom segment
                    int bottomSegmentStartY = 2 * (topBottomSegmentHeight + dontCareHeight);
                    int bottomSegmentMiddleY = bottomSegmentStartY + topBottomSegmentHeight / 2;
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, bottomSegmentMiddleY), new Point(width, bottomSegmentMiddleY));
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, bottomSegmentStartY), new Point(width / 2, bottomSegmentStartY + topBottomSegmentHeight));
                } // if

                // Draw segment boundaries
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, 0, TotalLines, topBottomSegmentHeight));
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, topBottomSegmentHeight + dontCareHeight, TotalLines, middleSegmentHeight));
                context.DrawRectangle(null, new Pen(Brushes.Green, 1), new Rect(0, 2 * (topBottomSegmentHeight + dontCareHeight), TotalLines, topBottomSegmentHeight));

                // Draw the centroids
                foreach (var centroid in centroids)
                {
                    context.DrawEllipse(Brushes.Red, null, centroid, 5, 5);
                } // foreach
            } // using

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(_bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Combine the axes and centroids with the existing bitmap
            DrawingVisual combinedVisual = new DrawingVisual();
            using (DrawingContext context = combinedVisual.RenderOpen())
            {
                context.DrawImage(_bitmap_1560, new Rect(0, 0, _bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight));
                context.DrawImage(renderBitmap, new Rect(0, 0, _bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight));
            }

            RenderTargetBitmap combinedBitmap = new RenderTargetBitmap(_bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            combinedBitmap.Render(combinedVisual);

            // Update the ImageCanvas with the combined image
            ImageCanvas_1560.Background = new ImageBrush(combinedBitmap);
        } // DrawAxesAndCentroids

        public void DrawAxesAndCentroidOneSet(Point centroid, int imageHeight)
        {
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                int width = (CurrentTab == 1560) ? _bitmap_1560.PixelWidth : _bitmap_520.PixelWidth;
                int height = (CurrentTab == 1560) ? _bitmap_1560.PixelHeight : _bitmap_520.PixelHeight;

                if (IsCheckboxChecked)
                {
                    // Draw axes for the whole image
                    int middleY = imageHeight / 2;
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, middleY), new Point(width, middleY));
                    context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, 0), new Point(width / 2, imageHeight));
                }

                // Draw the centroid
                context.DrawEllipse(Brushes.Red, null, centroid, 5, 5);
            }

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(_bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Combine the axes and centroid with the existing bitmap
            DrawingVisual combinedVisual = new DrawingVisual();
            using (DrawingContext context = combinedVisual.RenderOpen())
            {
                context.DrawImage(_bitmap_1560, new Rect(0, 0, _bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight));
                context.DrawImage(renderBitmap, new Rect(0, 0, _bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight));
            }

            RenderTargetBitmap combinedBitmap = new RenderTargetBitmap(_bitmap_1560.PixelWidth, _bitmap_1560.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            combinedBitmap.Render(combinedVisual);

            // Update the ImageCanvas with the combined image
            ImageCanvas_1560.Background = new ImageBrush(combinedBitmap);
        } // DrawAxesAndCentroidOneSet

        public void DrawGraphs()
        {
            DrawXAxisGraph(0, XAxisGraphCanvas1);
            DrawYAxisGraph(0, YAxisGraphCanvas1);
            DrawXAxisGraph(1, XAxisGraphCanvas2);
            DrawYAxisGraph(1, YAxisGraphCanvas2);
            DrawXAxisGraph(2, XAxisGraphCanvas3);
            DrawYAxisGraph(2, YAxisGraphCanvas3);
        } // DrawGraphs

        private void DrawXAxisGraph(int segment, Canvas canvas)
        {
            int segmentHeight = 312;
            int dontCareHeight = 312;
            int startY = segment * (segmentHeight + dontCareHeight);
            if (segment == 1)
            {
                startY = segmentHeight + dontCareHeight;
            } // if

            double[] xSums = new double[TotalLines];
            for (int x = 0; x < TotalLines; x++)
            {
                double sum = 0;
                for (int y = startY; y < startY + segmentHeight; y++)
                {
                    int index = y * TotalLines + x;
                    byte[] packet = _receivedPackets[x];
                    if (packet != null)
                    {
                        int packetIndex = y * 3;
                        int value = (packet[packetIndex] << 16) | (packet[packetIndex + 1] << 8) | packet[packetIndex + 2];
                        sum += value;
                    } // if
                } // for
                xSums[x] = sum;
            } // for

            double maxSum = xSums.Max();
            double scale = canvas.Height / maxSum;
            if (maxSum == 0)
            {
                scale = 1;
            } // if

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawLine(new Pen(Brushes.White, 2), new Point(0, canvas.Height / 2), new Point(canvas.Width, canvas.Height / 2));
                context.DrawLine(new Pen(Brushes.White, 2), new Point(canvas.Width / 2, 35), new Point(canvas.Width / 2, 65));

                PointCollection points = new PointCollection();
                for (int x = 0; x < TotalLines; x++)
                {
                    double y = (canvas.Height - (xSums[x] * scale)) / 2;
                    context.DrawLine(new Pen(Brushes.Green, 1), new Point(x, canvas.Height / 2), new Point(x, y));
                    points.Add(new Point(x, y));
                } // for

                // Draw the curve line using the highest points
                if (points.Count > 1)
                {
                    PathFigure pathFigure = new PathFigure { StartPoint = points[0] };
                    PolyBezierSegment bezierSegment = new PolyBezierSegment(points, true);
                    pathFigure.Segments.Add(bezierSegment);
                    PathGeometry pathGeometry = new PathGeometry();
                    pathGeometry.Figures.Add(pathFigure);
                    context.DrawGeometry(null, new Pen(Brushes.Green, 2), pathGeometry);
                } // if
            } // using

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);
            canvas.Background = new ImageBrush(renderBitmap);
        } // DrawXAxisGraph

        private void DrawYAxisGraph(int segment, Canvas canvas)
        {
            int segmentHeight = 312;
            int dontCareHeight = 312;
            int startY = segment * (segmentHeight + dontCareHeight);
            if (segment == 1)
            {
                startY = segmentHeight + dontCareHeight;
            }

            double[] ySums = new double[segmentHeight];
            for (int y = startY; y < startY + segmentHeight; y++)
            {
                double sum = 0;
                for (int x = 0; x < TotalLines; x++)
                {
                    int index = y * TotalLines + x;
                    byte[] packet = _receivedPackets[x];
                    if (packet != null)
                    {
                        int packetIndex = y * 3;
                        int value = (packet[packetIndex] << 16) | (packet[packetIndex + 1] << 8) | packet[packetIndex + 2];
                        sum += value;
                    }
                }
                ySums[y - startY] = sum;
            } // for

            double maxSum = ySums.Max();
            double scale = canvas.Width / maxSum;
            if (maxSum == 0)
            {
                scale = 1;
            } // if

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                context.DrawLine(new Pen(Brushes.White, 2), new Point(canvas.Width / 2, 0), new Point(canvas.Width / 2, canvas.Height));
                context.DrawLine(new Pen(Brushes.White, 2), new Point(35, canvas.Height / 2), new Point(65, canvas.Height / 2));

                PointCollection points = new PointCollection();
                for (int y = 0; y < segmentHeight; y++)
                {
                    double x = (canvas.Width - (ySums[y] * scale)) / 2;
                    if (x != (canvas.Width / 2))
                    {
                        x = (canvas.Width - (ySums[y] * scale)) / 2 + (canvas.Width / 2);
                    } // if

                    context.DrawLine(new Pen(Brushes.Green, 1), new Point(canvas.Width / 2, y), new Point(x, y));
                    points.Add(new Point(x, y));
                } // for

                // Draw the curve line using the rightest points
                if (points.Count > 1)
                {
                    PathFigure pathFigure = new PathFigure { StartPoint = points[0] };
                    PolyBezierSegment bezierSegment = new PolyBezierSegment(points, true);
                    pathFigure.Segments.Add(bezierSegment);
                    PathGeometry pathGeometry = new PathGeometry();
                    pathGeometry.Figures.Add(pathFigure);
                    context.DrawGeometry(null, new Pen(Brushes.Green, 2), pathGeometry);
                } // if
            } // using

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap((int)canvas.Width, (int)canvas.Height, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);
            canvas.Background = new ImageBrush(renderBitmap);
        } // DrawYAxisGraph

        // --------------------------------- Mouse Events -------------------------------------
        private void ImageCanvas_1560_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            Point clickPosition = e.GetPosition(ImageCanvas_1560);
            double centerX = ImageCanvas_1560.ActualWidth / 2;
            double centerY = ImageCanvas_1560.ActualHeight / 2;
            double adjustedX = clickPosition.X - centerX;
            double adjustedY = centerY - clickPosition.Y; // Make y negative when clicking the lower half
            //ClickPositionTextBlock_1560.Text = $"Click Position: ({adjustedX:F0}, {adjustedY:F0})";

            int segmentHeight = 312;
            int dontCareHeight = 312;

            int segmentIndex = (int)(clickPosition.Y / (segmentHeight + dontCareHeight));
            if (segmentIndex == 1)
            {
                segmentIndex = 1;
            } // if
            else if (clickPosition.Y > segmentHeight + dontCareHeight)
            {
                segmentIndex = 2;
            } // elese if
            else
            {
                segmentIndex = 0;
            } // else

            ShowEnlargedSegment(segmentIndex);
        } // ImageCanvas_1560_MouseLeftButtonDown

        private void ImageCanvas_520_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // 520 ver does not have segments, we enlarge the whole image
            Point clickPosition = e.GetPosition(ImageCanvas_520);
            double centerX = ImageCanvas_520.ActualWidth / 2;
            double centerY = ImageCanvas_520.ActualHeight / 2;
            double adjustedX = clickPosition.X - centerX;
            double adjustedY = centerY - clickPosition.Y; // Make y negative when clicking the lower half
            //ClickPositionTextBlock_520.Text = $"Click Position: ({adjustedX:F0}, {adjustedY:F0})";

            ShowEnlargedSegment(0);
        } // ImageCanvas_520_MouseLeftButtonDown

        private void ImageCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point position = e.GetPosition((IInputElement)sender);
            double centerX = ((Canvas)sender).ActualWidth / 2;
            double centerY = ((Canvas)sender).ActualHeight / 2;
            double adjustedX = position.X - centerX;
            double adjustedY = centerY - position.Y; // Make y negative when hovering over the lower half

            if (CurrentTab == 1560)
            {
                HoverPositionTextBlock_1560.Text = $"Hover Position: ({adjustedX:F0}, {adjustedY:F0})";
            } // if
            else
            {
                HoverPositionTextBlock_520.Text = $"Hover Position: ({adjustedX:F0}, {adjustedY:F0})";
            } // else

            DrawCross(position);
        } // ImageCanvas_MouseMove

        private void ImageCanvas_MouseLeave(object sender, MouseEventArgs e)
        {
            ClearCross();

            if (CurrentTab == 1560)
            {
                HoverPositionTextBlock_1560.Text = $"Hover Position: (0, 0)";
            } // if
            else
            {
                HoverPositionTextBlock_520.Text = $"Hover Position: (0, 0)";
            } // else
        } // ImageCanvas_MouseLeave

        private void DrawCross(Point position)
        {
            int width = (CurrentTab == 1560) ? _bitmap_1560.PixelWidth : _bitmap_520.PixelWidth;
            int height = (CurrentTab == 1560) ? _bitmap_1560.PixelHeight : _bitmap_520.PixelHeight;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                // Draw the existing image
                if (CurrentTab == 1560)
                {
                    context.DrawImage(_bitmap_1560, new Rect(0, 0, width, height));
                } // if
                else
                {
                    context.DrawImage(_bitmap_520, new Rect(0, 0, width, height));
                } // else

                // Draw the cross
                context.DrawLine(new Pen(Brushes.BlueViolet, 0.7), new Point(0, position.Y), new Point(width, position.Y));
                context.DrawLine(new Pen(Brushes.BlueViolet, 0.7), new Point(position.X, 0), new Point(position.X, height));
            } // using

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Update the ImageCanvas with the combined image
            if (CurrentTab == 1560)
            {
                ImageCanvas_1560.Background = new ImageBrush(renderBitmap);
            } // if
            else
            {
                ImageCanvas_520.Background = new ImageBrush(renderBitmap);
            } // else
        } // DrawCross

        private void ClearCross()
        {
            int width = (CurrentTab == 1560) ? _bitmap_1560.PixelWidth : _bitmap_520.PixelWidth;
            int height = (CurrentTab == 1560) ? _bitmap_1560.PixelHeight : _bitmap_520.PixelHeight;

            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                // Draw the existing image
                if (CurrentTab == 1560)
                {
                    context.DrawImage(_bitmap_1560, new Rect(0, 0, width, height));
                }
                else
                {
                    context.DrawImage(_bitmap_520, new Rect(0, 0, width, height));
                }
            }

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Update the ImageCanvas with the combined image
            if (CurrentTab == 1560)
            {
                ImageCanvas_1560.Background = new ImageBrush(renderBitmap);
            } // if
            else
            {
                ImageCanvas_520.Background = new ImageBrush(renderBitmap);
            } // else
        } // ClearCross

        private void ShowEnlargedSegment(int segmentIndex)
        {
            if (!_enlargedSegmentWindows.ContainsKey(segmentIndex))
            {
                var enlargedSegmentWindow = new EnlargedSegmentWindow();
                enlargedSegmentWindow.Closed += (sender, e) => _enlargedSegmentWindows.Remove(segmentIndex);
                _enlargedSegmentWindows[segmentIndex] = enlargedSegmentWindow;
                enlargedSegmentWindow.Show();
            }

            var window = _enlargedSegmentWindows[segmentIndex];
            window.Title = $"Segment {segmentIndex + 1}";
            window.segmentIndex = segmentIndex;
            if (CurrentTab == 1560)
                window.UpdateImage(_bitmap_1560);
            else
                window.UpdateImage(_bitmap_520);
        } // ShowEnlargedSegment

        private async void FlashGreenLight()
        {
            GreenLight.Visibility = Visibility.Visible;
            await Task.Delay(700); // Flash duration
            GreenLight.Visibility = Visibility.Collapsed;
        }
    } // class MainWindow
} // namespace UsbApp
