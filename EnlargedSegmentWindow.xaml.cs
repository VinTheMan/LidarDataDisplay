using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace UsbApp
{
    public partial class EnlargedSegmentWindow : Window
    {
        public int segmentIndex;
        public EnlargedSegmentWindow()
        {
            InitializeComponent();
            this.Closed += EnlargedSegmentWindow_Closed;
        }

        private void EnlargedSegmentWindow_Closed(object sender, EventArgs e)
        {
            // Handle the window closed event
            if (((MainWindow)Application.Current.MainWindow) != null)
                ((MainWindow)Application.Current.MainWindow)._enlargedSegmentWindow = null;
        } // EnlargedSegmentWindow_Closed

        public void UpdateImage(WriteableBitmap wholeBitmap)
        {
            int segmentHeight = 312;
            int dontCareHeight = 312;

            int startY = segmentIndex * (segmentHeight + dontCareHeight);
            if (segmentIndex == 1)
            {
                startY = segmentHeight + dontCareHeight;
            }

            // Create a cropped bitmap for the segment
            CroppedBitmap croppedBitmap = new CroppedBitmap(wholeBitmap, new Int32Rect(0, startY, wholeBitmap.PixelWidth, segmentHeight));

            // Create a DrawingVisual to draw the axes and centroids
            DrawingVisual visual = new DrawingVisual();
            using (DrawingContext context = visual.RenderOpen())
            {
                try
                {
                    int width = croppedBitmap.PixelWidth;
                    int height = croppedBitmap.PixelHeight;
                    MainWindow mainWindow = (MainWindow)Application.Current.MainWindow;

                    if (mainWindow.IsCheckboxChecked)
                    {
                        // Draw axes
                        context.DrawLine(new Pen(Brushes.Red, 1), new Point(0, height / 2), new Point(width, height / 2));
                        context.DrawLine(new Pen(Brushes.Red, 1), new Point(width / 2, 0), new Point(width / 2, height));
                    } // if

                    // Draw the centroids
                    Point centroid = mainWindow.centroid_for_segmentWindow[segmentIndex];
                    context.DrawEllipse(Brushes.Red, null, new Point(centroid.X, centroid.Y - startY), 5, 5);
                } // try
                catch (Exception e)
                {
                    Console.WriteLine(e.Message);
                } // catch
            } // using

            RenderTargetBitmap renderBitmap = new RenderTargetBitmap(croppedBitmap.PixelWidth, croppedBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            renderBitmap.Render(visual);

            // Combine the axes and centroids with the cropped bitmap
            DrawingVisual combinedVisual = new DrawingVisual();
            using (DrawingContext context = combinedVisual.RenderOpen())
            {
                context.DrawImage(croppedBitmap, new Rect(0, 0, croppedBitmap.PixelWidth, croppedBitmap.PixelHeight));
                context.DrawImage(renderBitmap, new Rect(0, 0, croppedBitmap.PixelWidth, croppedBitmap.PixelHeight));
            }

            RenderTargetBitmap combinedBitmap = new RenderTargetBitmap(croppedBitmap.PixelWidth, croppedBitmap.PixelHeight, 96, 96, PixelFormats.Pbgra32);
            combinedBitmap.Render(combinedVisual);

            // Display the combined bitmap in an Image control
            ImageSegment.Source = combinedBitmap;
        } // UpdateImage
    } // EnlargedSegmentWindow
} // UsbApp
