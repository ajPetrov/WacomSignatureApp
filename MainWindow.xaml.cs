using System.Windows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using wgssSTU;
using System.Drawing.Imaging;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace WacomSignatureApp
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Tablet tablet;
        private ICapability capability;
        private List<IPenData> penDataList = new List<IPenData>();
        private Bitmap signatureBitmap;
        private bool isHandlingInput = false;

        private System.Drawing.Rectangle submitButton;
        private System.Drawing.Rectangle clearButton;
        private System.Drawing.Point? _lastDrawnPoint = null;

        private Bitmap canvasBitmap;
        private Graphics canvasGraphics;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void CaptureSignature_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                UsbDevices usbDevices = new UsbDevices();
                if (usbDevices.Count == 0)
                {
                    MessageBox.Show("No Wacom STU devices found.");
                    return;
                }

                tablet = new Tablet();
                IUsbDevice usbDevice = usbDevices[0];
                tablet.usbConnect(usbDevice, false);

                capability = tablet.getCapability();
                penDataList.Clear();

                // Create canvas once
                canvasBitmap = new Bitmap(capability.screenWidth, capability.screenHeight, PixelFormat.Format32bppArgb);
                canvasGraphics = Graphics.FromImage(canvasBitmap);

                DrawBasePrompt();
                WriteCanvasToTablet();

                tablet.onPenData += new ITabletEvents2_onPenDataEventHandler((penData) =>
                {
                    penDataList.Add(penData);

                    if (isHandlingInput)
                        return;

                    int x = penData.x * capability.screenWidth / capability.tabletMaxX;
                    int y = penData.y * capability.screenHeight / capability.tabletMaxY;
                    var currentPoint = new System.Drawing.Point(x, y);

                    if (penData.sw != 0)
                    {
                        if (_lastDrawnPoint.HasValue)
                        {
                            canvasGraphics.DrawLine(Pens.Black, _lastDrawnPoint.Value, currentPoint);
                        }
                        _lastDrawnPoint = currentPoint;
                    }
                    else
                    {
                        _lastDrawnPoint = null;
                        WriteCanvasToTablet();

                        if (penDataList.Count > 1)
                        {
                            var last = penDataList[penDataList.Count - 2];
                            int lx = penData.x * capability.screenWidth / capability.tabletMaxX;
                            int ly = penData.y * capability.screenHeight / capability.tabletMaxY;
                            var tapPoint = new System.Drawing.Point(lx, ly);

                            if (submitButton.Contains(tapPoint))
                            {
                                isHandlingInput = true;
                                SubmitSignature();
                            }
                            else if (clearButton.Contains(tapPoint))
                            {
                                isHandlingInput = true;
                                ClearSignature();
                            }
                        }
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        private void DrawBasePrompt()
        {
            canvasGraphics.Clear(Color.White);
            canvasGraphics.DrawString("Please sign below", new Font("Arial", 10), Brushes.Black, 5, 5);
            canvasGraphics.DrawRectangle(Pens.Black, 0, 0, canvasBitmap.Width - 1, canvasBitmap.Height - 1);

            clearButton = new System.Drawing.Rectangle(10, canvasBitmap.Height - 40, 90, 30);
            submitButton = new System.Drawing.Rectangle(canvasBitmap.Width - 100, canvasBitmap.Height - 40, 90, 30);

            canvasGraphics.FillRectangle(Brushes.LightGray, clearButton);
            canvasGraphics.DrawRectangle(Pens.Black, clearButton);
            canvasGraphics.DrawString("Clear", new Font("Arial", 8), Brushes.Black, clearButton.Location);

            canvasGraphics.FillRectangle(Brushes.LightGray, submitButton);
            canvasGraphics.DrawRectangle(Pens.Black, submitButton);
            canvasGraphics.DrawString("Submit", new Font("Arial", 8), Brushes.Black, submitButton.Location);
        }

        private void WriteCanvasToTablet()
        {
            try
            {
                byte[] monoData;

                try
                {
                    monoData = ConvertTo1BitBitmap(canvasBitmap);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("ConvertTo1BitBitmap FAILED:\n\n" + ex.GetType().Name + ": " + ex.Message);
                    return;
                }

                Array imageArray = monoData;
                tablet.writeImage(0, imageArray);

                SignatureImage.Source = BitmapToImageSource((Bitmap)canvasBitmap.Clone());
            }
            catch (Exception ex)
            {
                MessageBox.Show("WriteCanvasToTablet FAILED:\n\n" + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private void SubmitSignature()
        {
            Dispatcher.BeginInvoke(new Action(async () =>
            {
                tablet.setInkingMode(0);
                tablet.writeImage(0, new byte[capability.screenWidth * capability.screenHeight / 8]);
                tablet.disconnect();
                RenderSignature();

                await Task.Delay(300);
                isHandlingInput = false;
            }));
        }

        private async void ClearSignature()
        {
            if (tablet != null)
            {
                tablet.setInkingMode(0);
            }

            penDataList.Clear();
            _lastDrawnPoint = null;
            DrawBasePrompt();
            WriteCanvasToTablet();

            await Task.Delay(300);
            isHandlingInput = false;
        }

        private void RenderSignature()
        {
            if (penDataList == null || penDataList.Count < 2)
                return;

            int width = capability.screenWidth;
            int height = capability.screenHeight;
            int maxX = capability.tabletMaxX;
            int maxY = capability.tabletMaxY;

            signatureBitmap = new Bitmap(width, height);
            using (Graphics g = Graphics.FromImage(signatureBitmap))
            {
                g.Clear(Color.White);
                Pen pen = new Pen(Color.Black, 2);

                for (int i = 1; i < penDataList.Count; ++i)
                {
                    var p1 = penDataList[i - 1];
                    var p2 = penDataList[i];

                    if (p1.sw != 0 && p2.sw != 0)
                    {
                        System.Drawing.Point pt1 = new System.Drawing.Point(p1.x * width / maxX, p1.y * height / maxY);
                        System.Drawing.Point pt2 = new System.Drawing.Point(p2.x * width / maxX, p2.y * height / maxY);
                        g.DrawLine(pen, pt1, pt2);
                    }
                }
            }

            SignatureImage.Source = BitmapToImageSource((Bitmap)signatureBitmap.Clone());

            signatureBitmap.Save("signature_temp.png", ImageFormat.Png);
        }

        private byte[] ConvertTo1BitBitmap(Bitmap source)
        {
            int width = source.Width;
            int height = source.Height;

            // Convert source to 32bpp ARGB if not already
            Bitmap safeSource = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(safeSource))
            {
                g.DrawImageUnscaled(source, 0, 0);
            }

            Bitmap mono = new Bitmap(width, height, PixelFormat.Format1bppIndexed);
            BitmapData monoData = mono.LockBits(
                new System.Drawing.Rectangle(0, 0, mono.Width, mono.Height),
                ImageLockMode.WriteOnly,
                mono.PixelFormat);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Color c = safeSource.GetPixel(x, y);
                    bool isBlack = (c.R + c.G + c.B) / 3 < 128;

                    if (isBlack)
                    {
                        int index = y * monoData.Stride + (x >> 3);
                        byte existingByte = Marshal.ReadByte(monoData.Scan0, index);
                        existingByte |= (byte)(0x80 >> (x & 0x7));
                        Marshal.WriteByte(monoData.Scan0, index, existingByte);
                    }
                }
            }

            mono.UnlockBits(monoData);
            safeSource.Dispose();

            BitmapData bmpData = mono.LockBits(
                new System.Drawing.Rectangle(0, 0, mono.Width, mono.Height),
                ImageLockMode.ReadOnly,
                mono.PixelFormat);

            int byteCount = bmpData.Stride * mono.Height;
            byte[] data = new byte[byteCount];
            Marshal.Copy(bmpData.Scan0, data, 0, byteCount);
            mono.UnlockBits(bmpData);
            mono.Dispose();

            return data;
        }

        private BitmapImage BitmapToImageSource(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Bmp); // suspect line
                    memory.Position = 0;

                    var bitmapImage = new BitmapImage();
                    bitmapImage.BeginInit();
                    bitmapImage.StreamSource = memory;
                    bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                    bitmapImage.EndInit();
                    bitmapImage.Freeze();
                    return bitmapImage;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("BitmapToImageSource FAILED:\n\n" + ex.GetType().Name + ": " + ex.Message);
                return null;
            }
        }

        private void SaveSignature_Click(object sender, RoutedEventArgs e)
        {
            if (signatureBitmap == null)
            {
                MessageBox.Show("No signature captured.");
                return;
            }

            string filePath = $"Signature_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            signatureBitmap.Save(filePath, ImageFormat.Png);
            MessageBox.Show($"Saved to: {filePath}");
        }

    }
}
