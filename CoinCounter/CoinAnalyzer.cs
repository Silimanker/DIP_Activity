using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Runtime.InteropServices;

namespace CoinCounter
{
    // =====================================================================
    //  Data types
    // =====================================================================

    /// <summary>A Philippine coin denomination.</summary>
    public sealed class Denomination
    {
        public string Name { get; }
        public string Tag { get; }          // short label drawn on the picture
        public decimal Value { get; }       // in pesos
        public double DiameterMm { get; }   // relative size reference (see README)
        public Color Tint { get; }          // outline colour on the picture

        private Denomination(string name, string tag, decimal value, double mm, Color tint)
        {
            Name = name; Tag = tag; Value = value; DiameterMm = mm; Tint = tint;
        }

        /// <summary>Ordered from the smallest coin to the biggest.</summary>
        public static readonly Denomination[] All =
        {
            new Denomination("5 Sentimo",  "5c",  0.05m, 15.8, Color.Magenta),
            new Denomination("10 Sentimo", "10c", 0.10m, 17.2, Color.Orange),
            new Denomination("25 Sentimo", "25c", 0.25m, 20.3, Color.DeepSkyBlue),
            new Denomination("1 Peso",     "P1",  1.00m, 24.0, Color.Lime),
            new Denomination("5 Peso",     "P5",  5.00m, 26.8, Color.Red),
        };
    }

    public sealed class Coin
    {
        public int Id { get; set; }
        public double CenterX { get; set; }
        public double CenterY { get; set; }
        public double Diameter { get; set; }     // pixels (equivalent-circle diameter)
        public int Area { get; set; }            // pixels, holes included
        public double FillRatio { get; set; }    // area / bounding-box area (circle ~ 0.785)
        public bool HasHole { get; set; }        // a centred round hole (5 sentimo)
        public bool TouchesBorder { get; set; }  // cut off by the image edge
        public Denomination Denomination { get; set; }
    }

    public sealed class AnalysisResult
    {
        public int Width { get; set; }
        public int Height { get; set; }
        public int Threshold { get; set; }
        public bool DarkCoinsOnLight { get; set; }
        public byte[] Mask { get; set; }                       // 1 = coin pixel
        public double PixelsPerMm { get; set; }
        public List<Coin> Coins { get; } = new List<Coin>();
        public List<string> Warnings { get; } = new List<string>();

        public int CountOf(Denomination d)
        {
            int n = 0;
            foreach (Coin c in Coins)
                if (c.Denomination == d) n++;
            return n;
        }

        public decimal Total
        {
            get
            {
                decimal t = 0;
                foreach (Coin c in Coins)
                    if (c.Denomination != null) t += c.Denomination.Value;
                return t;
            }
        }
    }

    // =====================================================================
    //  The analyzer  (pure .NET - no third-party libraries)
    //
    //  Pipeline:
    //   1. Convert to grayscale
    //   2. 3x3 mean blur (removes speckle noise)
    //   3. Otsu's method -> automatic threshold -> binary mask
    //   4. Connected-component labeling (8-connected) -> one blob per coin
    //   5. Per blob: fill holes (flood fill), equivalent diameter,
    //      detect a centred round hole (5 sentimo)
    //   6. Cluster diameters into size groups and match the groups to the
    //      denominations by least-squares fit of the known size ratios
    // =====================================================================
    public static class CoinAnalyzer
    {
        // Diameters closer than this ratio belong to the same size group.
        private const double ClusterGap = 1.04;

        // Reference photo (the activity image): width in px and the 1-peso
        // diameter in px. Only used as a weak prior when the scale is ambiguous
        // (e.g. the picture contains just one kind of coin).
        private const double RefImageWidth = 546.0;
        private const double RefPesoDiameterPx = 62.7;

