using iTextSharp.text;
using iTextSharp.text.pdf;
using NTwain;
using NTwain.Data;
using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace Sample.Winform
{
    sealed partial class TestForm : Form
    {
        ImageCodecInfo _tiffCodecInfo;
        TwainSession _twain;
        bool _stopScan;
        bool _loadingCaps;
        ImageProcessor imageProcessor;
        #region setup & cleanup

        public TestForm()
        {
            InitializeComponent();
            if (NTwain.PlatformInfo.Current.IsApp64Bit)
            {
                Text = Text + " (64bit)";
            }
            else
            {
                Text = Text + " (32bit)";
            }
            foreach (var enc in ImageCodecInfo.GetImageEncoders())
            {
                if (enc.MimeType == "image/tiff") { _tiffCodecInfo = enc; break; }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            SetupTwain();

        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_twain != null)
            {
                if (e.CloseReason == CloseReason.UserClosing && _twain.State > 4)
                {
                    e.Cancel = true;
                }
                else
                {
                    CleanupTwain();
                }
            }
            base.OnFormClosing(e);
        }
        int Index = 0;
        int IndexPage = 0;
        private void SetupTwain()
        {
            var appId = TWIdentity.CreateFromAssembly(DataGroups.Image, Assembly.GetEntryAssembly());
            imageProcessor = new ImageProcessor(false,(float)nLebel.Value);
            _twain = new TwainSession(appId);
            _twain.StateChanged += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("State changed to " + _twain.State + " on thread " + Thread.CurrentThread.ManagedThreadId);
               // AddLog("State changed to " + _twain.State + " on thread " + Thread.CurrentThread.ManagedThreadId);
            };
            _twain.TransferError += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Got xfer error on thread " + Thread.CurrentThread.ManagedThreadId);
            };
            _twain.DataTransferred += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Transferred data event on thread " + Thread.CurrentThread.ManagedThreadId);

                // example on getting ext image info
                var infos = e.GetExtImageInfo(ExtendedImageInfo.Camera).Where(it => it.ReturnCode == ReturnCode.Success);
                foreach (var it in infos)
                {
                    var values = it.ReadValues();
                    PlatformInfo.Current.Log.Info(string.Format("{0} = {1}", it.InfoID, values.FirstOrDefault()));
                    break;
                }

                bool isEmptyImage = false;
                // handle image data
                System.Drawing.Image img = null;
                if (e.NativeData != IntPtr.Zero)
                {
                    var stream = e.GetNativeImageStream();
                    if (stream != null)
                    {
                        img = System.Drawing.Image.FromStream(stream);
                        isEmptyImage=CheckIsEmpty(img);
                        
                    }

                }
                else if (!string.IsNullOrEmpty(e.FileDataPath))
                {
                    img = new Bitmap(e.FileDataPath);
                }
                if (img != null)
                {
                    this.BeginInvoke(new Action(() =>
                    {
                        //if (pictureBox1.Image != null)
                        //{
                        //    pictureBox1.Image.Dispose();
                        //    pictureBox1.Image = null;
                        //}
                        //pictureBox1.Image = img;
                        
                    }));
                    var path = Path.Combine(textBox1.Text,$"{((TWFix32)comboDPI.SelectedItem)}_{(int)imageProcessor.blankThreshold}");
                    if (!Directory.Exists(path))
                        Directory.CreateDirectory(path);
                    
                    if (++Index % 2 != 0)
                    {
                        IndexPage++;
                    }
                var filename =$"{IndexPage}_{(Index%2==0?1:2)}" ;
                    
                    if (chkJpeg.Checked)
                    {
                        var pathJpeg = Path.Combine(path, "Jpeg");
                        if (!Directory.Exists(pathJpeg))
                            Directory.CreateDirectory(pathJpeg);
                        var pathJpegEmpty= Path.Combine(pathJpeg, "EmptyImage");
                        if (!Directory.Exists(pathJpegEmpty))
                            Directory.CreateDirectory(pathJpegEmpty);

                        var imageSaveStopWatch = Stopwatch.StartNew();
                        try
                        {
                            saveImageTofile(img,isEmptyImage? pathJpegEmpty: pathJpeg, filename);
                        }catch(Exception er)
                        {
                            StopAndLog(imageSaveStopWatch, $"error: {er.Message} ");
                        }

                        finally
                        {
                            StopAndLog(imageSaveStopWatch, $"{filename} Save jpeg ");
                        }
                    }
                    if (chkPdf.Checked)
                    {
                        var pathJpeg = Path.Combine(path, "Pdf");
                        if (!Directory.Exists(pathJpeg))
                            Directory.CreateDirectory(pathJpeg);
                        var imageToPdfSW = Stopwatch.StartNew();
                        try
                        {
                            ConvertJPG2PDF(img, pathJpeg,filename);
                            //exportarPDF(img, filename);
                        }
                        catch (Exception er)
                        {
                            StopAndLog(imageToPdfSW, $"pdf error: {er.Message} ");
                        }
                        finally
                        {
                            StopAndLog(imageToPdfSW, $"{filename} Save pdf ");
                        }
                    }
                }
            };
            _twain.SourceDisabled += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Source disabled event on thread " + Thread.CurrentThread.ManagedThreadId);
                this.BeginInvoke(new Action(() =>
                {
                    btnStopScan.Enabled = false;
                    btnStartCapture.Enabled = true;
                    panelOptions.Enabled = true;
                    StopAndLog(mainStopWatch, "End scanning");
                    SaveLogToFile();
                    LoadSourceCaps();
                }));
            };
            _twain.TransferReady += (s, e) =>
            {
                PlatformInfo.Current.Log.Info("Transferr ready event on thread " + Thread.CurrentThread.ManagedThreadId);
                e.CancelAll = _stopScan;
            };

            // either set sync context and don't worry about threads during events,
            // or don't and use control.invoke during the events yourself
            PlatformInfo.Current.Log.Info("Setup thread = " + Thread.CurrentThread.ManagedThreadId);
            _twain.SynchronizationContext = SynchronizationContext.Current;
            if (_twain.State < 3)
            {
                // use this for internal msg loop
                _twain.Open();
                // use this to hook into current app loop
                //_twain.Open(new WindowsFormsMessageLoopHook(this.Handle));
            }
        }

        private bool CheckIsEmpty(System.Drawing.Image img)
        {
            
            var checktime = Stopwatch.StartNew();
            var value = imageProcessor.StdDev(img);
            checktime.Stop  ();
            bool result = value < imageProcessor.blankThreshold;
            AddLog($"Check empty {result} ({value}) duration {TimeSpan.FromMilliseconds(checktime.ElapsedMilliseconds).TotalSeconds} sec");
            return result;
        }

        private void SaveLogToFile()
        {
            if (!string.IsNullOrEmpty(textLog.Text))
            {
                
                var path = Path.Combine(textBox1.Text, $"{((TWFix32)comboDPI.SelectedItem)}_{(int)imageProcessor.blankThreshold}", "Log.txt");
                File.WriteAllText(path, textLog.Text);
            }
        }
        private void CleanupTwain()
        {
            if (_twain.State == 4)
            {
                _twain.CurrentSource.Close();
            }
            if (_twain.State == 3)
            {
                _twain.Close();
            }

            if (_twain.State > 2)
            {
                // normal close down didn't work, do hard kill
                _twain.ForceStepDown(2);
            }
        }

        #endregion

        #region toolbar

        private void btnSources_DropDownOpening(object sender, EventArgs e)
        {
            if (btnSources.DropDownItems.Count == 2)
            {
                ReloadSourceList();
            }
        }

        private void reloadSourcesListToolStripMenuItem_Click(object sender, EventArgs e)
        {
            ReloadSourceList();
        }

        private void ReloadSourceList()
        {
            if (_twain.State >= 3)
            {
                while (btnSources.DropDownItems.IndexOf(sepSourceList) > 0)
                {
                    var first = btnSources.DropDownItems[0];
                    first.Click -= SourceMenuItem_Click;
                    btnSources.DropDownItems.Remove(first);
                }
                foreach (var src in _twain)
                {
                    var srcBtn = new ToolStripMenuItem(src.Name);
                    srcBtn.Tag = src;
                    srcBtn.Click += SourceMenuItem_Click;
                    srcBtn.Checked = _twain.CurrentSource != null && _twain.CurrentSource.Name == src.Name;
                    btnSources.DropDownItems.Insert(0, srcBtn);
                }
            }
        }

        void SourceMenuItem_Click(object sender, EventArgs e)
        {
            // do nothing if source is enabled
            if (_twain.State > 4) { return; }

            if (_twain.State == 4) { _twain.CurrentSource.Close(); }

            foreach (var btn in btnSources.DropDownItems)
            {
                var srcBtn = btn as ToolStripMenuItem;
                if (srcBtn != null) { srcBtn.Checked = false; }
            }

            var curBtn = (sender as ToolStripMenuItem);
            var src = curBtn.Tag as DataSource;
            if (src.Open() == ReturnCode.Success)
            {
                curBtn.Checked = true;
                btnStartCapture.Enabled = true;
                LoadSourceCaps();
            }
        }
        Stopwatch mainStopWatch;
        private void btnStartCapture_Click(object sender, EventArgs e)
        {
            if (_twain.State == 4)
            {
                //_twain.CurrentSource.CapXferCount.Set(4);

                _stopScan = false;
                textLog.Clear();
                imageProcessor.blankThreshold =(float) nLebel.Value;
                AddLog($"Start scanning... (Порог для проверки пустой ст. {imageProcessor.blankThreshold})");
                Index = 0;
                IndexPage = 0;
                mainStopWatch = Stopwatch.StartNew();
                if (_twain.CurrentSource.Capabilities.CapUIControllable.IsSupported)//.SupportedCaps.Contains(CapabilityId.CapUIControllable))
                {
                    // hide scanner ui if possible
                    if (_twain.CurrentSource.Enable(SourceEnableMode.NoUI, false, this.Handle) == ReturnCode.Success)
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
                else
                {
                    if (_twain.CurrentSource.Enable(SourceEnableMode.ShowUI, true, this.Handle) == ReturnCode.Success)
                    {
                        btnStopScan.Enabled = true;
                        btnStartCapture.Enabled = false;
                        panelOptions.Enabled = false;
                    }
                }
                //StopAndLog(stopwatch, "End scanning");
                
            }
        }
        private void StopAndLog(Stopwatch stopwatch, string message)
        {
            if (stopwatch != null)
            {
                stopwatch.Stop();
                this.Invoke(new MethodInvoker(delegate ()
                {
                    if (cbxTime.SelectedIndex==0)
                        AddLog($"{message} {TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds).TotalSeconds} sec");
                    else
                    {
                        AddLog($"{message} {TimeSpan.FromMilliseconds(stopwatch.ElapsedMilliseconds).TotalMilliseconds} msec");
                    }
                }));


            }
        }
        private void btnStopScan_Click(object sender, EventArgs e)
        {
            _stopScan = true;
        }

        private void btnSaveImage_Click(object sender, EventArgs e)
        {
            //var img = pictureBox1.Image;

            //if (img != null)
            //{
            //    switch (img.PixelFormat)
            //    {
            //        case PixelFormat.Format1bppIndexed:
            //            saveFileDialog1.Filter = "tiff files|*.tif";
            //            break;
            //        default:
            //            saveFileDialog1.Filter = "png files|*.png";
            //            break;
            //    }

            //    if (saveFileDialog1.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            //    {
            //        if (saveFileDialog1.FileName.EndsWith(".tif", StringComparison.OrdinalIgnoreCase))
            //        {
            //            EncoderParameters tiffParam = new EncoderParameters(1);

            //            tiffParam.Param[0] = new EncoderParameter(Encoder.Compression, (long)EncoderValue.CompressionCCITT4);

            //            pictureBox1.Image.Save(saveFileDialog1.FileName, _tiffCodecInfo, tiffParam);
            //        }
            //        else
            //        {
            //            pictureBox1.Image.Save(saveFileDialog1.FileName, ImageFormat.Png);
            //        }
            //    }
            //}
        }

        #endregion
        private void saveImageTofile(System.Drawing.Image image,string path, string name)
        {
            
            SaveJpeg(image, Path.Combine(path, $"{name}.jpeg"));
        }
        void SaveJpeg(System.Drawing.Image img, string filename)
        {
            EncoderParameter qualityParam = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 50L);
            ImageCodecInfo jpegCodec = GetEncoder(ImageFormat.Jpeg);
            EncoderParameters encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = qualityParam;
            img.Save(filename, jpegCodec, encoderParams);
        }
        ImageCodecInfo GetEncoder(ImageFormat format)
        {

            ImageCodecInfo[] codecs = ImageCodecInfo.GetImageDecoders();

            foreach (ImageCodecInfo codec in codecs)
            {
                if (codec.FormatID == format.Guid)
                {
                    return codec;
                }
            }
            return null;
        }
        #region cap control


        private void LoadSourceCaps()
        {
            var src = _twain.CurrentSource;
            _loadingCaps = true;

            //var test = src.SupportedCaps;

            if (groupDepth.Enabled = src.Capabilities.ICapPixelType.IsSupported)
            {
                LoadDepth(src.Capabilities.ICapPixelType);
            }
            if (groupDPI.Enabled = src.Capabilities.ICapXResolution.IsSupported && src.Capabilities.ICapYResolution.IsSupported)
            {
                LoadDPI(src.Capabilities.ICapXResolution);
            }
            // TODO: find out if this is how duplex works or also needs the other option
            if (groupDuplex.Enabled = src.Capabilities.CapDuplexEnabled.IsSupported)
            {
                LoadDuplex(src.Capabilities.CapDuplexEnabled);
            }
            if (groupSize.Enabled = src.Capabilities.ICapSupportedSizes.IsSupported)
            {
                LoadPaperSize(src.Capabilities.ICapSupportedSizes);
            }
            btnAllSettings.Enabled = src.Capabilities.CapEnableDSUIOnly.IsSupported;
            _loadingCaps = false;
        }

        private void LoadPaperSize(ICapWrapper<SupportedSize> cap)
        {
            var list = cap.GetValues().ToList();
            comboSize.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                comboSize.SelectedItem = cur;
            }
            var labelTest = cap.GetLabel();
            if (!string.IsNullOrEmpty(labelTest))
            {
                groupSize.Text = labelTest;
            }
        }


        private void LoadDuplex(ICapWrapper<BoolType> cap)
        {
            ckDuplex.Checked = cap.GetCurrent() == BoolType.True;
        }


        private void LoadDPI(ICapWrapper<TWFix32> cap)
        {
            // only allow dpi of certain values for those source that lists everything
            var list = cap.GetValues().Where(dpi => (dpi % 50) == 0).ToList();
            comboDPI.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                comboDPI.SelectedItem = cur;
            }
        }

        private void LoadDepth(ICapWrapper<PixelType> cap)
        {
            var list = cap.GetValues().ToList();
            comboDepth.DataSource = list;
            var cur = cap.GetCurrent();
            if (list.Contains(cur))
            {
                comboDepth.SelectedItem = cur;
            }
            var labelTest = cap.GetLabel();
            if (!string.IsNullOrEmpty(labelTest))
            {
                groupDepth.Text = labelTest;
            }
        }

        private void comboSize_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (SupportedSize)comboSize.SelectedItem;
                _twain.CurrentSource.Capabilities.ICapSupportedSizes.SetValue(sel);
            }
        }

        private void comboDepth_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (PixelType)comboDepth.SelectedItem;
                _twain.CurrentSource.Capabilities.ICapPixelType.SetValue(sel);
            }
        }

        private void comboDPI_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                var sel = (TWFix32)comboDPI.SelectedItem;
                _twain.CurrentSource.Capabilities.ICapXResolution.SetValue(sel);
                _twain.CurrentSource.Capabilities.ICapYResolution.SetValue(sel);
            }
        }

        private void ckDuplex_CheckedChanged(object sender, EventArgs e)
        {
            if (!_loadingCaps && _twain.State == 4)
            {
                _twain.CurrentSource.Capabilities.CapDuplexEnabled.SetValue(ckDuplex.Checked ? BoolType.True : BoolType.False);
            }
        }

        private void btnAllSettings_Click(object sender, EventArgs e)
        {
            _twain.CurrentSource.Enable(SourceEnableMode.ShowUIOnly, true, this.Handle);
        }

        #endregion

        private void button1_Click(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.SelectedPath = textBox1.Text;
                if (dialog.ShowDialog() == DialogResult.OK)
                {
                    textBox1.Text = dialog.SelectedPath;
                }

            }
        }

        
        private void AddLog(string msg)
        {
            textLog.AppendText($"{msg}{Environment.NewLine}");
        }
        public void exportarPDF(System.Drawing.Image image,string path,  string name)
        {
            // System.Drawing.Image image = System.Drawing.Image.FromFile("C://snippetsource.jpg"); // Here it saves with a physical file
           // System.Drawing.Image image = img;  //Here I passed a bitmap
            Document doc = new Document(PageSize.A4);
              path = Path.Combine(path, $"{name}.pdf");
            PdfWriter_GetInstance(doc, new FileStream(path, FileMode.Create));
           // PdfWriter.GetInstance(doc, new FileStream(path, FileMode.Create));
            doc.Open();
            iTextSharp.text.Image pdfImage = iTextSharp.text.Image.GetInstance(image,
                    System.Drawing.Imaging.ImageFormat.Jpeg);
            doc.Add(pdfImage);
            doc.Close();
        }
        iTextSharp.text.pdf.PdfWriter PdfWriter_GetInstance(iTextSharp.text.Document document, System.IO.FileStream FS)
        {
            iTextSharp.text.pdf.PdfWriter writer = null;
            for (int ts = 0; ts < 6; ts++)
            {
                try
                {
                    writer = iTextSharp.text.pdf.PdfWriter.GetInstance(document, FS); // sometime rise exception on first call
                    break; //created, then exit loop
                }
                catch
                {
                    System.Threading.Thread.Sleep(250); // wait for a while...
                }
            }
            if (writer == null) // check if instantiated
            {
                throw new Exception("iTextSharp PdfWriter is null");
            }

            return writer;
        }
        void ConvertJPG2PDF(System.Drawing.Image img,string path, string pdf)
        {
            pdf= Path.Combine( path, $"{pdf}.pdf");
            var document = new Document(iTextSharp.text.PageSize.A4, 25, 25, 25, 25);
            using (var stream = new FileStream(pdf, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                //PdfWriter.GetInstance(document, stream);
                PdfWriter_GetInstance(document, stream);
                document.Open();
                
                    var image = iTextSharp.text.Image.GetInstance(img,
                    System.Drawing.Imaging.ImageFormat.Jpeg); // iTextSharp.text.Image.GetInstance(imageStream);
                    if (image.Height > iTextSharp.text.PageSize.A4.Height - 25)
                    {
                        image.ScaleToFit(iTextSharp.text.PageSize.A4.Width - 25, iTextSharp.text.PageSize.A4.Height - 25);
                    }
                    else if (image.Width > iTextSharp.text.PageSize.A4.Width - 25)
                    {
                        image.ScaleToFit(iTextSharp.text.PageSize.A4.Width - 25, iTextSharp.text.PageSize.A4.Height - 25);
                    }
                    image.Alignment = iTextSharp.text.Image.ALIGN_MIDDLE;
                    document.Add(image);
                

                document.Close();
            }
        }

        private void nLebel_ValueChanged(object sender, EventArgs e)
        {
            imageProcessor.blankThreshold =(float)nLebel.Value; 
        }
    }
}
