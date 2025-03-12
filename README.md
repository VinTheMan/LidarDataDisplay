# AA Data Display

## Overview
LidarDataDisplay is a .NET Framework 4.8 application designed to simulate and display data packets when AA.<br/>
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
0. Make sure the path of application's exe file has "Python311" folder and the app is running within the built folder.
The structure should look like this:
```
Release
¢u¢w¢w Python311
¢u¢w¢w LidarDataDisplay.exe
¢x  
¢x  ... .dll files, etc.
¢x  
¢|¢w¢w LidarDataDisplay.exe.config
```
1. Run the application inside the folder.
2. Use the "Start Simulation" button on the debug window to begin simulating UDP packets.
3. Use the "Clear Text" button to clear the displayed data.
4. Click on specific segments of result image to enlarge and analyze them.

## Before Diving into Code Structure<br/>You should keep in mind the following packet structures:
### UDP Packet Structure when RX-AA
![Current UDP packet format 20250219-RXAA](https://github.com/user-attachments/assets/4950e0f3-943c-42a0-b350-5f8eb25c50da)
### UDP Packet Structure when BS-AA
![Current UDP packet format 20250219-BSAA](https://github.com/user-attachments/assets/d31dec50-988e-45e4-ab79-a77f19ff0d5e)

## Code Structure
### Main Components
- **DebugWindow.xaml.cs**: Handles the main simulation and display logic.
- **EnlargedSegmentWindow.xaml.cs**: Handles the logic for enlarging and analyzing specific segments of the data.
- **AssemblyInfo.cs**: Contains metadata about the assembly.

### Key Methods
- `MainWindow()`
The constructor initializes the main window, sets up the data context, initializes the bitmap, and opens the debug window. It also sets up the Python environment for further processing.

- `MainWindow_Closed(object sender, EventArgs e)`
Handles the event when the main window is closed. It stops listening for UDP packets and shuts down the application.

- `OnPropertyChanged([CallerMemberName] string name = null)`
Notifies the UI about property changes to update the binding.

- `MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)`
Handles the event when the selected tab changes. It updates the current tab, stops listening for UDP packets, and resets the data.

- `SaveImageButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Save Image" button is clicked. It opens a file dialog to save the current image as a PNG file.

- `SaveCsvButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Save CSV" button is clicked. It opens a file dialog to save the current data as a CSV file.

- `StartListeningButton_Click(object sender, RoutedEventArgs e)`
Handles the event when the "Start Listening" button is clicked. It starts or stops listening for UDP packets and updates the button text accordingly.

- `StopListening()`
Stops listening for UDP packets and updates the UI to reflect the stopped state.

- `ListenForUdpPackets()`
Asynchronously listens for incoming UDP packets and processes them.

- `ParseUdpPacket(byte[] data)`
Parses the received UDP packet and updates the bitmap with the parsed data.

- `CalculateChecksum(byte[] data, int length)`
Calculates the checksum for the given data.

- `InitializeBitmap()`
Initializes the bitmap for displaying the data.

- `UpdateBitmap()`
Updates the bitmap with the received data and calculates centroids.

- `UpdateBitmapV2()`
Updates the bitmap for the 520 format with the received data and calculates centroids.

- `ResetData()`
Resets the data buffers and flags for the next frame.

- `CalculateCentroids()`
Calculates the centroids for the segments in the 1560 format and updates the UI with the calculated values.

- `CalculateCentroidsOneSet()`
Calculates the centroid for the entire image in the 520 format and updates the UI with the calculated values.

- `DrawAxesAndCentroids(List<Point> centroids, int topBottomSegmentHeight, int middleSegmentHeight, int dontCareHeight)`
Draws the axes and centroids on the bitmap for the 1560 format.

- `DrawAxesAndCentroidOneSet(Point centroid, int imageHeight)`
Draws the axes and centroid on the bitmap for the 520 format.

- `DrawGraphs()`
Draws the X and Y axis graphs for the 1560 format.

- `DrawGraphsOneSet()`
Draws the X and Y axis graphs for the 520 format.

- `ImageCanvas_1560_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)`
Handles the event when the left mouse button is clicked on the 1560 image canvas. It shows the enlarged segment window for the clicked segment.

- `ImageCanvas_520_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)`
Handles the event when the left mouse button is clicked on the 520 image canvas. It shows the enlarged segment window for the entire image.

- `ImageCanvas_MouseMove(object sender, MouseEventArgs e)`
Handles the event when the mouse moves over the image canvas. It updates the hover position and draws a cross at the current mouse position.

- `ImageCanvas_MouseLeave(object sender, MouseEventArgs e)`
Handles the event when the mouse leaves the image canvas. It clears the cross and resets the hover position.

- `maxValue_InputKeyDown(object sender, KeyEventArgs e)`
Handles the event when a key is pressed in the max value input box. It updates the old max value when the Enter key is pressed.

- `ShowEnlargedSegment(int segmentIndex)`
Shows the enlarged segment window for the specified segment index.

- `FlashGreenLight()`
Flashes the green light indicator for a short duration.

## Contact
For any questions or suggestions, please contact [vincent911016@gmail.com](mailto:vincent911016@gmail.com).