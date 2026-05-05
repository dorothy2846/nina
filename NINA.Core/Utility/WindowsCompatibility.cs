#region "copyright"

/*
    Copyright © 2016 - 2026 Stefan Berg <isbeorn86+NINA@googlemail.com> and the N.I.N.A. contributors

    This file is part of N.I.N.A. - Nighttime Imaging 'N' Astronomy.

    This Source Code Form is subject to the terms of the Mozilla Public
    License, v. 2.0. If a copy of the MPL was not distributed with this
    file, You can obtain one at http://mozilla.org/MPL/2.0/.
*/

#endregion "copyright"

// Compatibility shims for WPF types when building for non-Windows targets (e.g., linux-x64).
// These stubs allow the codebase to compile on net9.0 without WPF while keeping the
// net10.0-windows build unchanged (where the real WPF types are used).

#if !WINDOWS
using System;

namespace System.Windows.Media {
    public class SolidColorBrush {
        public SolidColorBrush(Color color) {
            Color = color;
        }

        public Color Color { get; set; }
    }

    public struct Color : IEquatable<Color> {
        public byte A { get; set; }
        public byte R { get; set; }
        public byte G { get; set; }
        public byte B { get; set; }

        public static Color FromArgb(byte a, byte r, byte g, byte b) =>
            new Color { A = a, R = r, G = g, B = b };

        public static Color FromRgb(byte r, byte g, byte b) =>
            new Color { A = 255, R = r, G = g, B = b };

        public override string ToString() => $"#{A:X2}{R:X2}{G:X2}{B:X2}";

        public bool Equals(Color other) => A == other.A && R == other.R && G == other.G && B == other.B;
        public override bool Equals(object obj) => obj is Color c && Equals(c);
        public override int GetHashCode() => HashCode.Combine(A, R, G, B);
        public static bool operator ==(Color left, Color right) => left.Equals(right);
        public static bool operator !=(Color left, Color right) => !left.Equals(right);
    }

    public static class Colors {
        public static Color Blue => Color.FromRgb(0, 0, 255);
        public static Color Red => Color.FromRgb(255, 0, 0);
        public static Color Orange => Color.FromRgb(255, 165, 0);
        public static Color Green => Color.FromRgb(0, 128, 0);
        public static Color White => Color.FromRgb(255, 255, 255);
        public static Color Black => Color.FromRgb(0, 0, 0);
        public static Color LawnGreen => Color.FromRgb(124, 252, 0);
        public static Color Transparent => Color.FromArgb(0, 255, 255, 255);
    }
}

namespace System.ComponentModel {
    public struct SortDescription {
        public SortDescription(string propertyName, ListSortDirection direction) {
            PropertyName = propertyName;
            Direction = direction;
        }

        public string PropertyName { get; }
        public ListSortDirection Direction { get; }
    }

    public interface ICollectionView {
        System.Collections.IList SortDescriptions { get; }
        System.Collections.IList GroupDescriptions { get; }
        Predicate<object> Filter { get; set; }
        void Refresh();
    }

    public class CollectionView : ICollectionView {
        public System.Collections.IList SortDescriptions { get; } = new System.Collections.ArrayList();
        public System.Collections.IList GroupDescriptions { get; } = new System.Collections.ArrayList();
        public Predicate<object> Filter { get; set; }
        public void Refresh() { }
    }
}

namespace System.Windows.Controls {
    public class Control {
    }

    public class TextBox : Control {
        public static readonly object TextProperty = new object();
        public object ToolTip { get; set; }

        public System.Windows.Data.BindingExpression GetBindingExpression(object dp) {
            return new System.Windows.Data.BindingExpression();
        }
    }
}

namespace Dasync.Collections {
    public static class CollectionExtensions {
    }
}

namespace NINA.Core.Utility.Notification {
    public enum NotificationWorkArea {
        PrimaryScreen,
        MainWindow,
        SameScreenAsApplication
    }

