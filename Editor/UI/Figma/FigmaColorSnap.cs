using System;
using System.Collections.Generic;
using UnityEngine;

namespace Molca.Editor.UI.Figma
{
    /// <summary>
    /// Snaps an arbitrary Figma fill to the nearest catalog color, using CIEDE2000 (perceptual) distance
    /// in CIE-Lab — so "this fill ≈ <c>color/primary</c>" is a perceptual match, not a naive RGB one. This
    /// is the deterministic core of the token pre-pass (Sprint 58.3): the math is pure and testable; the
    /// catalog→palette resolution (token → actual <c>ColorID</c> color) is supplied by the caller.
    /// </summary>
    public static class FigmaColorSnap
    {
        /// <summary>A candidate palette entry: a token id and the color it currently resolves to.</summary>
        public readonly struct PaletteEntry
        {
            public readonly string TokenId;
            public readonly Color Color;
            public PaletteEntry(string tokenId, Color color) { TokenId = tokenId; Color = color; }
        }

        /// <summary>
        /// Returns the palette entry perceptually closest to <paramref name="target"/>, with the CIEDE2000
        /// <paramref name="distance"/> (0 = identical; smaller = closer). <paramref name="tokenId"/> is null
        /// and the method returns false when the palette is empty.
        /// </summary>
        public static bool TryNearest(IReadOnlyList<PaletteEntry> palette, Color target,
            out string tokenId, out double distance)
        {
            tokenId = null;
            distance = double.MaxValue;
            if (palette == null || palette.Count == 0) return false;

            var targetLab = RgbToLab(target);
            for (int i = 0; i < palette.Count; i++)
            {
                double d = CIEDE2000(targetLab, RgbToLab(palette[i].Color));
                if (d < distance)
                {
                    distance = d;
                    tokenId = palette[i].TokenId;
                }
            }
            return tokenId != null;
        }

        /// <summary>The CIEDE2000 color difference ΔE00 between two sRGB colors (alpha ignored).</summary>
        public static double Difference(Color a, Color b) => CIEDE2000(RgbToLab(a), RgbToLab(b));

        // ── CIE-Lab conversion (sRGB → linear → XYZ (D65) → Lab) ────────────────────────────────

        private readonly struct Lab { public readonly double L, A, B; public Lab(double l, double a, double b) { L = l; A = a; B = b; } }

        private static Lab RgbToLab(Color c)
        {
            double r = SrgbToLinear(c.r), g = SrgbToLinear(c.g), b = SrgbToLinear(c.b);

            // Linear sRGB → XYZ (D65), then normalize by the D65 white point.
            double x = (r * 0.4124564 + g * 0.3575761 + b * 0.1804375) / 0.95047;
            double y = (r * 0.2126729 + g * 0.7151522 + b * 0.0721750) / 1.00000;
            double z = (r * 0.0193339 + g * 0.1191920 + b * 0.9503041) / 1.08883;

            double fx = LabF(x), fy = LabF(y), fz = LabF(z);
            return new Lab(116.0 * fy - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
        }

        private static double SrgbToLinear(double v) =>
            v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);

        private static double LabF(double t) =>
            t > 0.008856 ? Math.Pow(t, 1.0 / 3.0) : (7.787 * t) + (16.0 / 116.0);

        // ── CIEDE2000 ΔE00 (Sharma et al.) ───────────────────────────────────────────────────────

        private static double CIEDE2000(Lab a, Lab b)
        {
            const double deg2rad = Math.PI / 180.0;
            const double rad2deg = 180.0 / Math.PI;

            double lBarPrime = (a.L + b.L) / 2.0;
            double c1 = Math.Sqrt(a.A * a.A + a.B * a.B);
            double c2 = Math.Sqrt(b.A * b.A + b.B * b.B);
            double cBar = (c1 + c2) / 2.0;

            double cBar7 = Math.Pow(cBar, 7);
            double g = 0.5 * (1.0 - Math.Sqrt(cBar7 / (cBar7 + Math.Pow(25.0, 7))));

            double a1p = a.A * (1.0 + g);
            double a2p = b.A * (1.0 + g);
            double c1p = Math.Sqrt(a1p * a1p + a.B * a.B);
            double c2p = Math.Sqrt(a2p * a2p + b.B * b.B);
            double cBarPrime = (c1p + c2p) / 2.0;

            double h1p = Hue(a.B, a1p);
            double h2p = Hue(b.B, a2p);

            double dLp = b.L - a.L;
            double dCp = c2p - c1p;

            double dhp;
            if (c1p * c2p == 0.0) dhp = 0.0;
            else
            {
                double diff = h2p - h1p;
                if (diff > 180.0) diff -= 360.0;
                else if (diff < -180.0) diff += 360.0;
                dhp = diff;
            }
            double dHp = 2.0 * Math.Sqrt(c1p * c2p) * Math.Sin((dhp * deg2rad) / 2.0);

            double hBarPrime;
            if (c1p * c2p == 0.0) hBarPrime = h1p + h2p;
            else
            {
                double sum = h1p + h2p;
                if (Math.Abs(h1p - h2p) > 180.0) sum += (sum < 360.0) ? 360.0 : -360.0;
                hBarPrime = sum / 2.0;
            }

            double t = 1.0
                - 0.17 * Math.Cos((hBarPrime - 30.0) * deg2rad)
                + 0.24 * Math.Cos((2.0 * hBarPrime) * deg2rad)
                + 0.32 * Math.Cos((3.0 * hBarPrime + 6.0) * deg2rad)
                - 0.20 * Math.Cos((4.0 * hBarPrime - 63.0) * deg2rad);

            double dTheta = 30.0 * Math.Exp(-Math.Pow((hBarPrime - 275.0) / 25.0, 2));
            double cBarPrime7 = Math.Pow(cBarPrime, 7);
            double rc = 2.0 * Math.Sqrt(cBarPrime7 / (cBarPrime7 + Math.Pow(25.0, 7)));
            double rt = -rc * Math.Sin((2.0 * dTheta) * deg2rad);

            double lBarMinus50Sq = (lBarPrime - 50.0) * (lBarPrime - 50.0);
            double sl = 1.0 + (0.015 * lBarMinus50Sq) / Math.Sqrt(20.0 + lBarMinus50Sq);
            double sc = 1.0 + 0.045 * cBarPrime;
            double sh = 1.0 + 0.015 * cBarPrime * t;

            double termL = dLp / sl;
            double termC = dCp / sc;
            double termH = dHp / sh;

            _ = rad2deg; // kept for clarity of the deg/rad pairing above
            return Math.Sqrt(termL * termL + termC * termC + termH * termH + rt * termC * termH);
        }

        private static double Hue(double b, double ap)
        {
            if (b == 0.0 && ap == 0.0) return 0.0;
            double angle = Math.Atan2(b, ap) * (180.0 / Math.PI);
            return angle >= 0.0 ? angle : angle + 360.0;
        }
    }
}
