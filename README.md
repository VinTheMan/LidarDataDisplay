# AA Data Display

## Overview
This is a .NET Framework 4.8 application designed to simulate and display data packets when AA.<br/>
The application supports two UDP packet formats from RX-AA and BS-AA.

## Features
- Simulate data packets.
- Display data in real-time when AA.
- Save logs and raw data.
- Calculate and generate image, centroids etc.

## Installation
1. Clone the repository:
	```bash
	git clone https://github.com/VinTheMan/LidarDataDisplay.git
	```
2. Open the solution in Visual Studio 2022.
3. Build the solution to restore the necessary NuGet packages.

## Usage
0. Make sure the directory of exe file has `Python311/` folder and the app is running within the built folder.<br/>
	The structure should look like this:
	```bash
	Release/
	├── Python311/
	│   └── ... python package files
	├── LidarDataDisplay.exe
	│  
	│  ... .dll files, etc.
	│  
	└── README.md
	```
1. Run the application inside the folder.
2. Use the "Start Simulation" button on the debug window to begin simulating UDP packets.
3. Use the "Clear Text" button to clear the displayed data.
4. Click on specific segments of result image to enlarge and analyze them.

## Before Diving into Code Structure<br/>You should keep in mind the following packet structures:
### UDP Packet Structure when RX-AA (1560 x 105)
![Current UDP packet format 20250219-RXAA](https://github.com/user-attachments/assets/4950e0f3-943c-42a0-b350-5f8eb25c50da)
### UDP Packet Structure when BS-AA (520 x 105)
![Current UDP packet format 20250219-BSAA](https://github.com/user-attachments/assets/d31dec50-988e-45e4-ab79-a77f19ff0d5e)

## Code Structure
### Main Components
- **DebugWindow.xaml.cs**: Handles the main simulation and display logic.
- **EnlargedSegmentWindow.xaml.cs**: Handles the logic for enlarging and analyzing specific segments of the data.
- **AssemblyInfo.cs**: Contains metadata about the assembly.

### Key Methods
- `MainWindow()`
The constructor initializes the main window, sets up the data context, initializes the bitmap, and opens the debug window. It also sets up the Python environment for further processing.

- `OnPropertyChanged([CallerMemberName] string name = null)`
Notifies the UI about property changes to update the binding.

- `MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)`
Handles the event when the selected tab changes (format change between 1560 & 520). It updates the current tab, stops listening for UDP packets, and resets the data.

- `SaveImageButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Save Image" button is clicked. It opens a file dialog to save the current result image as a PNG file.

- `SaveCsvButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Save CSV" button is clicked. It opens a file dialog to save the current raw value data as a CSV file.

- `StartListeningButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Start Listening" button is clicked. It starts or stops listening for UDP packets and updates the button text accordingly.

- `ListenForUdpPackets()`
Asynchronously listens for incoming UDP packets and processes them. Called when "Start Listening" button is clicked.

- `ParseUdpPacket(byte[] data)`<br/>
Parses the received UDP packet and updates the bitmap with the parsed data.<br/>
I use bitmask to keep track of the four packets when processing the 1560 format.<br/><br/>
`_receivedPacketFlagsDict[psn] == 0x0F`<br/>
🔍means all four packets are received. ( 0x0F == 1111 )<br/><br/>
`_receivedPacketFlagsDict[psn] |= (1 << udpNumber)`<br/>		
`1 << udpNumber`<br/>
🔍This operation shifts the number 1 to the left by udpNumber positions. This creates a bitmask where only the bit corresponding to udpNumber is set to 1.<br/>
`|=`<br/>
🔍This is the bitwise OR assignment operator. It updates the value of _receivedPacketFlagsDict[psn] by performing a bitwise OR with the current value and the bitmask created by `1 << udpNumber`.<br/>

- `CalculateChecksum(byte[] data, int length)`
Calculates the checksum for the given data. Currently not in use due to the packets do not have correct checksums (Mar 21, 2025) 

- `InitializeBitmap()`
Initializes the bitmap for displaying the data. Make it so the UI starts with a pure black image ready to be painted.

- `UpdateBitmap()`
Updates the bitmap for the 1560 format with the received data and calculates centroids.

- `UpdateBitmapV2()`
Updates the bitmap for the 520 format with the received data and calculates centroids.

- `ResetData()`
Resets the data buffers and flags for the next frame. Also temp save the data in case user clicks the "Save Data" button.

- `CalculateCentroids()`
Calculates the centroids for the 3 segments in the 1560 format and updates the UI with the calculated values.<br/>


- `CalculateCentroidsOneSet()`
Calculates the centroid for the entire image in the 520 format and updates the UI with the calculated values.

- `DrawAxesAndCentroids(List<Point> centroids, int topBottomSegmentHeight, int middleSegmentHeight, int dontCareHeight)`
Draws the axes and centroids on the bitmap for the 1560 format.

- `DrawAxesAndCentroidOneSet(Point centroid, int imageHeight)`
Draws the axes and centroid on the bitmap for the 520 format.

- `DrawGraphs()`
Draws the X and Y axis graphs for the 1560 format. Called by UpdateBitmap().

- `DrawGraphsOneSet()`
Draws the X and Y axis graphs for the 520 format. Called by UpdateBitmapV2().

- `ImageCanvas_1560_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)`
Handles the event when the left mouse button is clicked on the 1560 image canvas. It shows the enlarged segment window for the clicked segment.

- `ImageCanvas_520_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)`
Handles the event when the left mouse button is clicked on the 520 image canvas. It shows the enlarged segment window for the entire image.

- `ImageCanvas_MouseMove(object sender, MouseEventArgs e)`
Handles the event when the mouse moves over the image canvas. It updates the hover position and draws a cross at the current mouse position.

- `ImageCanvas_MouseLeave(object sender, MouseEventArgs e)`
Handles the event when the mouse leaves the image canvas. It clears the cross and resets the hover position.

- `ShowEnlargedSegment(int segmentIndex)`
Shows the enlarged segment window for the specified segment index.

## Contact
For any questions or suggestions, please contact [vincent911016@gmail.com](mailto:vincent911016@gmail.com).