    public enum NotificationCorner {
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight
    }

    public static class Notification {
        public static void ShowError(string message) {
            Logger.Error(message);
        }
        public static void ShowWarning(string message) {
            Logger.Warning(message);
        }
        public static void ShowWarning(string message, TimeSpan duration) {
            Logger.Warning(message);
        }
        public static void ShowInformation(string message) {
            Logger.Info(message);
        }
        public static void ShowSuccess(string message) {
            Logger.Info(message);
        }
        public static void ShowExternalError(string message, string url) {
            Logger.Error(message);
        }
        public static void ShowExternalWarning(string message, string title) {
            Logger.Warning($"{title}: {message}");
        }
    }
}

namespace System.Windows.Media {
    public struct PixelFormat {
        public string Name { get; set; }
        public int BitsPerPixel { get; set; }
        public override bool Equals(object obj) => obj is PixelFormat pf && Name == pf.Name;
        public override int GetHashCode() => Name?.GetHashCode() ?? 0;
        public static bool operator ==(PixelFormat left, PixelFormat right) => left.Equals(right);
        public static bool operator !=(PixelFormat left, PixelFormat right) => !left.Equals(right);
    }

    public static class PixelFormats {
        public static PixelFormat Gray16 => new PixelFormat { Name = "Gray16", BitsPerPixel = 16 };
        public static PixelFormat Gray8 => new PixelFormat { Name = "Gray8", BitsPerPixel = 8 };
        public static PixelFormat Rgb48 => new PixelFormat { Name = "Rgb48", BitsPerPixel = 48 };
        public static PixelFormat Bgr565 => new PixelFormat { Name = "Bgr565", BitsPerPixel = 16 };
        public static PixelFormat Bgra32 => new PixelFormat { Name = "Bgra32", BitsPerPixel = 32 };
        public static PixelFormat Bgr32 => new PixelFormat { Name = "Bgr32", BitsPerPixel = 32 };
        public static PixelFormat Rgb24 => new PixelFormat { Name = "Rgb24", BitsPerPixel = 24 };
        public static PixelFormat Bgr24 => new PixelFormat { Name = "Bgr24", BitsPerPixel = 24 };
        public static PixelFormat Pbgra32 => new PixelFormat { Name = "Pbgra32", BitsPerPixel = 32 };
        public static PixelFormat Indexed8 => new PixelFormat { Name = "Indexed8", BitsPerPixel = 8 };
    }

    public class ScaleTransform {
        public ScaleTransform(double scaleX, double scaleY) { }
    }
}

namespace System.Windows.Media.Imaging {
    /// <summary>Cross-platform stand-in for WPF's BitmapSource. Holds the
    /// pixel buffer in a managed byte array — that's the part the original
    /// stub omitted, and why every NINA.Image stretch / debayer through this
    /// path silently lost its pixels on Linux/macOS. The Windows build keeps
    /// using the real WPF type via #if WINDOWS, so this only kicks in on
    /// non-Windows targets.</summary>
    public class BitmapSource {
        public int PixelWidth { get; set; }
        public int PixelHeight { get; set; }
        public double Width => PixelWidth;
        public double Height => PixelHeight;
        public PixelFormat Format { get; set; }
        public int DpiX { get; set; } = 96;
        public int DpiY { get; set; } = 96;

        // Backing pixel buffer + stride. Allocated by Create / WritePixels;
        // CopyPixels reads from here. Stride may differ from PixelWidth*bpp/8
        // for word-aligned formats — preserve the original.
        protected internal byte[]? _pixels;
        protected internal int _stride;

        public void Freeze() { }

