using System.Windows;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Media.Imaging;
using wgssSTU;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Threading.Tasks;

namespace WacomSignatureApp
{
    public partial class MainWindow : Window
    {
        private Tablet tablet;
        private ICapability capability;
        private List<IPenData> penDataList = new List<IPenData>();
        private Bitmap signatureBitmap;
        private bool isHandlingInput = false;

        private System.Drawing.Rectangle submitButton;
        private System.Drawing.Rectangle clearButton;
        private System.Drawing.Rectangle cancelButton;
        private System.Drawing.Point? _lastDrawnPoint = null;

        private Bitmap canvasBitmap;
        private Graphics canvasGraphics;
        private DispatcherTimer refreshTimer;
        private bool canvasDirty = false;
        private bool isDrawingStroke = false;
        private int clearCount = 0;
        private bool isCancelling = false;
        private byte[] lastFrameHash;

        public MainWindow()
        {
            InitializeComponent();
        }

        private void CaptureSignature_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (tablet != null)
                {
                    try
                    {
                        refreshTimer?.Stop();
                        tablet.setInkingMode(0);
                        tablet.disconnect();
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine(ex.Message);
                    }
                    finally
                    {
                        tablet = null;
                    }
                }

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

                canvasBitmap = new Bitmap(capability.screenWidth, capability.screenHeight, PixelFormat.Format32bppArgb);
                canvasGraphics = Graphics.FromImage(canvasBitmap);
                canvasGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

                DrawBasePrompt();
                canvasDirty = true;

                tablet.setInkingMode(0);

                tablet.onPenData += new ITabletEvents2_onPenDataEventHandler((penData) =>
                {
                    penDataList.Add(penData);

                    if (isHandlingInput) return;

                    int x = penData.x * capability.screenWidth / capability.tabletMaxX;
                    int y = penData.y * capability.screenHeight / capability.tabletMaxY;
                    var currentPoint = new System.Drawing.Point(x, y);

                    if (penData.sw != 0)
                    {
                        isDrawingStroke = true;

                        if (_lastDrawnPoint.HasValue)
                        {
                            canvasGraphics.DrawLine(Pens.Black, _lastDrawnPoint.Value, currentPoint);
                            canvasDirty = true;
                        }

                        _lastDrawnPoint = currentPoint;
                    }
                    else
                    {
                        isDrawingStroke = false;
                        _lastDrawnPoint = null;
                        canvasDirty = true;

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
                            else if (cancelButton.Contains(tapPoint))
                            {
                                isHandlingInput = true;
                                CancelSignature();
                            }
                        }
                    }
                });

                tablet.setInkingMode(1);

