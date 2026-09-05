// Raster helpers for Tools\Make-Assets.ps1.
//
// Evania's mark is traced from the authored artwork in Assets\Source rather than re-entered by
// hand, so this file is geometry maths only: load, mask, trace, simplify, and the bevel pass that
// turns a flat fill into a pressed surface. Colour and composition stay in the PowerShell next to
// every other tunable, so a designer changes one file.
//
// Keep this class in sync with Make-Assets.ps1: the script Add-Type's it from disk on every run.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

public static class EvaniaMascot
{
    // ---- loading -------------------------------------------------------------------------------

    public sealed class Art
    {
        public int W;
        public int H;
        public byte[] Gray = Array.Empty<byte>();      // luma, 0..255
    }

    public static Art Load(string path)
    {
        Art a = new Art();
        using (Bitmap bmp = new Bitmap(path))
        {
            a.W = bmp.Width;
            a.H = bmp.Height;
            Rectangle rc = new Rectangle(0, 0, a.W, a.H);
            BitmapData bd = bmp.LockBits(rc, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] raw = new byte[Math.Abs(bd.Stride) * a.H];
            Marshal.Copy(bd.Scan0, raw, 0, raw.Length);
            bmp.UnlockBits(bd);

            a.Gray = new byte[a.W * a.H];
            for (int y = 0; y < a.H; y++)
            {
                int row = y * bd.Stride;
                for (int x = 0; x < a.W; x++)
                {
                    int i = row + x * 4;
                    int l = (int)(0.299 * raw[i + 2] + 0.587 * raw[i + 1] + 0.114 * raw[i]);
                    a.Gray[y * a.W + x] = (byte)(l < 0 ? 0 : (l > 255 ? 255 : l));
                }
            }
        }
        return a;
    }

    // ---- masks ---------------------------------------------------------------------------------

    // Dark pixels: the strokes and solid fills of the drawing.
    public static byte[] Ink(Art a, int threshold)
    {
        byte[] m = new byte[a.Gray.Length];
        for (int i = 0; i < m.Length; i++) m[i] = (byte)(a.Gray[i] < threshold ? 1 : 0);
        return m;
    }

    // Everything that is not the page the drawing sits on. The page is found rather than assumed
    // from a luminance cutoff, so a soft-shadowed source still yields a hard silhouette.
    public static byte[] Figure(Art a)
    {
        int w = a.W, h = a.H;
        byte[] light = new byte[w * h];
        for (int i = 0; i < light.Length; i++) light[i] = (byte)(a.Gray[i] >= 160 ? 1 : 0);

        int[] lab = new int[w * h];
        for (int i = 0; i < lab.Length; i++) lab[i] = -1;
        bool[] border = new bool[1];
        int id = 0;
        Queue<int> q = new Queue<int>();
        int[] pageIds = new int[w * h + 1];
        int pageCount = 0;

        for (int start = 0; start < w * h; start++)
        {
            if (light[start] == 0 || lab[start] >= 0) continue;
            lab[start] = id;
            q.Enqueue(start);
            bool touches = false;
            while (q.Count > 0)
            {
                int p = q.Dequeue();
                int px = p % w, py = p / w;
                if (px == 0 || py == 0 || px == w - 1 || py == h - 1) touches = true;
                int np;
                if (px + 1 < w) { np = p + 1; if (light[np] == 1 && lab[np] < 0) { lab[np] = id; q.Enqueue(np); } }
                if (px > 0) { np = p - 1; if (light[np] == 1 && lab[np] < 0) { lab[np] = id; q.Enqueue(np); } }
                if (py + 1 < h) { np = p + w; if (light[np] == 1 && lab[np] < 0) { lab[np] = id; q.Enqueue(np); } }
                if (py > 0) { np = p - w; if (light[np] == 1 && lab[np] < 0) { lab[np] = id; q.Enqueue(np); } }
            }
            if (touches)
            {
                if (pageCount < pageIds.Length) pageIds[pageCount++] = id;
            }
            id++;
        }

        HashSet<int> page = new HashSet<int>();
        for (int k = 0; k < pageCount; k++) page.Add(pageIds[k]);

        byte[] m = new byte[w * h];
        for (int i = 0; i < m.Length; i++)
        {
            bool isPage = light[i] == 1 && page.Contains(lab[i]);
            m[i] = (byte)(isPage ? 0 : 1);
        }
        return m;
    }