        public static AnalysisResult Analyze(Bitmap source)
        {
            int w = source.Width, h = source.Height;
            var result = new AnalysisResult { Width = w, Height = h };

            // 1-2. grayscale + blur
            byte[] gray = ToGray(source);
            byte[] smooth = BoxBlur3(gray, w, h);

            // 3. threshold (automatic)
            int t = Otsu(smooth);
            bool darkCoins = BorderMean(smooth, w, h) >= t;   // light background?
            byte[] mask = new byte[w * h];
            for (int i = 0; i < mask.Length; i++)
                mask[i] = (byte)((darkCoins ? smooth[i] <= t : smooth[i] > t) ? 1 : 0);

            result.Threshold = t;
            result.DarkCoinsOnLight = darkCoins;
            result.Mask = mask;

            // 4. connected components
            int[] labels = new int[w * h];
            List<Blob> blobs = Label(mask, labels, w, h);

            // 5. measure each blob that is big enough to be a coin
            int minArea = Math.Max(40, (int)(0.001 * w * h));
            var candidates = new List<Coin>();
            for (int i = 0; i < blobs.Count; i++)
            {
                if (blobs[i].Area < minArea) continue;
                candidates.Add(MeasureBlob(blobs[i], i + 1, labels, w, h));
            }

            // discard specks / shadows / merged blobs
            if (candidates.Count > 0)
            {
                double med = Median(candidates.Select(c => (double)c.Area).ToList());
                var kept = new List<Coin>();
                foreach (Coin c in candidates)
                {
                    if (c.Area < 0.35 * med) continue;                       // too small
                    if (!c.TouchesBorder && c.FillRatio < 0.60) continue;    // not round
                    if (c.Area > 3.3 * med)
                    {
                        result.Warnings.Add("A large blob at (" + (int)c.CenterX + ", " + (int)c.CenterY +
                                            ") was ignored - touching coins?");
                        continue;
                    }
                    kept.Add(c);
                }
                candidates = kept;
            }

            for (int i = 0; i < candidates.Count; i++)
                candidates[i].Id = i + 1;

            // 6. classify
            Classify(candidates, w, result);
            result.Coins.AddRange(candidates);
            return result;
        }

        // -----------------------------------------------------------------
        //  Step 1-3 helpers
        // -----------------------------------------------------------------

