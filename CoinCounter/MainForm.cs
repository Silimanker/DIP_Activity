using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace CoinCounter
{
    public class MainForm : Form
    {
        private readonly PictureBox pb = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Gainsboro };
        private readonly ListView lv = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, GridLines = true };
        private readonly Label lblTotal = new Label { Dock = DockStyle.Fill, Font = new Font("Segoe UI", 18f, FontStyle.Bold), TextAlign = ContentAlignment.MiddleCenter, Text = "Total: -" };
        private readonly Label lblInfo = new Label { Dock = DockStyle.Fill, Padding = new Padding(6), Text = "Open an image of coins to begin." };
        private readonly Button btnOpen = new Button { Text = "Open image...", AutoSize = true };
        private readonly Button btnCount = new Button { Text = "Count coins", AutoSize = true, Enabled = false };
        private readonly Button btnSave = new Button { Text = "Save annotated...", AutoSize = true, Enabled = false };
        private readonly CheckBox chkMask = new CheckBox { Text = "Show binary mask", AutoSize = true, Enabled = false, Padding = new Padding(8, 5, 0, 0) };

        private Bitmap original;
        private Bitmap annotated;
        private Bitmap maskBitmap;
        private AnalysisResult result;

        public MainForm()
        {
            Text = "Philippine Coin Counter";
            Width = 1100;
            Height = 700;

            lv.Columns.Add("Denomination", 120);
            lv.Columns.Add("Count", 60, HorizontalAlignment.Right);
            lv.Columns.Add("Subtotal (PHP)", 100, HorizontalAlignment.Right);

            var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 40, Padding = new Padding(6, 6, 0, 0) };
            bar.Controls.Add(btnOpen);
            bar.Controls.Add(btnCount);
            bar.Controls.Add(btnSave);
            bar.Controls.Add(chkMask);

            var side = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            side.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
            side.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            side.Controls.Add(lv, 0, 0);
            side.Controls.Add(lblTotal, 0, 1);
            side.Controls.Add(lblInfo, 0, 2);

            var split = new SplitContainer { Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel2 };
            split.Panel1.Controls.Add(pb);
            split.Panel2.Controls.Add(side);

            Controls.Add(split);
            Controls.Add(bar);

            Shown += delegate { split.SplitterDistance = Math.Max(200, split.Width - 340); };

            btnOpen.Click += OnOpen;
            btnCount.Click += async delegate { await RunAnalysis(); };
            btnSave.Click += OnSave;
            chkMask.CheckedChanged += delegate { RefreshPicture(); };
        }

        // ---- actions ------------------------------------------------------

        private async void OnOpen(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog { Filter = "Images|*.bmp;*.jpg;*.jpeg;*.png;*.gif;*.tif;*.tiff" })
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    // copy so the file is not kept locked by the Bitmap
                    using (var fromFile = new Bitmap(dlg.FileName))
                        original = new Bitmap(fromFile);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "Could not open the image:\n" + ex.Message, Text);
                    return;
                }
            }

            result = null;
            pb.Image = null;   // never leave the PictureBox pointing at a disposed bitmap
            DisposeAndNull(ref annotated);
            DisposeAndNull(ref maskBitmap);
            btnCount.Enabled = true;
            btnSave.Enabled = false;
            chkMask.Enabled = false;
            chkMask.Checked = false;
            pb.Image = original;
            lv.Items.Clear();
            lblTotal.Text = "Total: -";

            await RunAnalysis();   // detect immediately after opening
        }

        private async Task RunAnalysis()
        {
            if (original == null) return;

            btnOpen.Enabled = btnCount.Enabled = false;
            Cursor = Cursors.WaitCursor;
            lblInfo.Text = "Analyzing...";

            try
            {
                Bitmap work = new Bitmap(original);   // the worker thread gets its own copy
                result = await Task.Run(() =>
                {
                    try { return CoinAnalyzer.Analyze(work); }
                    finally { work.Dispose(); }
                });

                pb.Image = null;
                DisposeAndNull(ref annotated);
                DisposeAndNull(ref maskBitmap);
                annotated = BuildAnnotated(original, result);
                maskBitmap = BuildMaskBitmap(result);

                ShowResults();
                chkMask.Enabled = true;
                btnSave.Enabled = true;
                RefreshPicture();
            }
            catch (Exception ex)
            {
                lblInfo.Text = "Analysis failed: " + ex.Message;
            }
            finally
            {
                Cursor = Cursors.Default;
                btnOpen.Enabled = btnCount.Enabled = true;
            }
        }

        private void OnSave(object sender, EventArgs e)
        {
            if (annotated == null) return;
            using (var dlg = new SaveFileDialog { Filter = "PNG|*.png|JPEG|*.jpg", FileName = "coins_annotated.png" })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    annotated.Save(dlg.FileName);
            }
        }

        // ---- display ------------------------------------------------------

        private void RefreshPicture()
        {
            if (original == null) return;
            if (chkMask.Checked && maskBitmap != null) pb.Image = maskBitmap;
            else if (annotated != null) pb.Image = annotated;
            else pb.Image = original;
        }

        private void ShowResults()
        {
            lv.Items.Clear();
            foreach (Denomination d in Denomination.All)
            {
                int n = result.CountOf(d);
                lv.Items.Add(new ListViewItem(new[] { d.Name, n.ToString(), (n * d.Value).ToString("N2") }));
            }
            var totalRow = new ListViewItem(new[] { "All coins", result.Coins.Count.ToString(), result.Total.ToString("N2") });
            totalRow.Font = new Font(lv.Font, FontStyle.Bold);
            lv.Items.Add(totalRow);

            lblTotal.Text = "Total: \u20B1" + result.Total.ToString("N2");

            string info = "Image: " + result.Width + " x " + result.Height + " px\r\n" +
                          "Otsu threshold: " + result.Threshold +
                          (result.DarkCoinsOnLight ? " (dark coins, light background)" : " (light coins, dark background)") + "\r\n" +
                          "Coins found: " + result.Coins.Count + "\r\n" +
                          "Estimated scale: " + result.PixelsPerMm.ToString("0.00") + " px/mm";
            foreach (string w in result.Warnings)
                info += "\r\nWarning: " + w;
            lblInfo.Text = info;
        }

        private static Bitmap BuildAnnotated(Bitmap src, AnalysisResult r)
        {
            var bmp = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height));
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;

                float penWidth = Math.Max(2f, src.Width / 250f);
                float fontPx = Math.Max(9f, src.Width / 48f);
                using (var font = new Font("Segoe UI", fontPx, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    foreach (Coin c in r.Coins)
                    {
                        Color tint = c.Denomination != null ? c.Denomination.Tint : Color.Yellow;
                        string tag = c.Denomination != null ? c.Denomination.Tag : "?";
                        float rad = (float)(c.Diameter / 2.0);

                        using (var pen = new Pen(tint, penWidth))
                            g.DrawEllipse(pen, (float)c.CenterX - rad, (float)c.CenterY - rad, rad * 2, rad * 2);

                        var box = new RectangleF((float)c.CenterX - rad, (float)c.CenterY - fontPx, rad * 2, fontPx * 2);
                        g.DrawString(tag, font, Brushes.Black, new RectangleF(box.X + 1, box.Y + 1, box.Width, box.Height), fmt);
                        g.DrawString(tag, font, Brushes.White, box, fmt);
                    }
                }
            }
            return bmp;
        }

        private static Bitmap BuildMaskBitmap(AnalysisResult r)
        {
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, r.Width, r.Height),
                                           ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] raw = new byte[Math.Abs(stride) * r.Height];
                for (int y = 0; y < r.Height; y++)
                {
                    for (int x = 0; x < r.Width; x++)
                    {
                        byte v = r.Mask[y * r.Width + x] != 0 ? (byte)255 : (byte)0;
                        int i = y * Math.Abs(stride) + x * 4;
                        raw[i] = raw[i + 1] = raw[i + 2] = v;
                        raw[i + 3] = 255;
                    }
                }
                Marshal.Copy(raw, 0, data.Scan0, raw.Length);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
            return bmp;
        }

        private static void DisposeAndNull(ref Bitmap bmp)
        {
            if (bmp != null)
            {
                bmp.Dispose();
                bmp = null;
            }
        }
    }
}