    public static byte[] Ellipse(int w, int h, double cx, double cy, double rx, double ry)
    {
        byte[] m = new byte[w * h];
        for (int y = 0; y < h; y++)
        {
            double dy = (y + 0.5 - cy) / ry;
            for (int x = 0; x < w; x++)
            {
                double dx = (x + 0.5 - cx) / rx;
                if (dx * dx + dy * dy <= 1.0) m[y * w + x] = 1;
            }
        }
        return m;
    }

    public static byte[] Box(int w, int h, int x0, int y0, int x1, int y1)
    {
        byte[] m = new byte[w * h];
        for (int y = Math.Max(0, y0); y < Math.Min(h, y1); y++)
            for (int x = Math.Max(0, x0); x < Math.Min(w, x1); x++) m[y * w + x] = 1;
        return m;
    }

    public static byte[] Union(params byte[][] ms)
    {
        byte[] r = new byte[ms[0].Length];
        for (int i = 0; i < r.Length; i++)
        {
            byte v = 0;
            for (int k = 0; k < ms.Length; k++) v |= ms[k][i];
            r[i] = v;
        }
        return r;
    }

    public static byte[] Subtract(byte[] a, byte[] b)
    {
        byte[] r = new byte[a.Length];
        for (int i = 0; i < r.Length; i++) r[i] = (byte)(a[i] != 0 && b[i] == 0 ? 1 : 0);
        return r;
    }

