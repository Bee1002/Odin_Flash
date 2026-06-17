# Genera el set de iconos del proyecto desde source_icon.png (squircle + alpha).
# Preserva el diseño tal cual: fondo azul navy, bordes redondeados, sin marco blanco.
#
# Salida:
#   Assets/icon.ico          — multi-resolución (16–256) para exe, instalador, accesos directos
#   Assets/icon_master.png   — maestro 256 px
#   Assets/icons/icon_*.png  — tamaños individuales
#
# Uso:
#   powershell -ExecutionPolicy Bypass -File tools\Convert-PngToIcon.ps1

param(
    [string]$SourcePath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$assetsDir = Join-Path $root 'Assets'
$iconsDir = Join-Path $assetsDir 'icons'
$iconPath = Join-Path $assetsDir 'icon.ico'
$previewPath = Join-Path $assetsDir 'icon_master.png'
$sizes = @(16, 24, 32, 48, 64, 128, 256)

if ([string]::IsNullOrWhiteSpace($SourcePath)) {
    $SourcePath = Join-Path $assetsDir 'source_icon.png'
} elseif (-not [IO.Path]::IsPathRooted($SourcePath)) {
    $SourcePath = Join-Path $root $SourcePath
}

if (-not (Test-Path $SourcePath)) {
    throw "Missing source PNG: $SourcePath"
}

New-Item -ItemType Directory -Force -Path $assetsDir, $iconsDir | Out-Null
Add-Type -AssemblyName System.Drawing

$source = @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class OdinIconPipeline
{
    public static void Build(string pngPath, string iconPath, string previewPath, string iconsDir, int[] sizes)
    {
        using (Bitmap raw = new Bitmap(pngPath))
        using (Bitmap master = NormalizeSquare(raw))
        {
            master.Save(previewPath, ImageFormat.Png);

            foreach (int size in sizes)
            {
                using (Bitmap scaled = Render(master, size))
                {
                    string pngOut = Path.Combine(iconsDir, "icon_" + size + ".png");
                    scaled.Save(pngOut, ImageFormat.Png);
                }
            }

            SaveIcoPng(iconPath, sizes, master);
        }
    }

    static bool IsPhotoMargin(Color c)
    {
        if (c.A < 8) return false;
        int lum = (c.R + c.G + c.B) / 3;
        int spread = Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        return lum >= 180 && spread <= 30;
    }

    static Rectangle DetectAlphaBounds(Bitmap bmp)
    {
        int minX = bmp.Width, minY = bmp.Height, maxX = 0, maxY = 0;
        bool found = false;

        for (int y = 0; y < bmp.Height; y++)
        {
            for (int x = 0; x < bmp.Width; x++)
            {
                Color c = bmp.GetPixel(x, y);
                if (c.A < 12) continue;
                if (IsPhotoMargin(c)) continue;

                found = true;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        if (!found)
            return new Rectangle(0, 0, bmp.Width, bmp.Height);

        return Rectangle.FromLTRB(minX, minY, maxX + 1, maxY + 1);
    }

    static Bitmap NormalizeSquare(Bitmap raw)
    {
        Rectangle bounds = DetectAlphaBounds(raw);
        int side = Math.Max(bounds.Width, bounds.Height);

        Bitmap square = new Bitmap(side, side, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(square))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.CompositingMode = CompositingMode.SourceCopy;

            int ox = (side - bounds.Width) / 2;
            int oy = (side - bounds.Height) / 2;
            g.DrawImage(raw, new Rectangle(ox, oy, bounds.Width, bounds.Height), bounds, GraphicsUnit.Pixel);
        }

        return square;
    }

    static Bitmap Render(Bitmap source, int size)
    {
        Bitmap bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.CompositingQuality = CompositingQuality.HighQuality;
            g.CompositingMode = CompositingMode.SourceOver;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        return bmp;
    }

    static void SaveIcoPng(string path, int[] sizes, Bitmap master)
    {
        using (MemoryStream ms = new MemoryStream())
        using (BinaryWriter bw = new BinaryWriter(ms))
        {
            bw.Write((ushort)0);
            bw.Write((ushort)1);
            bw.Write((ushort)sizes.Length);

            byte[][] images = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
            {
                using (Bitmap bmp = Render(master, sizes[i]))
                using (MemoryStream pngMs = new MemoryStream())
                {
                    bmp.Save(pngMs, ImageFormat.Png);
                    images[i] = pngMs.ToArray();
                }
            }

            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                int s = sizes[i];
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)(s >= 256 ? 0 : s));
                bw.Write((byte)0);
                bw.Write((byte)0);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(images[i].Length);
                bw.Write(offset);
                offset += images[i].Length;
            }

            for (int i = 0; i < sizes.Length; i++)
                bw.Write(images[i]);

            File.WriteAllBytes(path, ms.ToArray());
        }
    }
}
'@

if (-not ([System.Management.Automation.PSTypeName]'OdinIconPipeline').Type) {
    Add-Type -TypeDefinition $source -ReferencedAssemblies System.Drawing
}

[OdinIconPipeline]::Build($SourcePath, $iconPath, $previewPath, $iconsDir, $sizes)

Write-Host "OK master:  $previewPath" -ForegroundColor Green
Write-Host "OK ico:     $iconPath" -ForegroundColor Green
Write-Host "OK sizes:   $iconsDir\icon_*.png" -ForegroundColor Green
exit 0