        public void CopyPixels(byte[] buffer, int stride, int offset) {
            if (_pixels == null) return;
            int len = Math.Min(_pixels.Length, buffer.Length - offset);
            Buffer.BlockCopy(_pixels, 0, buffer, offset, len);
        }
        public void CopyPixels(ushort[] buffer, int stride, int offset) {
            if (_pixels == null) return;
            int byteLen = Math.Min(_pixels.Length, (buffer.Length - offset) * 2);
            Buffer.BlockCopy(_pixels, 0, buffer, offset * 2, byteLen);
        }
        public void CopyPixels(Array buffer, int stride, int offset) {
            if (_pixels == null) return;
            int elementSize = Buffer.ByteLength(buffer) / Math.Max(1, buffer.Length);
            int byteLen = Math.Min(_pixels.Length, Buffer.ByteLength(buffer) - offset * elementSize);
            Buffer.BlockCopy(_pixels, 0, buffer, offset * elementSize, byteLen);
        }
        public void CopyPixels(Int32Rect sourceRect, Array buffer, int stride, int offset) {
            if (_pixels == null) return;
            int bpp = Math.Max(1, Format.BitsPerPixel / 8);
            int srcStride = _stride > 0 ? _stride : PixelWidth * bpp;
            int rectW = sourceRect.Width <= 0 ? PixelWidth : sourceRect.Width;
            int rectH = sourceRect.Height <= 0 ? PixelHeight : sourceRect.Height;
            int rowBytes = rectW * bpp;
            int destBytesPerEntry = Buffer.ByteLength(buffer) / Math.Max(1, buffer.Length);
            for (int row = 0; row < rectH; row++) {
                int srcOff = (sourceRect.Y + row) * srcStride + sourceRect.X * bpp;
                int dstByteOff = offset * destBytesPerEntry + row * stride;
                int n = Math.Min(rowBytes, _pixels.Length - srcOff);
                if (n <= 0) break;
                Buffer.BlockCopy(_pixels, srcOff, buffer, dstByteOff, n);
            }
        }
        public void CopyPixels(Int32Rect sourceRect, IntPtr buffer, int bufferSize, int stride) {
            if (_pixels == null || buffer == IntPtr.Zero) return;
            int bpp = Math.Max(1, Format.BitsPerPixel / 8);
            int srcStride = _stride > 0 ? _stride : PixelWidth * bpp;
            int rectW = sourceRect.Width <= 0 ? PixelWidth : sourceRect.Width;
            int rectH = sourceRect.Height <= 0 ? PixelHeight : sourceRect.Height;
            int rowBytes = rectW * bpp;
            for (int row = 0; row < rectH; row++) {
                int srcOff = (sourceRect.Y + row) * srcStride + sourceRect.X * bpp;
                int n = Math.Min(rowBytes, _pixels.Length - srcOff);
                if (n <= 0) break;
                System.Runtime.InteropServices.Marshal.Copy(_pixels, srcOff, buffer + row * stride, n);
            }
        }

        public static BitmapSource Create(int width, int height, double dpiX, double dpiY, PixelFormat format, object palette, Array pixels, int stride) {
            var bs = new BitmapSource { PixelWidth = width, PixelHeight = height, Format = format, DpiX = (int)dpiX, DpiY = (int)dpiY, _stride = stride };
            int bytes = Buffer.ByteLength(pixels);
            bs._pixels = new byte[bytes];
            Buffer.BlockCopy(pixels, 0, bs._pixels, 0, bytes);
            return bs;
        }
        public static BitmapSource Create(int width, int height, double dpiX, double dpiY, PixelFormat format, object palette, IntPtr buffer, int bufferSize, int stride) {
            var bs = new BitmapSource { PixelWidth = width, PixelHeight = height, Format = format, DpiX = (int)dpiX, DpiY = (int)dpiY, _stride = stride };
            bs._pixels = new byte[bufferSize];
            if (buffer != IntPtr.Zero) {
                System.Runtime.InteropServices.Marshal.Copy(buffer, bs._pixels, 0, bufferSize);
            }
            return bs;
        }
    }