    // Grows a mask by r pixels. Used to widen the cut-out around a removed feature so no stub of
    // the original stroke survives at a zone boundary.
    public static byte[] Dilate(byte[] m, int w, int h, int r)
    {
        byte[] tmp = new byte[m.Length];
        byte[] outm = new byte[m.Length];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool on = false;
                for (int d = -r; d <= r && !on; d++)
                {
                    int sx = x + d;
                    if (sx < 0 || sx >= w) continue;
                    if (m[y * w + sx] != 0) on = true;
                }
                tmp[y * w + x] = (byte)(on ? 1 : 0);
            }
        }
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool on = false;
                for (int d = -r; d <= r && !on; d++)
                {
                    int sy = y + d;
                    if (sy < 0 || sy >= h) continue;
                    if (tmp[sy * w + x] != 0) on = true;
                }
                outm[y * w + x] = (byte)(on ? 1 : 0);
            }
        }
        return outm;
    }

    public static int Area(byte[] m)
    {
        int c = 0;
        for (int i = 0; i < m.Length; i++) if (m[i] != 0) c++;
        return c;
    }

    /// <summary>
    /// Clears ink islands that are stranded inside a window: components with no pixel on the
    /// window's border and no more than maxArea pixels. After the rims are lifted out of a drawing
    /// this is what removes the fragments they leave welded to the hair -- and because it demands
    /// that a fragment be cut off from the border of the window, the hair itself, which flows
    /// through the same window, can never be cleared by it.
    /// </summary>
    public static int ClearStrays(byte[] m, int w, int h, int x0, int y0, int x1, int y1, int maxArea)
    {
        int[] lab = new int[w * h];
        for (int i = 0; i < lab.Length; i++) lab[i] = -1;
        Queue<int> q = new Queue<int>();
        int removed = 0;
        int id = 0;

        for (int start = y0 * w + x0; start <= y1 * w + x1; start++)
        {
            if (m[start] == 0 || lab[start] >= 0) continue;
            int comp = id++;
            lab[start] = comp;
            q.Enqueue(start);
            List<int> members = new List<int>();
            bool touchesBorder = false;
            while (q.Count > 0)
            {
                int p = q.Dequeue();
                members.Add(p);
                int px = p % w, py = p / w;
                if (px <= x0 || py <= y0 || px >= x1 || py >= y1) touchesBorder = true;
                int np;
                if (px + 1 <= x1) { np = p + 1; if (m[np] != 0 && lab[np] < 0) { lab[np] = comp; q.Enqueue(np); } }
                if (px - 1 >= x0) { np = p - 1; if (m[np] != 0 && lab[np] < 0) { lab[np] = comp; q.Enqueue(np); } }
                if (py + 1 <= y1) { np = p + w; if (m[np] != 0 && lab[np] < 0) { lab[np] = comp; q.Enqueue(np); } }
                if (py - 1 >= y0) { np = p - w; if (m[np] != 0 && lab[np] < 0) { lab[np] = comp; q.Enqueue(np); } }
            }
            if (touchesBorder || members.Count > maxArea) continue;
            foreach (int p in members) { m[p] = 0; removed++; }
        }
        return removed;
    }

    public static int VertexCount(List<double[]> loops)
    {
        int c = 0;
        for (int i = 0; i < loops.Count; i++) c += loops[i].Length / 2;
        return c;
    }

    public static double[] Bounds(byte[] m, int w, int h)
    {
        double x0 = w, y0 = h, x1 = -1, y1 = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (m[y * w + x] == 0) continue;
                if (x < x0) x0 = x; if (x > x1) x1 = x;
                if (y < y0) y0 = y; if (y > y1) y1 = y;
            }
        if (x1 < 0) return new double[] { 0, 0, 0, 0 };
        return new double[] { x0, y0, x1 + 1, y1 + 1 };
    }

    // ---- tracing -------------------------------------------------------------------------------

    /// <summary>
    /// Marching squares over the mask, joined into closed loops, decimated, then corner-cut.
    /// Each loop is returned as a flat [x0,y0,x1,y1,...] array in source pixel coordinates.
    ///
    /// Crossings are stored as an undirected adjacency: a midpoint sits on one cell edge, which two
    /// cells share, and each of them contributes exactly one segment there -- so every midpoint has
    /// degree two and walking the graph yields clean loops with no orientation bookkeeping. Nested
    /// holes come for free because the caller fills all loops of a mask into one GraphicsPath with
    /// FillMode::Alternate: the ink has a face hole that carries a pupil island that carries a
    /// highlight hole, and parity is the only rule that gets all four levels right.
    /// </summary>
    public static List<double[]> Trace(byte[] mask, int w, int h, double eps, int passes)
    {
        int stride = 2 * w + 2;
        Dictionary<long, List<long>> adj = new Dictionary<long, List<long>>();

        for (int y = 0; y < h - 1; y++)
        {
            for (int x = 0; x < w - 1; x++)
            {
                int tl = mask[y * w + x];
                int tr = mask[y * w + x + 1];
                int br = mask[(y + 1) * w + x + 1];
                int bl = mask[(y + 1) * w + x];
                int idx = (tl << 3) | (tr << 2) | (br << 1) | bl;
                if (idx == 0 || idx == 15) continue;

                // Doubled lattice coordinates: 0=left, 1=top, 3=right, 2=bottom edge midpoints.
                long e0 = Enc(stride, 2 * x, 2 * y + 1);
                long e1 = Enc(stride, 2 * x + 1, 2 * y);
                long e2 = Enc(stride, 2 * x + 2, 2 * y + 1);
                long e3 = Enc(stride, 2 * x + 1, 2 * y + 2);

                switch (idx)
                {
                    case 1: Link(adj, e0, e3); break;         // bl
                    case 2: Link(adj, e3, e2); break;         // br
                    case 3: Link(adj, e0, e2); break;         // bl + br
                    case 4: Link(adj, e1, e2); break;         // tr
                    case 5: Link(adj, e1, e2); Link(adj, e0, e3); break;   // saddle tr + bl
                    case 6: Link(adj, e1, e3); break;         // tr + br
                    case 7: Link(adj, e1, e0); break;         // all but tl
                    case 8: Link(adj, e1, e0); break;         // tl
                    case 9: Link(adj, e1, e3); break;         // tl + bl
                    case 10: Link(adj, e1, e0); Link(adj, e3, e2); break;  // saddle tl + br
                    case 11: Link(adj, e1, e2); break;        // all but tr
                    case 12: Link(adj, e0, e2); break;        // tl + tr
                    case 13: Link(adj, e2, e3); break;        // all but br
                    case 14: Link(adj, e0, e3); break;        // all but bl
                }
            }
        }

        List<double[]> loops = new List<double[]>();
        List<long> keys = new List<long>(adj.Keys);
        keys.Sort();
        HashSet<long> used = new HashSet<long>();

        foreach (long start in keys)
        {
            if (used.Contains(EdgeKey(start, adj[start][0])) &&
                (adj[start].Count < 2 || used.Contains(EdgeKey(start, adj[start][1])))) continue;

            List<double> pts = new List<double>();
            long cur = start, prev = -1;
            int guard = 0, cap = w * h + 8;

            while (guard++ < cap)
            {
                pts.Add(DecX(stride, cur) * 0.5 + 0.5);
                pts.Add(DecY(stride, cur) * 0.5 + 0.5);

                long pick = -1;
                foreach (long cand in adj[cur])
                {
                    if (used.Contains(EdgeKey(cur, cand))) continue;
                    if (cand == prev && pts.Count > 2) continue;
                    pick = cand;
                    break;
                }
                if (pick < 0) break;
                used.Add(EdgeKey(cur, pick));
                prev = cur;
                cur = pick;
                if (cur == start) break;
            }

            double[] poly = new double[pts.Count];
            Array.Copy(pts.ToArray(), poly, pts.Count);
            if (poly.Length < 8) continue;
            if (Math.Abs(SignedArea(poly)) < 1.5) continue;
            poly = Simplify(poly, eps);
            poly = Smooth(poly, passes);
            if (poly.Length >= 8) loops.Add(poly);
        }
        return loops;
    }

    static long Enc(int stride, long gx, long gy) { return gy * stride + gx; }
    static int DecX(int stride, long k) { return (int)(k % stride); }
    static int DecY(int stride, long k) { return (int)(k / stride); }

    static long EdgeKey(long a, long b) { return a < b ? (a * 1000003L + b) : (b * 1000003L + a); }

    static void Link(Dictionary<long, List<long>> adj, long a, long b)
    {
        if (a == b) return;
        if (!adj.TryGetValue(a, out List<long>? la)) { la = new List<long>(2); adj[a] = la; }
        if (!adj.TryGetValue(b, out List<long>? lb)) { lb = new List<long>(2); adj[b] = lb; }
        la.Add(b);
        lb.Add(a);
    }

    public static double SignedArea(double[] p)
    {
        double a = 0;
        int n = p.Length / 2;
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            a += p[i * 2] * p[j * 2 + 1] - p[j * 2] * p[i * 2 + 1];
        }
        return a / 2.0;
    }

    static double[] Simplify(double[] p, double eps)
    {
        int n = p.Length / 2;
        if (n < 5 || eps <= 0) return p;
        bool[] keep = new bool[n];
        for (int i = 0; i < n; i++) keep[i] = false;
        keep[0] = true;
        keep[n - 1] = true;
        int far = 0;
        double best = -1;
        for (int i = 1; i < n; i++)
        {
            double d = Dist(p, 0, i);
            if (d > best) { best = d; far = i; }
        }
        keep[far] = true;
        Rdp(p, 0, far, eps * eps, keep);
        Rdp(p, far, n - 1, eps * eps, keep);
        List<double> outp = new List<double>();
        for (int i = 0; i < n; i++)
        {
            if (!keep[i]) continue;
            outp.Add(p[i * 2]);
            outp.Add(p[i * 2 + 1]);
        }
        return outp.ToArray();
    }

    static void Rdp(double[] p, int first, int last, double eps2, bool[] keep)
    {
        if (last <= first + 1) return;
        double maxd = -1;
        int index = -1;
        double x1 = p[first * 2], y1 = p[first * 2 + 1];
        double x2 = p[last * 2], y2 = p[last * 2 + 1];
        double dx = x2 - x1, dy = y2 - y1;
        double len2 = dx * dx + dy * dy;
        for (int i = first + 1; i < last; i++)
        {
            double px = p[i * 2], py = p[i * 2 + 1], d;
            if (len2 < 1e-12)
            {
                double ex = px - x1, ey = py - y1;
                d = ex * ex + ey * ey;
            }
            else
            {
                double t = ((px - x1) * dx + (py - y1) * dy) / len2;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double qx = x1 + t * dx - px, qy = y1 + t * dy - py;
                d = qx * qx + qy * qy;
            }
            if (d > maxd) { maxd = d; index = i; }
        }
        if (maxd > eps2 && index > first && index < last)
        {
            keep[index] = true;
            Rdp(p, first, index, eps2, keep);
            Rdp(p, index, last, eps2, keep);
        }
    }

    static double Dist(double[] p, int i, int j)
    {
        double dx = p[i * 2] - p[j * 2], dy = p[i * 2 + 1] - p[j * 2 + 1];
        return dx * dx + dy * dy;
    }

    static double[] Smooth(double[] p, int passes)
    {
        double[] cur = p;
        for (int k = 0; k < passes; k++) cur = Chaikin(cur);
        return cur;
    }

    // Closed corner cutting: every corner moves a quarter of the way along both adjacent edges, so
    // the staircase the trace produces relaxes into the curve the artwork intended. Three passes is
    // enough to lose the steps without rounding away a genuinely sharp tip like the star's points.
    static double[] Chaikin(double[] p)
    {
        int n = p.Length / 2;
        if (n < 3) return p;
        List<double> q = new List<double>(p.Length * 2);
        for (int i = 0; i < n; i++)
        {
            int j = (i + 1) % n;
            double ax = p[i * 2], ay = p[i * 2 + 1];
            double bx = p[j * 2], by = p[j * 2 + 1];
            q.Add(ax * 0.75 + bx * 0.25); q.Add(ay * 0.75 + by * 0.25);
            q.Add(ax * 0.25 + bx * 0.75); q.Add(ay * 0.25 + by * 0.75);
        }
        return q.ToArray();
    }

    // ---- emboss --------------------------------------------------------------------------------

    // Box-blurred coverage of an 8-bit alpha plane, separable so it stays linear in pixels.
    public static float[] SoftAlpha(byte[] src, int w, int h, int radius)
    {
        float[] a = new float[w * h];
        float[] b = new float[w * h];
        float[] c = new float[w * h];
        for (int i = 0; i < a.Length; i++) a[i] = src[i];
        int r = Math.Max(1, radius);
        int win = r * 2 + 1;

        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int d = -r; d <= r; d++) sum += a[row + Clampi(d, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                b[row + x] = sum / win;
                sum += a[row + Clampi(x + r + 1, 0, w - 1)] - a[row + Clampi(x - r, 0, w - 1)];
            }
        }
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int d = -r; d <= r; d++) sum += b[Clampi(d, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                c[y * w + x] = sum / win;
                sum += b[Clampi(y + r + 1, 0, h - 1) * w + x] - b[Clampi(y - r, 0, h - 1) * w + x];
            }
        }
        return c;
    }

    static int Clampi(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

    public static float[] BlurF32(float[] src, int w, int h, int radius)
    {
        float[] b = new float[src.Length];
        float[] c = new float[src.Length];
        int r = Math.Max(1, radius);
        int win = r * 2 + 1;
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            float sum = 0;
            for (int d = -r; d <= r; d++) sum += src[row + Clampi(d, 0, w - 1)];
            for (int x = 0; x < w; x++)
            {
                b[row + x] = sum / win;
                sum += src[row + Clampi(x + r + 1, 0, w - 1)] - src[row + Clampi(x - r, 0, w - 1)];
            }
        }
        for (int x = 0; x < w; x++)
        {
            float sum = 0;
            for (int d = -r; d <= r; d++) sum += b[Clampi(d, 0, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                c[y * w + x] = sum / win;
                sum += b[Clampi(y + r + 1, 0, h - 1) * w + x] - b[Clampi(y - r, 0, h - 1) * w + x];
            }
        }
        return c;
    }

    public static byte[] BlurU8(byte[] src, int w, int h, int radius)
    {
        float[] f = BlurF32(ToFloat(src), w, h, radius);
        byte[] o = new byte[f.Length];
        for (int i = 0; i < o.Length; i++) o[i] = (byte)(f[i] < 0 ? 0 : (f[i] > 255 ? 255 : (int)(f[i] + 0.5f)));
        return o;
    }

    static float[] ToFloat(byte[] src)
    {
        float[] f = new float[src.Length];
        for (int i = 0; i < f.Length; i++) f[i] = src[i];
        return f;
    }

    public static float[] ToFloat(byte[] src, double scale)
    {
        float[] f = new float[src.Length];
        for (int i = 0; i < f.Length; i++) f[i] = (float)(src[i] * scale);
        return f;
    }

    public static int[] SolidWithAlpha(byte[] alpha, int rgb, double scale)
    {
        int[] o = new int[alpha.Length];
        for (int i = 0; i < o.Length; i++)
        {
            int a = (int)(alpha[i] * scale + 0.5);
            if (a > 255) a = 255; else if (a < 0) a = 0;
            o[i] = (a << 24) | rgb;
        }
        return o;
    }

    /// <summary>
    /// Presses a flat fill into a surface. The gradient of the blurred coverage is the tilt of the
    /// bevel wall, so a key light from the upper left brightens every edge that faces it and darkens
    /// the ones that face away; a soft specular term rides on top of the bright side. Operates in
    /// place on a straight-alpha ARGB buffer. The interior is untouched because the gradient is zero
    /// there, which is why this reads as embossed rather than as a second lighting pass.
    /// </summary>
    public static void Bevel(int[] argb, int w, int h, float[] height,
                             double strength, double specular, double lightX, double lightY)
    {
        double ll = Math.Sqrt(lightX * lightX + lightY * lightY);
        if (ll < 1e-9) ll = 1;
        double nlx = lightX / ll, nly = lightY / ll;

        for (int y = 1; y < h - 1; y++)
        {
            int row = y * w;
            for (int x = 1; x < w - 1; x++)
            {
                int p = row + x;
                if (argb[p] == 0) continue;
                double gx = (height[p + 1] - height[p - 1]) * 0.5;
                double gy = (height[p + w] - height[p - w]) * 0.5;
                // Tilting toward the light means the height falls along the light direction.
                double dot = -(gx * nlx + gy * nly);
                double mul = 1.0 + strength * dot;
                if (mul < 0.25) mul = 0.25;
                double rim = dot > 0 ? Math.Pow(dot, 2.5) * specular : 0;

                int c = argb[p];
                int a = (c >> 24) & 0xFF;
                int r = Write8(((c >> 16) & 0xFF) * mul + 255.0 * rim);
                int g = Write8(((c >> 8) & 0xFF) * mul + 255.0 * rim);
                int b = Write8((c & 0xFF) * mul + 255.0 * rim);
                argb[p] = (a << 24) | (r << 16) | (g << 8) | b;
            }
        }
    }

    static int Write8(double v) { int i = (int)(v + 0.5); return i < 0 ? 0 : (i > 255 ? 255 : i); }

    public static int[] ToArgb(Bitmap bmp)
    {
        Rectangle rc = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData bd = bmp.LockBits(rc, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        int[] buf = new int[bmp.Width * bmp.Height];
        Marshal.Copy(bd.Scan0, buf, 0, buf.Length);
        bmp.UnlockBits(bd);
        return buf;
    }

    // Decodes a finished PNG into an ARGB buffer so the generator can interrogate its own output.
    // Every judgement about the artwork has to arrive as a measurement: the pipeline is verified by
    // numbers sampled at known source coordinates, not by someone looking at the picture.
    public static int[] ArgbFromBytes(byte[] png)
    {
        using (System.IO.MemoryStream ms = new System.IO.MemoryStream(png))
        using (Bitmap bmp = new Bitmap(ms))
        {
            return ToArgb(bmp);
        }
    }

    public static void FromArgb(Bitmap bmp, int[] buf)
    {
        Rectangle rc = new Rectangle(0, 0, bmp.Width, bmp.Height);
        BitmapData bd = bmp.LockBits(rc, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        Marshal.Copy(buf, 0, bd.Scan0, buf.Length);
        bmp.UnlockBits(bd);
    }

    public static byte[] AlphaOf(int[] argb)
    {
        byte[] a = new byte[argb.Length];
        for (int i = 0; i < a.Length; i++) a[i] = (byte)((argb[i] >> 24) & 0xFF);
        return a;
    }

    // Pulls one 8-bit channel out of an ARGB buffer as a standalone plane. Every per-pixel read in
    // the pipeline goes through here rather than through GetPixel: a 1500px master is 2.2M pixels
    // and GetPixel costs about a second per channel, which is unaffordable once per asset.
    public static byte[] ChannelOf(int[] argb, int shift)
    {
        byte[] a = new byte[argb.Length];
        for (int i = 0; i < a.Length; i++) a[i] = (byte)((argb[i] >> shift) & 0xFF);
        return a;
    }

    // ---- checks --------------------------------------------------------------------------------

    // Fraction of pixels where a re-rasterised trace disagrees with the mask it came from. This is
    // the only way the trace can be judged without eyes on the output.
    public static double MaskMismatch(Bitmap rendered, byte[] mask, int w, int h)
    {
        int[] argb = ToArgb(rendered);
        double err = 0;
        for (int i = 0; i < argb.Length; i++)
        {
            double cov = ((argb[i] >> 24) & 0xFF) / 255.0;
            err += Math.Abs(cov - mask[i]);
        }
        return err / argb.Length;
    }
}