        private static byte[] ToGray(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            BitmapData data = bmp.LockBits(new Rectangle(0, 0, w, h),
                                           ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                byte[] raw = new byte[Math.Abs(stride) * h];
                Marshal.Copy(data.Scan0, raw, 0, raw.Length);

                byte[] gray = new byte[w * h];
                for (int y = 0; y < h; y++)
                {
                    int row = y * Math.Abs(stride);
                    for (int x = 0; x < w; x++)
                    {
                        int i = row + x * 4;                 // B, G, R, A
                        gray[y * w + x] = (byte)((114 * raw[i] + 587 * raw[i + 1] + 299 * raw[i + 2]) / 1000);
                    }
                }
                return gray;
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }

        private static byte[] BoxBlur3(byte[] src, int w, int h)
        {
            byte[] dst = new byte[src.Length];
            for (int y = 0; y < h; y++)
            {
                int y0 = Math.Max(0, y - 1), y1 = Math.Min(h - 1, y + 1);
                for (int x = 0; x < w; x++)
                {
                    int x0 = Math.Max(0, x - 1), x1 = Math.Min(w - 1, x + 1);
                    int sum = 0, n = 0;
                    for (int yy = y0; yy <= y1; yy++)
                        for (int xx = x0; xx <= x1; xx++)
                        {
                            sum += src[yy * w + xx];
                            n++;
                        }
                    dst[y * w + x] = (byte)(sum / n);
                }
            }
            return dst;
        }

        /// <summary>Otsu's method: threshold that maximises between-class variance.</summary>
        private static int Otsu(byte[] gray)
        {
            long[] hist = new long[256];
            foreach (byte v in gray) hist[v]++;

            long total = gray.Length;
            double sumAll = 0;
            for (int i = 0; i < 256; i++) sumAll += i * (double)hist[i];

            double sumB = 0, best = -1;
            long wB = 0;
            int threshold = 128;
            for (int t = 0; t < 256; t++)
            {
                wB += hist[t];
                if (wB == 0) continue;
                long wF = total - wB;
                if (wF == 0) break;

                sumB += t * (double)hist[t];
                double mB = sumB / wB;
                double mF = (sumAll - sumB) / wF;
                double between = (double)wB * wF * (mB - mF) * (mB - mF);
                if (between > best)
                {
                    best = between;
                    threshold = t;
                }
            }
            return threshold;
        }

        private static double BorderMean(byte[] g, int w, int h)
        {
            double sum = 0; long n = 0;
            for (int x = 0; x < w; x++) { sum += g[x] + g[(h - 1) * w + x]; n += 2; }
            for (int y = 1; y < h - 1; y++) { sum += g[y * w] + g[y * w + w - 1]; n += 2; }
            return sum / n;
        }

        // -----------------------------------------------------------------
        //  Step 4: connected components (8-connectivity, iterative flood fill)
        // -----------------------------------------------------------------

        private sealed class Blob
        {
            public int Area;
            public int MinX = int.MaxValue, MinY = int.MaxValue, MaxX = -1, MaxY = -1;
        }

        private static List<Blob> Label(byte[] mask, int[] labels, int w, int h)
        {
            var blobs = new List<Blob>();
            int[] stack = new int[w * h];

            for (int start = 0; start < mask.Length; start++)
            {
                if (mask[start] == 0 || labels[start] != 0) continue;

                int id = blobs.Count + 1;
                var b = new Blob();
                int sp = 0;
                stack[sp++] = start;
                labels[start] = id;

                while (sp > 0)
                {
                    int p = stack[--sp];
                    int x = p % w, y = p / w;

                    b.Area++;
                    if (x < b.MinX) b.MinX = x;
                    if (x > b.MaxX) b.MaxX = x;
                    if (y < b.MinY) b.MinY = y;
                    if (y > b.MaxY) b.MaxY = y;

                    for (int dy = -1; dy <= 1; dy++)
                    {
                        int ny = y + dy;
                        if (ny < 0 || ny >= h) continue;
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            int nx = x + dx;
                            if (nx < 0 || nx >= w) continue;
                            int q = ny * w + nx;
                            if (mask[q] != 0 && labels[q] == 0)
                            {
                                labels[q] = id;
                                stack[sp++] = q;
                            }
                        }
                    }
                }
                blobs.Add(b);
            }
            return blobs;
        }

        // -----------------------------------------------------------------
        //  Step 5: measure one blob (fill holes, diameter, centre hole test)
        // -----------------------------------------------------------------