    public class WriteableBitmap : BitmapSource {
        // Pin the managed byte array so consumers calling BackBuffer get a
        // stable IntPtr they can write through. NINA's stretch path uses
        // this to write the stretch map output via Marshal.Copy.
        private System.Runtime.InteropServices.GCHandle _pinnedHandle;

        public WriteableBitmap(int width, int height, double dpiX, double dpiY, PixelFormat format, object palette) {
            PixelWidth = width; PixelHeight = height; Format = format;
            DpiX = (int)dpiX; DpiY = (int)dpiY;
            int bpp = Math.Max(1, format.BitsPerPixel / 8);
            _stride = width * bpp;
            _pixels = new byte[height * _stride];
            _pinnedHandle = System.Runtime.InteropServices.GCHandle.Alloc(_pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        }
        public WriteableBitmap(BitmapSource source) {
            PixelWidth = source.PixelWidth; PixelHeight = source.PixelHeight; Format = source.Format;
            DpiX = source.DpiX; DpiY = source.DpiY;
            int bpp = Math.Max(1, Format.BitsPerPixel / 8);
            _stride = source._stride > 0 ? source._stride : PixelWidth * bpp;
            int sz = PixelHeight * _stride;
            _pixels = new byte[sz];
            if (source._pixels != null) {
                Buffer.BlockCopy(source._pixels, 0, _pixels, 0, Math.Min(source._pixels.Length, sz));
            }
            _pinnedHandle = System.Runtime.InteropServices.GCHandle.Alloc(_pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        }
        public IntPtr BackBuffer => _pinnedHandle.IsAllocated ? _pinnedHandle.AddrOfPinnedObject() : IntPtr.Zero;
        public int BackBufferStride => _stride > 0 ? _stride : PixelWidth * Math.Max(1, Format.BitsPerPixel / 8);
        public void Lock() { }
        public void Unlock() { }
        public void WritePixels(Int32Rect rect, Array buffer, int stride, int offset) {
            if (_pixels == null) return;
            int bpp = Math.Max(1, Format.BitsPerPixel / 8);
            int rectW = rect.Width <= 0 ? PixelWidth : rect.Width;
            int rectH = rect.Height <= 0 ? PixelHeight : rect.Height;
            int rowBytes = rectW * bpp;
            int srcByteOff = offset * (Buffer.ByteLength(buffer) / Math.Max(1, buffer.Length));
            for (int row = 0; row < rectH; row++) {
                int dstOff = (rect.Y + row) * _stride + rect.X * bpp;
                int srcOff = srcByteOff + row * stride;
                int n = Math.Min(rowBytes, _pixels.Length - dstOff);
                if (n <= 0) break;
                Buffer.BlockCopy(buffer, srcOff, _pixels, dstOff, n);
            }
        }
        public void AddDirtyRect(Int32Rect rect) { }

        ~WriteableBitmap() {
            if (_pinnedHandle.IsAllocated) _pinnedHandle.Free();
        }
    }

    public class FormatConvertedBitmap : BitmapSource {
        public FormatConvertedBitmap() { }
        public FormatConvertedBitmap(BitmapSource source, PixelFormat format, object palette, double alphaThreshold) {
            PixelWidth = source.PixelWidth; PixelHeight = source.PixelHeight; Format = format;
        }
        public BitmapSource Source { get; set; }
        public PixelFormat DestinationFormat { get; set; }
        public void BeginInit() { }
        public void EndInit() { }
    }

    public class BitmapDecoder {
        public System.Collections.Generic.List<BitmapFrame> Frames { get; set; } = new System.Collections.Generic.List<BitmapFrame>();
        public static BitmapDecoder Create(System.IO.Stream stream, BitmapCreateOptions options, BitmapCacheOption cache) {
            return new BitmapDecoder();
        }
    }

    public class BitmapFrame : BitmapSource {
        public BitmapMetadata Metadata { get; set; }
        public static BitmapFrame Create(BitmapSource source) => new BitmapFrame { PixelWidth = source.PixelWidth, PixelHeight = source.PixelHeight, Format = source.Format };
        public static BitmapFrame Create(BitmapSource source, object thumbnail, BitmapMetadata metadata, object colorContexts) => Create(source);
    }

    public class BitmapMetadata {
        public BitmapMetadata(string format) { }
        public string ApplicationName { get; set; }
        public string Title { get; set; }
        public string GetQuery(string query) => null;
        public void SetQuery(string query, object value) { }
    }

    public class TransformedBitmap : BitmapSource {
        public TransformedBitmap(BitmapSource source, ScaleTransform transform) {
            PixelWidth = source.PixelWidth; PixelHeight = source.PixelHeight; Format = source.Format;
        }
    }

    public class CroppedBitmap : BitmapSource {
        public CroppedBitmap(BitmapSource source, Int32Rect rect) {
            PixelWidth = rect.Width; PixelHeight = rect.Height; Format = source.Format;
        }
    }

    public enum BitmapCreateOptions { None = 0, PreservePixelFormat = 1, IgnoreColorProfile = 2 }
    public enum BitmapCacheOption { Default = 0, OnLoad = 1 }

    public class GifBitmapDecoder : BitmapDecoder {
        public GifBitmapDecoder(Uri uri, BitmapCreateOptions options, BitmapCacheOption cache) { }
    }
    public class TiffBitmapDecoder : BitmapDecoder {
        public TiffBitmapDecoder(Uri uri, BitmapCreateOptions options, BitmapCacheOption cache) { }
        public TiffBitmapDecoder(System.IO.Stream stream, BitmapCreateOptions options, BitmapCacheOption cache) { }
    }
    public class JpegBitmapDecoder : BitmapDecoder {
        public JpegBitmapDecoder(Uri uri, BitmapCreateOptions options, BitmapCacheOption cache) { }
        public JpegBitmapDecoder(System.IO.Stream stream, BitmapCreateOptions options, BitmapCacheOption cache) { }
    }
    public class PngBitmapDecoder : BitmapDecoder {
        public PngBitmapDecoder(Uri uri, BitmapCreateOptions options, BitmapCacheOption cache) { }
        public PngBitmapDecoder(System.IO.Stream stream, BitmapCreateOptions options, BitmapCacheOption cache) { }
    }

    public class BitmapEncoder {
        public System.Collections.Generic.IList<BitmapFrame> Frames { get; set; } = new System.Collections.Generic.List<BitmapFrame>();
        public void Save(System.IO.Stream stream) { }
    }
    public class TiffBitmapEncoder : BitmapEncoder {
        public object Compression { get; set; }
    }
    public class PngBitmapEncoder : BitmapEncoder { }
    public class JpegBitmapEncoder : BitmapEncoder {
        public int QualityLevel { get; set; }
    }

    public enum TiffCompressOption { None = 0, Lzw = 5, Zip = 8 }
}

namespace System.Windows.Media.Media3D {
    public struct Vector3D {
        public double X { get; set; }
        public double Y { get; set; }
        public double Z { get; set; }

