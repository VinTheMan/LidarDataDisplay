using System;
using System.Windows;
using System.Windows.Media.Imaging;

namespace UsbApp
{
    public partial class EnlargedSegmentWindow : Window
    {
        public EnlargedSegmentWindow()
        {
            InitializeComponent();
            this.Closed += EnlargedSegmentWindow_Closed;
        }

        private void EnlargedSegmentWindow_Closed(object sender, EventArgs e)
        {
            // Handle the window closed event
            ((MainWindow)Application.Current.MainWindow)._enlargedSegmentWindow = null;
        }

        public void UpdateImage(WriteableBitmap segmentBitmap, string title)
        {
            EnlargedImage.Source = segmentBitmap;
            this.Title = title;
        }
    }
}