        private static Coin MeasureBlob(Blob b, int id, int[] labels, int w, int h)
        {
            int bw = b.MaxX - b.MinX + 1;
            int bh = b.MaxY - b.MinY + 1;

            // region: 0 = background/hole candidate, 1 = this coin, 2 = outside background, 3 = visited hole
            byte[] region = new byte[bw * bh];
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    if (labels[(b.MinY + y) * w + b.MinX + x] == id)
                        region[y * bw + x] = 1;

            int[] stack = new int[bw * bh];
            int sp = 0;

            void Seed(int idx)
            {
                if (region[idx] == 0)
                {
                    region[idx] = 2;
                    stack[sp++] = idx;
                }
            }

            // flood the OUTSIDE background starting from the bounding-box border (4-connected)
            for (int x = 0; x < bw; x++) { Seed(x); Seed((bh - 1) * bw + x); }
            for (int y = 0; y < bh; y++) { Seed(y * bw); Seed(y * bw + bw - 1); }
            while (sp > 0)
            {
                int p = stack[--sp];
                int x = p % bw, y = p / bw;
                if (x > 0) Seed(p - 1);
                if (x < bw - 1) Seed(p + 1);
                if (y > 0) Seed(p - bw);
                if (y < bh - 1) Seed(p + bw);
            }

            // everything that is not "outside" is the filled coin (coin pixels + enclosed holes)
            int filled = 0;
            double sx = 0, sy = 0;
            for (int y = 0; y < bh; y++)
                for (int x = 0; x < bw; x++)
                    if (region[y * bw + x] != 2)
                    {
                        filled++;
                        sx += x;
                        sy += y;
                    }

            // find the biggest enclosed hole (4-connected components of the remaining 0-pixels)
            int holeArea = 0, hMinX = 0, hMaxX = 0, hMinY = 0, hMaxY = 0;
            double hSx = 0, hSy = 0;
            for (int start = 0; start < region.Length; start++)
            {
                if (region[start] != 0) continue;

                int area = 0, mnx = int.MaxValue, mxx = -1, mny = int.MaxValue, mxy = -1;
                double csx = 0, csy = 0;
                sp = 0;
                region[start] = 3;
                stack[sp++] = start;
                while (sp > 0)
                {
                    int p = stack[--sp];
                    int x = p % bw, y = p / bw;
                    area++; csx += x; csy += y;
                    if (x < mnx) mnx = x;
                    if (x > mxx) mxx = x;
                    if (y < mny) mny = y;
                    if (y > mxy) mxy = y;

                    if (x > 0 && region[p - 1] == 0) { region[p - 1] = 3; stack[sp++] = p - 1; }
                    if (x < bw - 1 && region[p + 1] == 0) { region[p + 1] = 3; stack[sp++] = p + 1; }
                    if (y > 0 && region[p - bw] == 0) { region[p - bw] = 3; stack[sp++] = p - bw; }
                    if (y < bh - 1 && region[p + bw] == 0) { region[p + bw] = 3; stack[sp++] = p + bw; }
                }

                if (area > holeArea)
                {
                    holeArea = area;
                    hMinX = mnx; hMaxX = mxx; hMinY = mny; hMaxY = mxy;
                    hSx = csx; hSy = csy;
                }
            }

            bool touches = b.MinX == 0 || b.MinY == 0 || b.MaxX == w - 1 || b.MaxY == h - 1;
            double cx = sx / filled, cy = sy / filled;
            double diameter = touches ? Math.Max(bw, bh) : 2.0 * Math.Sqrt(filled / Math.PI);

            // A real 5-sentimo hole is: big enough, near the centre, and round.
            bool hasHole = false;
            if (holeArea > 0)
            {
                double hx = hSx / holeArea, hy = hSy / holeArea;
                double offset = Math.Sqrt((hx - cx) * (hx - cx) + (hy - cy) * (hy - cy)) / diameter;
                double hw = hMaxX - hMinX + 1, hh = hMaxY - hMinY + 1;
                double fill = holeArea / (hw * hh);
                double aspect = hw / hh;
                hasHole = holeArea >= 0.015 * filled && offset <= 0.12 &&
                          fill >= 0.60 && aspect >= 0.60 && aspect <= 1.67;
            }

            return new Coin
            {
                CenterX = b.MinX + cx + 0.5,
                CenterY = b.MinY + cy + 0.5,
                Diameter = diameter,
                Area = filled,
                FillRatio = filled / (double)(bw * bh),
                HasHole = hasHole,
                TouchesBorder = touches
            };
        }

        // -----------------------------------------------------------------
        //  Step 6: size clustering + denomination matching
        // -----------------------------------------------------------------