        public Vector3D(double x, double y, double z) {
            X = x; Y = y; Z = z;
        }

        public static Vector3D CrossProduct(Vector3D v1, Vector3D v2) =>
            new Vector3D(v1.Y * v2.Z - v1.Z * v2.Y, v1.Z * v2.X - v1.X * v2.Z, v1.X * v2.Y - v1.Y * v2.X);

        public static double DotProduct(Vector3D v1, Vector3D v2) =>
            v1.X * v2.X + v1.Y * v2.Y + v1.Z * v2.Z;

        public double Length => Math.Sqrt(X * X + Y * Y + Z * Z);

        public void Normalize() {
            var len = Length;
            if (len > 0) { X /= len; Y /= len; Z /= len; }
        }
    }
}

namespace System.Windows {
    public struct Point {
        public double X { get; set; }
        public double Y { get; set; }

        public Point(double x, double y) {
            X = x; Y = y;
        }
    }

    public struct Int32Rect {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public Int32Rect(int x, int y, int width, int height) { X = x; Y = y; Width = width; Height = height; }
        public static Int32Rect Empty => new Int32Rect(0, 0, 0, 0);
    }

    public struct Vector {
        public double X { get; set; }
        public double Y { get; set; }

        public Vector(double x, double y) {
            X = x; Y = y;
        }