                refreshTimer = new DispatcherTimer();
                refreshTimer.Interval = TimeSpan.FromMilliseconds(50);
                refreshTimer.Tick += (s, args) =>
                {
                    if (canvasDirty && !isDrawingStroke)
                    {
                        WriteCanvasToTablet();
                        canvasDirty = false;
                    }
                };
                refreshTimer.Start();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Error: " + ex.Message);
            }
        }

        private void DrawBasePrompt()
        {
            canvasGraphics.Clear(Color.White);
            canvasGraphics.DrawString("Please sign below", new Font("Arial", 12), Brushes.Black, 10, 10);
            canvasGraphics.DrawRectangle(Pens.Black, 0, 0, canvasBitmap.Width - 1, canvasBitmap.Height - 1);

            int buttonWidth = 60;
            int buttonHeight = 25;
            int spacing = 10;

            submitButton = new System.Drawing.Rectangle(
                canvasBitmap.Width - buttonWidth - spacing,
                canvasBitmap.Height - buttonHeight - spacing,
                buttonWidth,
                buttonHeight);

            clearButton = new System.Drawing.Rectangle(
                submitButton.X - buttonWidth - spacing,
                submitButton.Y,
                buttonWidth,
                buttonHeight);

            cancelButton = new System.Drawing.Rectangle(
                clearButton.X - buttonWidth - spacing,
                submitButton.Y,
                buttonWidth,
                buttonHeight);

            canvasGraphics.FillRectangle(Brushes.White, cancelButton);
            canvasGraphics.DrawRectangle(Pens.Black, cancelButton);
            canvasGraphics.DrawString("Cancel", new Font("Arial", 8), Brushes.Black, cancelButton.Location);

            canvasGraphics.FillRectangle(Brushes.White, clearButton);
            canvasGraphics.DrawRectangle(Pens.Black, clearButton);
            canvasGraphics.DrawString("Clear", new Font("Arial", 8), Brushes.Black, clearButton.Location);

            canvasGraphics.FillRectangle(Brushes.White, submitButton);
            canvasGraphics.DrawRectangle(Pens.Black, submitButton);
            canvasGraphics.DrawString("Submit", new Font("Arial", 8), Brushes.Black, submitButton.Location);

            WriteCanvasToTablet();
            canvasDirty = false;
        }

        private bool AreArraysEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private void WriteCanvasToTablet()
        {
            try
            {
                byte[] monoData = ConvertTo1BitBitmap(canvasBitmap);
                Array imageArray = monoData;

                if (lastFrameHash != null && AreArraysEqual(monoData, lastFrameHash))
                    return;

                lastFrameHash = monoData;

                const byte encodingMode = (byte)wgssSTU.encodingMode.EncodingMode_1bit;
                tablet.writeImage(encodingMode, imageArray);
                Dispatcher.Invoke(() =>
                {
                    SignatureImage.Source = BitmapToImageSource((Bitmap)canvasBitmap.Clone());
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        private async void SubmitSignature()
        {
            if (tablet == null) return;

            refreshTimer?.Stop();

            RenderSignature();

            await Task.Delay(100);

            await Dispatcher.InvokeAsync(() =>
            {
                ResetTabletState();
                MessageBox.Show("Signature submitted successfully.");
            });
        }

        private async void ClearSignature()
        {
            tablet.setInkingMode(1);

            penDataList.Clear();
            _lastDrawnPoint = null;

            DrawBasePrompt();
            WriteCanvasToTablet();

            canvasDirty = true;

            clearCount++;
            if (clearCount >= 5)
            {
                clearCount = 0;
                await Task.Delay(150);
                ResetTabletAndReload();
            }

            await Task.Delay(300);
            isHandlingInput = false;
        }

        private async void CancelSignature()
        {
            if (isCancelling) return;
            isCancelling = true;

            await Dispatcher.InvokeAsync(() =>
            {
                ResetTabletState();
            });

            isCancelling = false;
        }

        private void ResetTabletAndReload()
        {
            Dispatcher.Invoke(() =>
            {
                CaptureSignature_Click(null, null);
            });
        }

        private void ResetTabletState()
        {
            try
            {
                refreshTimer?.Stop();
                refreshTimer = null;

                if (tablet != null)
                {
                    tablet.setInkingMode(0);
                    tablet.writeImage(0, new byte[capability.screenWidth * capability.screenHeight / 8]);
                    tablet.disconnect();
                    tablet = null;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Reset Tablet error: " + ex.Message);
            }

            SignatureImage.Source = null;
            penDataList.Clear();
            _lastDrawnPoint = null;
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

        private byte[] ConvertTo1BitBitmap(Bitmap bitmap)
        {
            wgssSTU.ProtocolHelper protocolHelper = new wgssSTU.ProtocolHelper();

            MemoryStream stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);

            byte[] bitmapData = (byte[])protocolHelper.resizeAndFlatten(
                stream.ToArray(),
                0, 0,
                (uint)bitmap.Width,
                (uint)bitmap.Height,
                capability.screenWidth,
                capability.screenHeight,
                (byte)wgssSTU.encodingMode.EncodingMode_1bit,
                wgssSTU.Scale.Scale_Fit,
                0, 0
            );

            return bitmapData;
        }

        private BitmapImage BitmapToImageSource(Bitmap bitmap)
        {
            try
            {
                using (MemoryStream memory = new MemoryStream())
                {
                    bitmap.Save(memory, ImageFormat.Bmp);
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
                MessageBox.Show("FAILED:\n\n" + ex.GetType().Name + ": " + ex.Message);
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