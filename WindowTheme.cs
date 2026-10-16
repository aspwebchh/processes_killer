using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace ProcessesKiller
{
    // Makes the native Windows title bar follow the dark app theme.
    // Unsupported attributes (older Windows) simply return an error code and are ignored.
    internal static class WindowTheme
    {
        private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaCaptionColor = 35;
        private const int DwmwaTextColor = 36;

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        internal static void Apply(Window window)
        {
            var helper = new WindowInteropHelper(window);
            if (helper.Handle != IntPtr.Zero)
            {
                ApplyToHandle(helper.Handle);
                return;
            }

            // The handle does not exist until the window is shown, so a window that is only constructed is left untouched.
            window.SourceInitialized += (sender, args) => ApplyToHandle(new WindowInteropHelper(window).Handle);
        }

        private static void ApplyToHandle(IntPtr handle)
        {
            if (handle == IntPtr.Zero) return;
            try
            {
                if (SetInt(handle, DwmwaUseImmersiveDarkMode, 1) != 0)
                {
                    SetInt(handle, DwmwaUseImmersiveDarkModeBefore20H1, 1);
                }

                // Windows 11 only: match the title bar to the app's top bar. Earlier versions ignore these.
                SetColor(handle, DwmwaBorderColor, "BorderColor");
                SetColor(handle, DwmwaCaptionColor, "TopBarColor");
                SetColor(handle, DwmwaTextColor, "TextPrimaryColor");
            }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        private static void SetColor(IntPtr handle, int attribute, string resourceKey)
        {
            if (Application.Current == null || !(Application.Current.TryFindResource(resourceKey) is Color)) return;
            SetInt(handle, attribute, ToColorRef((Color)Application.Current.TryFindResource(resourceKey)));
        }

        // COLORREF is 0x00BBGGRR.
        internal static int ToColorRef(Color color)
        {
            return color.R | (color.G << 8) | (color.B << 16);
        }

        private static int SetInt(IntPtr handle, int attribute, int value)
        {
            return DwmSetWindowAttribute(handle, attribute, ref value, sizeof(int));
        }
    }
}