        public double Length => Math.Sqrt(X * X + Y * Y);
    }

    public class ResourceDictionary : System.Collections.Generic.Dictionary<object, object> { }

    public class Application {
        public static Application Current { get; } = new Application();
        public Threading.Dispatcher Dispatcher => Threading.Dispatcher.CurrentDispatcher;
        public ResourceDictionary Resources { get; } = new ResourceDictionary();
    }
}

namespace System.Windows.Threading {
    public enum DispatcherPriority { Normal = 9, Background = 4, Render = 7 }

    public class Dispatcher {
        public static Dispatcher CurrentDispatcher => new Dispatcher();
        public void Invoke(Action action) => action();
        public void Invoke(Delegate method, params object[] args) => method?.DynamicInvoke(args);
        public DispatcherOperation BeginInvoke(DispatcherPriority priority, Delegate method) { method?.DynamicInvoke(); return new DispatcherOperation(); }
        public System.Threading.Tasks.Task InvokeAsync(Action action) { action(); return System.Threading.Tasks.Task.CompletedTask; }
    }

    public class DispatcherOperation {
        public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => System.Threading.Tasks.Task.CompletedTask.GetAwaiter();
    }

    public class DispatcherObject {
    }
}

namespace NINA.Core.Utility.ColorSchema {
    public class ColorSchema : BaseINPC {
        public string Name { get; set; } = string.Empty;
        public System.Windows.Media.Color PrimaryColor { get; set; }
        public System.Windows.Media.Color SecondaryColor { get; set; }
        public System.Windows.Media.Color BorderColor { get; set; }
        public System.Windows.Media.Color BackgroundColor { get; set; }
        public System.Windows.Media.Color SecondaryBackgroundColor { get; set; }
        public System.Windows.Media.Color TertiaryBackgroundColor { get; set; }
        public System.Windows.Media.Color ButtonBackgroundColor { get; set; }
        public System.Windows.Media.Color ButtonBackgroundSelectedColor { get; set; }
        public System.Windows.Media.Color ButtonForegroundColor { get; set; }
        public System.Windows.Media.Color ButtonForegroundDisabledColor { get; set; }
        public System.Windows.Media.Color CrosshairColor { get; set; }
        public System.Windows.Media.Color NotificationWarningColor { get; set; }
        public System.Windows.Media.Color NotificationWarningTextColor { get; set; }
        public System.Windows.Media.Color NotificationErrorColor { get; set; }
        public System.Windows.Media.Color NotificationErrorTextColor { get; set; }
        public System.Windows.Media.Color SequencerExpressionTextColor { get; set; }
    }

    public class ColorSchemas : BaseINPC {
        public System.Collections.Generic.List<ColorSchema> Items { get; set; } = new System.Collections.Generic.List<ColorSchema>();
        public ColorSchema ColorSchema { get; set; } = new ColorSchema();
        public ColorSchema AltColorSchema { get; set; } = new ColorSchema();
        public ColorSchema SecondaryColor { get; set; } = new ColorSchema();

