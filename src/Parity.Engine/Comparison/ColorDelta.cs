using Parity.Engine.Model;

namespace Parity.Engine.Comparison;

/// <summary>
/// 色差計算:sRGB → CIELAB(D65)→ CIEDE2000。
/// 比 hex 全等聰明:ΔE ≈ 1 是人眼可辨門檻,預設容差 2.0(規畫書 4.8)。
/// </summary>
public static class ColorDelta
{
    public static double Ciede2000(Rgba x, Rgba y) => Ciede2000(ToLab(x), ToLab(y));

    /// <summary>sRGB(0–255)→ CIELAB,D65 白點。</summary>
    public static (double L, double A, double B) ToLab(Rgba c)
    {
        // 1. 反 gamma(sRGB → linear)
        static double Lin(byte v)
        {
            var s = v / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        double r = Lin(c.R), g = Lin(c.G), b = Lin(c.B);

        // 2. linear RGB → XYZ(D65)
        double x = 0.4124564 * r + 0.3575761 * g + 0.1804375 * b;
        double y = 0.2126729 * r + 0.7151522 * g + 0.0721750 * b;
        double z = 0.0193339 * r + 0.1191920 * g + 0.9503041 * b;

        // 3. XYZ → Lab
        const double Xn = 0.95047, Yn = 1.0, Zn = 1.08883;
        static double F(double t) => t > 216.0 / 24389.0
            ? Math.Cbrt(t)
            : (24389.0 / 27.0 * t + 16.0) / 116.0;
        double fx = F(x / Xn), fy = F(y / Yn), fz = F(z / Zn);
        return (116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    /// <summary>CIEDE2000(kL=kC=kH=1),依 Sharma et al. 2005 的標準公式實作。</summary>
    public static double Ciede2000((double L, double A, double B) lab1, (double L, double A, double B) lab2)
    {
        const double Deg2Rad = Math.PI / 180.0;

        double c1 = Math.Sqrt(lab1.A * lab1.A + lab1.B * lab1.B);
        double c2 = Math.Sqrt(lab2.A * lab2.A + lab2.B * lab2.B);
        double cBar = (c1 + c2) / 2.0;

        double cBar7 = Math.Pow(cBar, 7);
        double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25.0, 7))));

        double a1p = (1.0 + g) * lab1.A;
        double a2p = (1.0 + g) * lab2.A;
        double c1p = Math.Sqrt(a1p * a1p + lab1.B * lab1.B);
        double c2p = Math.Sqrt(a2p * a2p + lab2.B * lab2.B);

        static double HueDeg(double b, double ap)
        {
            if (b == 0 && ap == 0) return 0;
            var h = Math.Atan2(b, ap) * (180.0 / Math.PI);
            return h < 0 ? h + 360.0 : h;
        }
        double h1p = HueDeg(lab1.B, a1p);
        double h2p = HueDeg(lab2.B, a2p);

        double dLp = lab2.L - lab1.L;
        double dCp = c2p - c1p;

        double dhp;
        if (c1p * c2p == 0) dhp = 0;
        else
        {
            var diff = h2p - h1p;
            dhp = Math.Abs(diff) <= 180 ? diff : diff > 180 ? diff - 360 : diff + 360;
        }
        double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin(dhp / 2.0 * Deg2Rad);

        double lBarP = (lab1.L + lab2.L) / 2.0;
        double cBarP = (c1p + c2p) / 2.0;

        double hBarP;
        if (c1p * c2p == 0) hBarP = h1p + h2p;
        else
        {
            var sum = h1p + h2p;
            hBarP = Math.Abs(h1p - h2p) <= 180 ? sum / 2.0
                : sum < 360 ? (sum + 360) / 2.0
                : (sum - 360) / 2.0;
        }

        double t = 1.0
            - 0.17 * Math.Cos((hBarP - 30.0) * Deg2Rad)
            + 0.24 * Math.Cos(2.0 * hBarP * Deg2Rad)
            + 0.32 * Math.Cos((3.0 * hBarP + 6.0) * Deg2Rad)
            - 0.20 * Math.Cos((4.0 * hBarP - 63.0) * Deg2Rad);

        double dTheta = 30.0 * Math.Exp(-Math.Pow((hBarP - 275.0) / 25.0, 2));
        double cBarP7 = Math.Pow(cBarP, 7);
        double rc = 2.0 * Math.Sqrt(cBarP7 / (cBarP7 + Math.Pow(25.0, 7)));

        double lm50 = (lBarP - 50.0) * (lBarP - 50.0);
        double sl = 1.0 + 0.015 * lm50 / Math.Sqrt(20.0 + lm50);
        double sc = 1.0 + 0.045 * cBarP;
        double sh = 1.0 + 0.015 * cBarP * t;
        double rt = -Math.Sin(2.0 * dTheta * Deg2Rad) * rc;

        double dl = dLp / sl, dc = dCp / sc, dh = dHp / sh;
        return Math.Sqrt(dl * dl + dc * dc + dh * dh + rt * dc * dh);
    }
}