        private static void Classify(List<Coin> coins, int imageWidth, AnalysisResult result)
        {
            double prior = (imageWidth / RefImageWidth) * (RefPesoDiameterPx / 24.0);   // px per mm

            // Coins cut by the image border have unreliable sizes: keep them out of the fit.
            List<Coin> fit = coins.Where(c => !c.TouchesBorder).OrderBy(c => c.Diameter).ToList();

            // group similar diameters
            var clusters = new List<List<Coin>>();
            foreach (Coin c in fit)
            {
                if (clusters.Count == 0)
                    clusters.Add(new List<Coin>());
                else
                {
                    List<Coin> last = clusters[clusters.Count - 1];
                    if (c.Diameter / last[last.Count - 1].Diameter > ClusterGap)
                        clusters.Add(new List<Coin>());
                }
                clusters[clusters.Count - 1].Add(c);
            }

            double scale = prior;
            int k = clusters.Count;
            int d = Denomination.All.Length;

            if (k >= 1 && k <= d)
            {
                double[] means = clusters.Select(cl => cl.Average(c => c.Diameter)).ToArray();
                int[] counts = clusters.Select(cl => cl.Count).ToArray();
                int[] assign = BestAssignment(means, counts, prior, out scale);

                for (int i = 0; i < k; i++)
                    foreach (Coin c in clusters[i])
                        c.Denomination = Denomination.All[assign[i]];
            }
            else if (k > d)
            {
                result.Warnings.Add("More distinct coin sizes (" + k + ") than known denominations - " +
                                    "using the nearest size instead of a fitted scale.");
                foreach (Coin c in fit)
                    c.Denomination = NearestBySize(c.Diameter, scale);
            }

            // border coins: nearest size using the fitted scale
            int cut = 0;
            foreach (Coin c in coins)
            {
                if (!c.TouchesBorder) continue;
                c.Denomination = NearestBySize(c.Diameter, scale);
                cut++;
            }
            if (cut > 0)
                result.Warnings.Add(cut + " coin(s) touch the image edge - their size is a guess.");

            // a centred round hole always means 5 sentimo
            foreach (Coin c in coins)
                if (c.HasHole)
                    c.Denomination = Denomination.All[0];

            result.PixelsPerMm = scale;
        }

        /// <summary>
        /// Chooses which denominations the size groups are (smallest group -> smaller coin, in order)
        /// by least-squares fitting  diameter_px = scale * diameter_mm. Works even if some
        /// denominations are missing from the picture.
        /// </summary>
        private static int[] BestAssignment(double[] means, int[] counts, double prior, out double scale)
        {
            int k = means.Length;
            int d = Denomination.All.Length;
            int[] cur = new int[k];
            int[] best = new int[k];
            double bestErr = double.MaxValue;
            double bestScale = prior;

            void Search(int j, int start)
            {
                if (j == k)
                {
                    double num = 0, den = 0;
                    for (int i = 0; i < k; i++)
                    {
                        double mm = Denomination.All[cur[i]].DiameterMm;
                        num += counts[i] * means[i] * mm;
                        den += counts[i] * mm * mm;
                    }
                    double sc = num / den;

                    double err = 0;
                    for (int i = 0; i < k; i++)
                    {
                        double pred = sc * Denomination.All[cur[i]].DiameterMm;
                        double rel = (means[i] - pred) / means[i];
                        err += counts[i] * rel * rel;
                    }
                    double lr = Math.Log(sc / prior);
                    err += 0.02 * lr * lr;          // weak tie-breaker towards the expected scale

                    if (err < bestErr)
                    {
                        bestErr = err;
                        bestScale = sc;
                        Array.Copy(cur, best, k);
                    }
                    return;
                }
                for (int i = start; i <= d - (k - j); i++)
                {
                    cur[j] = i;
                    Search(j + 1, i + 1);
                }
            }

            Search(0, 0);
            scale = bestScale;
            return best;
        }

        private static Denomination NearestBySize(double diameterPx, double pxPerMm)
        {
            Denomination bestD = Denomination.All[0];
            double bestDiff = double.MaxValue;
            foreach (Denomination dn in Denomination.All)
            {
                double diff = Math.Abs(diameterPx - dn.DiameterMm * pxPerMm);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestD = dn;
                }
            }
            return bestD;
        }

        private static double Median(List<double> values)
        {
            values.Sort();
            int n = values.Count;
            return n % 2 == 1 ? values[n / 2] : (values[n / 2 - 1] + values[n / 2]) / 2.0;
        }
    }
}