        public static ColorSchemas ReadColorSchemas() {
            var schemas = new ColorSchemas();
            schemas.Items.Add(new ColorSchema { Name = "Persian Faint" });
            schemas.Items.Add(new ColorSchema { Name = "Dark" });
            schemas.Items.Add(new ColorSchema { Name = "Custom" });
            schemas.Items.Add(new ColorSchema { Name = "Alternative Custom" });
            return schemas;
        }
    }
}

namespace NINA.Core.Model {
    // Stub for IImageGeometryProvider on non-Windows
    public interface IImageGeometryProvider {
    }
}


// ── WindowService stubs ──
namespace NINA.Core.Utility.WindowService {
    public interface IWindowService {
        void Show(object content, string title = "", System.Windows.ResizeMode resizeMode = default, System.Windows.WindowStyle windowStyle = default);
        IDispatcherOperationWrapper ShowDialog(object content, string title = "", System.Windows.ResizeMode resizeMode = default, System.Windows.WindowStyle windowStyle = default, System.Windows.Input.ICommand closeCommand = null);
        event System.EventHandler OnDialogResultChanged;
        event System.EventHandler OnClosed;
        void DelayedClose(System.TimeSpan t);
        System.Threading.Tasks.Task Close();
    }

    public interface IWindowServiceFactory {
        IWindowService Create();
    }

    public interface IDispatcherOperationWrapper {
        System.Threading.Tasks.Task Task { get; }
        object Result { get; }
        System.Runtime.CompilerServices.TaskAwaiter GetAwaiter();
    }

    public class WindowServiceFactory : IWindowServiceFactory {
        public IWindowService Create() => new WindowService();
    }

    public class WindowService : IWindowService {
        public void Show(object content, string title = "", System.Windows.ResizeMode resizeMode = default, System.Windows.WindowStyle windowStyle = default) { }
        public IDispatcherOperationWrapper ShowDialog(object content, string title = "", System.Windows.ResizeMode resizeMode = default, System.Windows.WindowStyle windowStyle = default, System.Windows.Input.ICommand closeCommand = null) {
            return new DispatcherOperationWrapper();
        }
        public void DelayedClose(System.TimeSpan t) { }
        public System.Threading.Tasks.Task Close() => System.Threading.Tasks.Task.CompletedTask;
        public event System.EventHandler OnDialogResultChanged;
        public event System.EventHandler OnClosed;
    }

    public class DispatcherOperationWrapper : IDispatcherOperationWrapper {
        public System.Threading.Tasks.Task Task => System.Threading.Tasks.Task.CompletedTask;
        public object Result => null;
        public System.Runtime.CompilerServices.TaskAwaiter GetAwaiter() => System.Threading.Tasks.Task.CompletedTask.GetAwaiter();
    }

    public class DialogResultEventArgs : System.EventArgs {
        public DialogResultEventArgs(bool? dialogResult) { DialogResult = dialogResult; }
        public bool? DialogResult { get; set; }
    }
}

// ── MyMessageBox stub ──
namespace NINA.Core.MyMessageBox {
    public class MyMessageBox {
        public static System.Windows.MessageBoxResult Show(string messageBoxText) {
            NINA.Core.Utility.Logger.Info($"MessageBox: {messageBoxText}");
            return System.Windows.MessageBoxResult.OK;
        }
        public static System.Windows.MessageBoxResult Show(string messageBoxText, string caption) {
            NINA.Core.Utility.Logger.Info($"MessageBox [{caption}]: {messageBoxText}");
            return System.Windows.MessageBoxResult.OK;
        }
        public static System.Windows.MessageBoxResult Show(string messageBoxText, string caption, System.Windows.MessageBoxButton button, System.Windows.MessageBoxResult defaultresult) {
            NINA.Core.Utility.Logger.Info($"MessageBox [{caption}]: {messageBoxText}");
            return defaultresult;
        }
    }
}

// ── WPF enums and types used by WindowService/MyMessageBox ──
namespace System.Windows {
    public enum ResizeMode { NoResize = 0, CanMinimize = 1, CanResize = 2, CanResizeWithGrip = 3 }
    public enum WindowStyle { None = 0, SingleBorderWindow = 1, ThreeDBorderWindow = 2, ToolWindow = 3 }
    public enum Visibility { Visible = 0, Hidden = 1, Collapsed = 2 }
    public enum MessageBoxButton { OK = 0, OKCancel = 1, YesNoCancel = 3, YesNo = 4 }
    public enum MessageBoxResult { None = 0, OK = 1, Cancel = 2, Yes = 6, No = 7 }
}

// ── ImageSource stub ──
namespace System.Windows.Media {
    public abstract class ImageSource {
    }

