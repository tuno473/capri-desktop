using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace CapriDesktop
{
    // Her mouse and keyboard on Mac: Quartz events, which macOS lets through once they allow Capri under Accessibility.
    internal static class MacFingers
    {
        private const string Quartz = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        internal const string Needs = "Using the mouse and keyboard isn't ready on this Mac yet. Allow Capri under System Settings > Privacy & Security > Accessibility, then ask again.";
        [StructLayout(LayoutKind.Sequential)] private struct Place { internal double X, Y; }
        [StructLayout(LayoutKind.Sequential)] private struct Area { internal double X, Y, Width, Height; }
        [DllImport(Quartz)] private static extern bool AXIsProcessTrusted();
        [DllImport(Quartz)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, int type, Place at, int button);
        [DllImport(Quartz)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, bool down);
        [DllImport(Quartz)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, int units, uint wheels, int first, int second, int third);
        [DllImport(Quartz)] private static extern void CGEventPost(int tap, IntPtr evt);
        [DllImport(Quartz)] private static extern void CGEventSetFlags(IntPtr evt, ulong flags);
        [DllImport(Quartz)] private static extern void CGEventSetIntegerValueField(IntPtr evt, int field, long value);
        [DllImport(Quartz)] private static extern void CGEventKeyboardSetUnicodeString(IntPtr evt, UIntPtr length, [MarshalAs(UnmanagedType.LPWStr)] string text);
        [DllImport(Quartz)] private static extern int CGGetActiveDisplayList(uint most, [Out] uint[] displays, out uint count);
        [DllImport(Quartz)] private static extern Area CGDisplayBounds(uint display);
        [DllImport(Foundation)] private static extern void CFRelease(IntPtr thing);
        private const int LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Moved = 5, LeftDragged = 6, OtherDown = 25, OtherUp = 26;
        private const int ClickCount = 1;
        private static Place now;

        internal static string Problem { get { return AXIsProcessTrusted() ? null : Needs; } }

        // A share of the whole desktop to a spot in points, which is how Quartz counts.
        private static Place Spot(double x, double y)
        {
            uint[] displays = new uint[16];
            uint count;
            double left = 0, top = 0, right = 1, bottom = 1;
            if (CGGetActiveDisplayList(16, displays, out count) == 0 && count > 0)
            {
                left = top = double.MaxValue;
                right = bottom = double.MinValue;
                for (int i = 0; i < count; i++)
                {
                    Area area = CGDisplayBounds(displays[i]);
                    left = Math.Min(left, area.X);
                    top = Math.Min(top, area.Y);
                    right = Math.Max(right, area.X + area.Width);
                    bottom = Math.Max(bottom, area.Y + area.Height);
                }
            }
            return new Place { X = left + x * (right - left - 1), Y = top + y * (bottom - top - 1) };
        }

        private static void Post(IntPtr evt)
        {
            if (evt == IntPtr.Zero) return;
            CGEventPost(0, evt);
            CFRelease(evt);
        }

        internal static void MoveTo(double x, double y)
        {
            now = Spot(x, y);
            Post(CGEventCreateMouseEvent(IntPtr.Zero, Moved, now, 0));
            Thread.Sleep(40);
        }

        internal static void Click(string button, int times)
        {
            int down = button == "right" ? RightDown : button == "middle" ? OtherDown : LeftDown, up = button == "right" ? RightUp : button == "middle" ? OtherUp : LeftUp, code = button == "right" ? 1 : button == "middle" ? 2 : 0;
            for (int i = 1; i <= Math.Max(1, times); i++)
            {
                foreach (int type in new[] { down, up })
                {
                    IntPtr evt = CGEventCreateMouseEvent(IntPtr.Zero, type, now, code);
                    if (evt != IntPtr.Zero) CGEventSetIntegerValueField(evt, ClickCount, i);
                    Post(evt);
                    Thread.Sleep(30);
                }
                Thread.Sleep(50);
            }
        }

        internal static void Drag(double fromX, double fromY, double toX, double toY)
        {
            MoveTo(fromX, fromY);
            Post(CGEventCreateMouseEvent(IntPtr.Zero, LeftDown, now, 0));
            for (int step = 1; step <= 12; step++)
            {
                Thread.Sleep(20);
                now = Spot(fromX + (toX - fromX) * step / 12, fromY + (toY - fromY) * step / 12);
                Post(CGEventCreateMouseEvent(IntPtr.Zero, LeftDragged, now, 0));
            }
            Post(CGEventCreateMouseEvent(IntPtr.Zero, LeftUp, now, 0));
        }

        internal static void Scroll(int notches)
        {
            for (int i = 0; i < Math.Abs(notches); i++)
            {
                Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1, 1, notches > 0 ? -1 : 1, 0, 0));
                Thread.Sleep(30);
            }
        }

        private static readonly Dictionary<string, ushort> Named = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase)
        {
            { "esc", 53 }, { "escape", 53 }, { "backspace", 51 }, { "tab", 48 }, { "enter", 36 }, { "return", 36 }, { "space", 49 }, { "capslock", 57 },
            { "home", 115 }, { "end", 119 }, { "pageup", 116 }, { "pagedown", 121 }, { "delete", 117 }, { "del", 117 }, { "up", 126 }, { "down", 125 }, { "left", 123 }, { "right", 124 },
            { "minus", 27 }, { "-", 27 }, { "comma", 43 }, { ",", 43 }, { "period", 47 }, { ".", 47 }, { "slash", 44 }, { "/", 44 },
            { "f1", 122 }, { "f2", 120 }, { "f3", 99 }, { "f4", 118 }, { "f5", 96 }, { "f6", 97 }, { "f7", 98 }, { "f8", 100 }, { "f9", 101 }, { "f10", 109 }, { "f11", 103 }, { "f12", 111 },
            { "a", 0 }, { "s", 1 }, { "d", 2 }, { "f", 3 }, { "h", 4 }, { "g", 5 }, { "z", 6 }, { "x", 7 }, { "c", 8 }, { "v", 9 }, { "b", 11 }, { "q", 12 }, { "w", 13 }, { "e", 14 }, { "r", 15 }, { "y", 16 }, { "t", 17 },
            { "1", 18 }, { "2", 19 }, { "3", 20 }, { "4", 21 }, { "6", 22 }, { "5", 23 }, { "9", 25 }, { "7", 26 }, { "8", 28 }, { "0", 29 }, { "o", 31 }, { "u", 32 }, { "i", 34 }, { "p", 35 }, { "l", 37 }, { "j", 38 }, { "k", 40 }, { "n", 45 }, { "m", 46 }
        };
        private static readonly Dictionary<string, ulong> Holding = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase)
        {
            { "shift", 0x20000 }, { "ctrl", 0x40000 }, { "control", 0x40000 }, { "alt", 0x80000 }, { "option", 0x80000 }, { "altgr", 0x80000 }, { "cmd", 0x100000 }, { "super", 0x100000 }, { "meta", 0x100000 }, { "win", 0x100000 }
        };

        // Her brain says ctrl+l, like everywhere else. On Mac that's cmd for the usual shortcuts, so ctrl with a letter means cmd.
        internal static bool Keys(string chord)
        {
            ulong flags = 0;
            List<ushort> keys = new List<ushort>();
            foreach (string part in (chord ?? "").ToLowerInvariant().Replace(" ", "").Split('+'))
            {
                if (part.Length == 0) continue;
                ulong held;
                ushort key;
                if (Holding.TryGetValue(part, out held)) flags |= held;
                else if (Named.TryGetValue(part, out key)) keys.Add(key);
                else return false;
            }
            if ((flags & 0x40000) != 0 && keys.Count > 0) flags = (flags & ~0x40000UL) | 0x100000;
            if (keys.Count == 0) return flags != 0;
            foreach (ushort key in keys) Press(key, true, flags);
            for (int i = keys.Count - 1; i >= 0; i--) Press(keys[i], false, flags);
            Thread.Sleep(60);
            return true;
        }

        private static void Press(ushort key, bool down, ulong flags)
        {
            IntPtr evt = CGEventCreateKeyboardEvent(IntPtr.Zero, key, down);
            if (evt != IntPtr.Zero) CGEventSetFlags(evt, flags);
            Post(evt);
            Thread.Sleep(15);
        }

        internal static bool Type(string text)
        {
            foreach (char c in text ?? "")
                foreach (bool down in new[] { true, false })
                {
                    IntPtr evt = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
                    if (evt != IntPtr.Zero) CGEventKeyboardSetUnicodeString(evt, (UIntPtr)1, c.ToString());
                    Post(evt);
                    Thread.Sleep(6);
                }
            return true;
        }
    }
}