    public class GeometryGroup {
    }
}

// ── IValueConverter stub ──
namespace System.Windows.Data {
    public class BindingExpression {
        public object ResolvedSource { get; set; }
    }

    public interface IValueConverter {
        object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture);
        object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture);
    }
}

// ── Accessibility stub (used by EFWdll.cs) ──
namespace Accessibility {
    // Stub to satisfy 'using Accessibility;' in EFWdll.cs
    internal static class AccessibilityStub { }
}


// ── DispatcherOperationStatus stub ──
namespace System.Windows.Threading {
    public enum DispatcherOperationStatus { Pending = 0, Aborted = 1, Completed = 2, Executing = 3 }
}


// ── Microsoft.Win32 dialog stubs ──
namespace Microsoft.Win32 {
    public class OpenFileDialog {
        public string FileName { get; set; } = string.Empty;
        public string InitialDirectory { get; set; } = string.Empty;
        public string Filter { get; set; } = string.Empty;
        public bool? ShowDialog() => false;
    }

    public class OpenFolderDialog {
        public string FolderName { get; set; } = string.Empty;
        public string InitialDirectory { get; set; } = string.Empty;
        public bool? ShowDialog() => false;
    }
}

// ── CoreUtil.GetFilteredFileDialog stub ──
namespace NINA.Core.Utility {
    public static partial class CoreUtil {
        public static Microsoft.Win32.OpenFileDialog GetFilteredFileDialog(string path, string filename, string filter) {
            return new Microsoft.Win32.OpenFileDialog {
                InitialDirectory = System.IO.Path.GetDirectoryName(path) ?? string.Empty,
                FileName = filename,
                Filter = filter
            };
        }
    }
}


// ── HttpDownloadImageRequest stub ──
namespace NINA.Core.Utility.Http {
    public class HttpDownloadImageRequest {
        private readonly string _url;
        public object[] Parameters { get; }
        public HttpDownloadImageRequest(string url, params object[] parameters) {
            _url = url;
            Parameters = parameters;
        }
        public System.Threading.Tasks.Task<System.Windows.Media.Imaging.BitmapSource> Request(System.Threading.CancellationToken ct, System.IProgress<int> progress = null) {
            return System.Threading.Tasks.Task.FromResult<System.Windows.Media.Imaging.BitmapSource>(new System.Windows.Media.Imaging.BitmapSource());
        }
    }
}


// ── System.Windows.Controls stubs ──
namespace System.Windows.Controls {
    public class ValidationResult {
        public static ValidationResult ValidResult => new ValidationResult(true, null);
        public bool IsValid { get; }
        public object ErrorContent { get; }
        public ValidationResult(bool isValid, object errorContent) { IsValid = isValid; ErrorContent = errorContent; }
    }
    public abstract class ValidationRule {
        public abstract ValidationResult Validate(object value, System.Globalization.CultureInfo cultureInfo);
    }
}


namespace System.Windows.Data {
    public class CollectionViewSource {
        public object Source { get; set; }
        public System.ComponentModel.ICollectionView View => null;
        public static System.ComponentModel.ICollectionView GetDefaultView(object source) => null;
    }
    public interface IMultiValueConverter {
        object Convert(object[] values, System.Type targetType, object parameter, System.Globalization.CultureInfo culture);
        object[] ConvertBack(object value, System.Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture);
    }
}

#endif
