using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Text;
using System.Text.Json;
using Avalonia;

namespace CapriDesktop
{
    internal sealed class Spot
    {
        internal long Window;
        internal PixelRect Bounds;
        internal bool Seat;
        internal bool Solid;
        internal int Pid;
        // Its app and title, where the desk says, for sitting on one by name.
        internal string Name = "";
    }

    internal abstract class Desk
    {
        internal List<PixelRect> Screens = new List<PixelRect>();
        internal double Scale = 1;

        internal static Desk Make()
        {
            try
            {
                if (OperatingSystem.IsWindows()) return new WindowsDesk();
                if (OperatingSystem.IsMacOS()) return new MacDesk();
                if (OperatingSystem.IsLinux() && HyprDesk.Socket() != null) return new HyprDesk();
                if (KWinMouse.Here) return new KdeDesk();
                if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") == null && Environment.GetEnvironmentVariable("DISPLAY") != null) return new X11Desk();
            }
            catch (Exception) { }
            return new BareDesk();
        }

        internal abstract string Name { get; }
        internal virtual double Points { get { return 0; } }
        internal abstract List<Spot> Windows(ICollection<long> skip);
        internal abstract Spot Find(long window);
        internal abstract bool Alive(long window);
        internal virtual string Debug() { return ""; }
        // She's closing, for a desk that left something running.
        internal virtual void Close() { }
        // Where the mouse is on the whole desktop, in pixels, or null where the desktop won't say (Wayland).
        internal virtual PixelPoint? Pointer() { return null; }

        // Asks the window manager not to give this window the keyboard when it's shown.
        internal virtual void Unfocused(long window) { }

        // For her clones: drags a window a step (goal and seconds say where it's headed, for desks that glide it themselves),
        // lets go of it, and puts it somewhere for good. False means this window can't be moved from here.
        internal virtual bool Carry(long window, PixelPoint at, PixelPoint goal, double seconds) { return false; }
        internal virtual void Halt(long window) { }
        internal virtual bool Place(long window, PixelPoint at) { return Carry(window, at, at, 0); }
        // The cursor to this spot, where the desktop lets her without her virtual mouse.
        internal virtual bool Warp(PixelPoint at) { return false; }
        // While her clones are out: what the desk needs to see their mouse and Esc. Null from EscDown means it can't see Esc.
        internal virtual void Chaos(bool on) { }
        internal virtual Func<bool> EscDown { get { return null; } }
        // Her keys, like Windows': 0 talk (Ctrl + Alt + V), 1 mute the mic (Ctrl + Alt + N), 2 quiet (Ctrl + Alt + P). False where the desk can't.
        internal virtual bool Keys(Action<int> pressed) { return false; }

        protected bool Fills(PixelRect bounds)
        {
            foreach (PixelRect screen in Screens) if (bounds.X <= screen.X && bounds.Y <= screen.Y && bounds.Right >= screen.Right && bounds.Bottom >= screen.Bottom) return true;
            return false;
        }

        // A window covering a whole screen, like a full-screen game or video.
        internal bool Covering(List<Spot> windows)
        {
            foreach (Spot spot in windows) if (spot.Solid && Fills(spot.Bounds)) return true;
            return false;
        }

        protected Spot Make(long window, PixelRect bounds, bool seat)
        {
            bool big = bounds.Width >= 120 && bounds.Height >= 60;
            return new Spot { Window = window, Bounds = bounds, Seat = seat && big && !Fills(bounds), Solid = big };
        }
    }

    internal sealed class BareDesk : Desk
    {
        internal override string Name { get { return "screen edges only"; } }
        internal override List<Spot> Windows(ICollection<long> skip) { return new List<Spot>(); }
        internal override Spot Find(long window) { return null; }
        internal override bool Alive(long window) { return false; }
    }

    internal sealed class WindowsDesk : Desk
    {
        private delegate bool Visit(IntPtr window, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] private struct Box { internal int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool EnumWindows(Visit visit, IntPtr data);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsZoomed(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Box box);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Box value, int size);
        [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);
        private static readonly string[] Shell = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "#32768", "tooltips_class32" };

        internal override string Name { get { return "Windows"; } }

        internal override List<Spot> Windows(ICollection<long> skip)
        {
            List<Spot> found = new List<Spot>();
            Visit visit = delegate(IntPtr window, IntPtr data)
            {
                if (skip.Contains(window.ToInt64())) return true;
                Spot spot = Look(window);
                if (spot != null) found.Add(spot);
                return true;
            };
            EnumWindows(visit, IntPtr.Zero);
            GC.KeepAlive(visit);
            return found;
        }

        internal override Spot Find(long window) { return Look(new IntPtr(window)); }

        internal override bool Alive(long window) { return IsWindow(new IntPtr(window)); }

        private Spot Look(IntPtr window)
        {
            if (!IsWindow(window) || !IsWindowVisible(window) || IsIconic(window)) return null;
            int cloaked;
            if (DwmGetWindowAttribute(window, 14, out cloaked, 4) == 0 && cloaked != 0) return null;
            int extended = GetWindowLong(window, -20);
            if ((extended & 0x20) != 0 || (extended & 0x08080000) == 0x08080000) return null;
            StringBuilder kind = new StringBuilder(128);
            GetClassName(window, kind, kind.Capacity);
            if (Array.IndexOf(Shell, kind.ToString()) >= 0) return null;
            Box box;
            if (DwmGetWindowAttribute(window, 9, out box, 16) != 0 && !GetWindowRect(window, out box)) return null;
            PixelRect bounds = new PixelRect(box.Left, box.Top, box.Right - box.Left, box.Bottom - box.Top);
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;
            bool tool = (extended & 0x80) != 0 && (extended & 0x40000) == 0;
            int style = GetWindowLong(window, -16);
            Spot spot = Make(window.ToInt64(), bounds, !tool && !IsZoomed(window));
            spot.Solid = spot.Solid && !tool && (style & 0x00C00000) == 0x00C00000;
            return spot;
        }
    }

    internal sealed class MacDesk : Desk
    {
        private const string Graphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
        private const string Foundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        private const uint OnScreenOnly = 1;
        private const uint IncludingWindow = 8;
        private const uint ExcludeDesktop = 16;
        [StructLayout(LayoutKind.Sequential)] private struct Area { internal double X, Y, Width, Height; }
        [DllImport(Graphics)] private static extern IntPtr CGWindowListCopyWindowInfo(uint option, uint relativeTo);
        [DllImport(Graphics)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CGRectMakeWithDictionaryRepresentation(IntPtr dictionary, out Area rect);
        [DllImport(Graphics)] private static extern uint CGMainDisplayID();
        [DllImport(Graphics)] private static extern Area CGDisplayBounds(uint display);
        [DllImport(Foundation)] private static extern long CFArrayGetCount(IntPtr array);
        [DllImport(Foundation)] private static extern IntPtr CFArrayGetValueAtIndex(IntPtr array, long index);
        [DllImport(Foundation)] private static extern IntPtr CFDictionaryGetValue(IntPtr dictionary, IntPtr key);
        [DllImport(Foundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, long type, out long value);
        [DllImport(Foundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(IntPtr number, long type, out double value);
        [DllImport(Foundation)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(IntPtr boolean);
        [DllImport(Foundation)] private static extern void CFRelease(IntPtr value);
        [StructLayout(LayoutKind.Sequential)] private struct Place { internal double X, Y; }
        [DllImport(Graphics)] private static extern IntPtr CGEventCreate(IntPtr source);
        [DllImport(Graphics)] private static extern Place CGEventGetLocation(IntPtr e);
        private readonly IntPtr number;
        private readonly IntPtr layer;
        private readonly IntPtr frame;
        private readonly IntPtr owner;
        private readonly IntPtr alpha;
        private readonly IntPtr onscreen;
        private readonly long self = Environment.ProcessId;

        internal MacDesk()
        {
            IntPtr library = NativeLibrary.Load(Graphics);
            number = Key(library, "kCGWindowNumber");
            layer = Key(library, "kCGWindowLayer");
            frame = Key(library, "kCGWindowBounds");
            owner = Key(library, "kCGWindowOwnerPID");
            alpha = Key(library, "kCGWindowAlpha");
            onscreen = Key(library, "kCGWindowIsOnscreen");
        }

        private static IntPtr Key(IntPtr library, string name) { return Marshal.ReadIntPtr(NativeLibrary.GetExport(library, name)); }

        internal override string Name { get { return "macOS"; } }

        internal override double Points { get { return CGDisplayBounds(CGMainDisplayID()).Width; } }

        internal override PixelPoint? Pointer()
        {
            IntPtr e = CGEventCreate(IntPtr.Zero);
            if (e == IntPtr.Zero) return null;
            try
            {
                Place at = CGEventGetLocation(e);
                return new PixelPoint((int)Math.Round(at.X * Scale), (int)Math.Round(at.Y * Scale));
            }
            finally { CFRelease(e); }
        }

        internal override List<Spot> Windows(ICollection<long> skip) { return List(OnScreenOnly | ExcludeDesktop, 0, skip, true); }

        private const string Access = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
        private const string CoreStrings = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        [DllImport(Access)] private static extern bool AXIsProcessTrusted();
        [DllImport(Access)] private static extern IntPtr AXUIElementCreateApplication(int pid);
        [DllImport(Access)] private static extern int AXUIElementCopyAttributeValue(IntPtr element, IntPtr attribute, out IntPtr value);
        [DllImport(Access)] private static extern int AXUIElementSetAttributeValue(IntPtr element, IntPtr attribute, IntPtr value);
        [DllImport(Access)] private static extern IntPtr AXValueCreate(int type, ref Place value);
        [DllImport(Access)] private static extern bool AXValueGetValue(IntPtr value, int type, out Place result);
        [DllImport(Access)] private static extern int CGWarpMouseCursorPosition(Place at);
        [DllImport(CoreStrings)] private static extern IntPtr CFStringCreateWithCString(IntPtr allocator, string text, uint encoding);
        [DllImport(Access)] private static extern bool CGEventSourceKeyState(int source, ushort key);
        private readonly Dictionary<long, IntPtr> grips = new Dictionary<long, IntPtr>();
        private static IntPtr positionName;

        internal override Func<bool> EscDown { get { return delegate { return CGEventSourceKeyState(0, 53); }; } }

        private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
        [StructLayout(LayoutKind.Sequential)] private struct HotId { internal uint Signature, Id; }
        [StructLayout(LayoutKind.Sequential)] private struct EventKind { internal uint Class, Kind; }
        private delegate int HotHandler(IntPtr next, IntPtr evt, IntPtr user);
        [DllImport(Carbon)] private static extern IntPtr GetApplicationEventTarget();
        [DllImport(Carbon)] private static extern int InstallEventHandler(IntPtr target, HotHandler handler, uint count, EventKind[] kinds, IntPtr user, out IntPtr made);
        [DllImport(Carbon)] private static extern int RegisterEventHotKey(uint key, uint modifiers, HotId id, IntPtr target, uint options, out IntPtr made);
        [DllImport(Carbon)] private static extern int GetEventParameter(IntPtr evt, uint name, uint type, IntPtr actual, uint size, IntPtr used, out HotId value);
        private static HotHandler hotKept;

        // Mac's own global keys: Control + Option + V, N and P, the Ctrl + Alt of Windows.
        internal override bool Keys(Action<int> pressed)
        {
            const uint Keyboard = 0x6B657962, HotKeyPressed = 5, DirectId = 0x686B6964, TypeId = 0x686B6964, Signature = 0x43505249, Control = 0x1000, Option = 0x800;
            hotKept = delegate(IntPtr next, IntPtr evt, IntPtr user)
            {
                HotId id;
                if (GetEventParameter(evt, DirectId, TypeId, IntPtr.Zero, 8, IntPtr.Zero, out id) == 0) pressed((int)id.Id);
                return 0;
            };
            IntPtr target = GetApplicationEventTarget(), made;
            if (InstallEventHandler(target, hotKept, 1, new[] { new EventKind { Class = Keyboard, Kind = HotKeyPressed } }, IntPtr.Zero, out made) != 0) return false;
            uint[] keys = { 9, 45, 35 };
            for (uint i = 0; i < keys.Length; i++) RegisterEventHotKey(keys[i], Control | Option, new HotId { Signature = Signature, Id = i }, target, 0, out made);
            return true;
        }

        internal override bool Warp(PixelPoint at) { return CGWarpMouseCursorPosition(new Place { X = at.X / Scale, Y = at.Y / Scale }) == 0; }

        // Mac moves another app's window through Accessibility, which they allow once under Privacy & Security. Her window is
        // found by matching its frame among its app's windows, since Accessibility doesn't share Quartz's window numbers.
        internal override bool Carry(long window, PixelPoint at, PixelPoint goal, double seconds)
        {
            if (!AXIsProcessTrusted()) return false;
            if (positionName == IntPtr.Zero) positionName = CFStringCreateWithCString(IntPtr.Zero, "AXPosition", 0x08000100);
            IntPtr grip;
            if (!grips.TryGetValue(window, out grip) && (grip = Grip(window)) == IntPtr.Zero) return false;
            grips[window] = grip;
            Place place = new Place { X = at.X / Scale, Y = at.Y / Scale };
            IntPtr value = AXValueCreate(1, ref place);
            if (value == IntPtr.Zero) return false;
            try { return AXUIElementSetAttributeValue(grip, positionName, value) == 0; }
            finally { CFRelease(value); }
        }

        internal override void Halt(long window) { }

        private IntPtr Grip(long window)
        {
            Spot spot = Find(window);
            if (spot == null || spot.Pid == 0) return IntPtr.Zero;
            IntPtr app = AXUIElementCreateApplication(spot.Pid), list;
            if (app == IntPtr.Zero) return IntPtr.Zero;
            IntPtr windowsName = CFStringCreateWithCString(IntPtr.Zero, "AXWindows", 0x08000100);
            try
            {
                if (AXUIElementCopyAttributeValue(app, windowsName, out list) != 0 || list == IntPtr.Zero) return IntPtr.Zero;
                long count = CFArrayGetCount(list);
                if (positionName == IntPtr.Zero) positionName = CFStringCreateWithCString(IntPtr.Zero, "AXPosition", 0x08000100);
                for (long i = 0; i < count; i++)
                {
                    IntPtr candidate = CFArrayGetValueAtIndex(list, i), where;
                    if (AXUIElementCopyAttributeValue(candidate, positionName, out where) != 0 || where == IntPtr.Zero) continue;
                    Place place;
                    bool read = AXValueGetValue(where, 1, out place);
                    CFRelease(where);
                    if (read && Math.Abs(place.X * Scale - spot.Bounds.X) < 2 && Math.Abs(place.Y * Scale - spot.Bounds.Y) < 2) return candidate;
                }
                return IntPtr.Zero;
            }
            finally { CFRelease(windowsName); CFRelease(app); }
        }

        internal override Spot Find(long window)
        {
            foreach (Spot spot in List(IncludingWindow, (uint)window, new long[0], true)) if (spot.Window == window) return spot;
            return null;
        }

        internal override bool Alive(long window)
        {
            foreach (Spot spot in List(IncludingWindow, (uint)window, new long[0], false)) if (spot.Window == window) return true;
            return false;
        }

        private List<Spot> List(uint option, uint relative, ICollection<long> skip, bool showing)
        {
            List<Spot> found = new List<Spot>();
            IntPtr array = CGWindowListCopyWindowInfo(option, relative);
            if (array == IntPtr.Zero) return found;
            try
            {
                long count = CFArrayGetCount(array);
                for (long i = 0; i < count; i++)
                {
                    IntPtr info = CFArrayGetValueAtIndex(array, i);
                    long id = Whole(info, number);
                    long level = Whole(info, layer);
                    if (level < 0 || level > 3 || Whole(info, owner) == self || skip.Contains(id)) continue;
                    IntPtr shown = CFDictionaryGetValue(info, onscreen);
                    if (showing && (shown == IntPtr.Zero || !CFBooleanGetValue(shown) || Real(info, alpha) <= 0.05)) continue;
                    IntPtr box = CFDictionaryGetValue(info, frame);
                    Area area;
                    if (box == IntPtr.Zero || !CGRectMakeWithDictionaryRepresentation(box, out area)) continue;
                    PixelRect bounds = new PixelRect((int)Math.Round(area.X * Scale), (int)Math.Round(area.Y * Scale), (int)Math.Round(area.Width * Scale), (int)Math.Round(area.Height * Scale));
                    if (bounds.Width <= 0 || bounds.Height <= 0) continue;
                    Spot made = Make(id, bounds, true);
                    made.Pid = (int)Whole(info, owner);
                    found.Add(made);
                }
            }
            finally { CFRelease(array); }
            return found;
        }

        internal override string Debug()
        {
            List<string> parts = new List<string>();
            IntPtr array = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktop, 0);
            if (array == IntPtr.Zero) return "no window info";
            try
            {
                long count = CFArrayGetCount(array);
                for (long i = 0; i < count && parts.Count < 12; i++)
                {
                    IntPtr info = CFArrayGetValueAtIndex(array, i);
                    IntPtr box = CFDictionaryGetValue(info, frame);
                    Area area = new Area();
                    if (box != IntPtr.Zero) CGRectMakeWithDictionaryRepresentation(box, out area);
                    parts.Add("#" + Whole(info, number) + " layer " + Whole(info, layer) + " pid " + Whole(info, owner) + " alpha " + Real(info, alpha).ToString("0.##") + " " + area.X + "," + area.Y + " " + area.Width + "x" + area.Height);
                }
                return count + " raw (self " + self + "): " + string.Join("; ", parts);
            }
            finally { CFRelease(array); }
        }

        private static long Whole(IntPtr info, IntPtr key)
        {
            IntPtr value = CFDictionaryGetValue(info, key);
            long result;
            return value != IntPtr.Zero && CFNumberGetValue(value, 4, out result) ? result : -1;
        }

        private static double Real(IntPtr info, IntPtr key)
        {
            IntPtr value = CFDictionaryGetValue(info, key);
            double result;
            return value != IntPtr.Zero && CFNumberGetValue(value, 6, out result) ? result : 1;
        }
    }

    internal sealed class X11Desk : Desk
    {
        private const string Library = "libX11.so.6";
        private delegate int Handler(IntPtr display, IntPtr error);
        [DllImport(Library)] private static extern IntPtr XOpenDisplay(IntPtr name);
        [DllImport(Library)] private static extern IntPtr XDefaultRootWindow(IntPtr display);
        [DllImport(Library)] private static extern IntPtr XInternAtom(IntPtr display, string name, bool onlyIfExists);
        [DllImport(Library)] private static extern int XGetWindowProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr offset, IntPtr length, bool delete, IntPtr type, out IntPtr actualType, out int actualFormat, out IntPtr items, out IntPtr after, out IntPtr data);
        [DllImport(Library)] private static extern int XFree(IntPtr data);
        [DllImport(Library)] private static extern int XGetGeometry(IntPtr display, IntPtr drawable, out IntPtr root, out int x, out int y, out uint width, out uint height, out uint border, out uint depth);
        [DllImport(Library)] private static extern bool XTranslateCoordinates(IntPtr display, IntPtr from, IntPtr to, int x, int y, out int toX, out int toY, out IntPtr child);
        [DllImport(Library)] private static extern IntPtr XSetErrorHandler(IntPtr handler);
        [DllImport(Library)] private static extern int XSync(IntPtr display, bool discard);
        [DllImport(Library)] private static extern bool XQueryPointer(IntPtr display, IntPtr window, out IntPtr root, out IntPtr child, out int rootX, out int rootY, out int x, out int y, out uint mask);
        [DllImport(Library)] private static extern int XChangeProperty(IntPtr display, IntPtr window, IntPtr property, IntPtr type, int format, int mode, IntPtr[] data, int elements);
        [DllImport(Library)] private static extern int XFlush(IntPtr display);
        [DllImport(Library)] private static extern int XMoveWindow(IntPtr display, IntPtr window, int x, int y);
        [DllImport(Library)] private static extern int XWarpPointer(IntPtr display, IntPtr from, IntPtr to, int fromX, int fromY, uint fromWidth, uint fromHeight, int x, int y);
        [DllImport(Library)] private static extern int XQueryKeymap(IntPtr display, byte[] keys);
        private static Handler handler;
        private static IntPtr previous;
        private static IntPtr ours;
        private readonly IntPtr display;
        private readonly IntPtr root;
        private readonly Dictionary<string, IntPtr> atoms = new Dictionary<string, IntPtr>();

        internal X11Desk()
        {
            display = XOpenDisplay(IntPtr.Zero);
            if (display == IntPtr.Zero) throw new InvalidOperationException("No X display.");
            ours = display;
            handler = delegate(IntPtr where, IntPtr error)
            {
                if (where == ours || previous == IntPtr.Zero) return 0;
                return Marshal.GetDelegateForFunctionPointer<Handler>(previous)(where, error);
            };
            previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(handler));
            root = XDefaultRootWindow(display);
        }

        internal override string Name { get { return "X11"; } }

        [DllImport(Library)] private static extern int XFetchName(IntPtr display, IntPtr window, out IntPtr name);

        private string NameOf(IntPtr window)
        {
            IntPtr name;
            if (XFetchName(display, window, out name) == 0 || name == IntPtr.Zero) return "";
            string read = Marshal.PtrToStringUTF8(name);
            XFree(name);
            return read ?? "";
        }

        [DllImport(Library)] private static extern int XGrabKey(IntPtr display, int key, uint modifiers, IntPtr window, bool ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(Library)] private static extern int XKeysymToKeycode(IntPtr display, IntPtr keysym);
        [DllImport(Library)] private static extern int XNextEvent(IntPtr display, byte[] evt);

        // On a display of its own, which does nothing but wait for her keys. Num Lock and Caps Lock count as other modifiers, so each is grabbed with them too.
        internal override bool Keys(Action<int> pressed)
        {
            IntPtr own = XOpenDisplay(IntPtr.Zero);
            if (own == IntPtr.Zero) return false;
            IntPtr top = XDefaultRootWindow(own);
            int[] codes = { XKeysymToKeycode(own, new IntPtr('v')), XKeysymToKeycode(own, new IntPtr('n')), XKeysymToKeycode(own, new IntPtr('p')) };
            foreach (int code in codes)
                foreach (uint locks in new uint[] { 0, 2, 16, 18 })
                    XGrabKey(own, code, 4u | 8u | locks, top, false, 1, 1);
            XFlush(own);
            Thread waiter = new Thread(delegate()
            {
                byte[] evt = new byte[192];
                while (true)
                {
                    XNextEvent(own, evt);
                    if (BitConverter.ToInt32(evt, 0) != 2) continue;
                    int at = Array.IndexOf(codes, BitConverter.ToInt32(evt, 84));
                    if (at >= 0) pressed(at);
                }
            });
            waiter.IsBackground = true;
            waiter.Name = "Capri keys";
            waiter.Start();
            return true;
        }

        internal override bool Carry(long window, PixelPoint at, PixelPoint goal, double seconds)
        {
            XMoveWindow(display, new IntPtr(window), at.X, at.Y);
            XFlush(display);
            return true;
        }

        internal override bool Warp(PixelPoint at)
        {
            XWarpPointer(display, IntPtr.Zero, root, 0, 0, 0, 0, at.X, at.Y);
            XFlush(display);
            return true;
        }

        // X11 shares the keyboard's state with anyone who asks. Esc is key 9 on every layout, and asked on a display of its own
        // because this one belongs to her window thread.
        internal override Func<bool> EscDown
        {
            get
            {
                IntPtr own = XOpenDisplay(IntPtr.Zero);
                if (own == IntPtr.Zero) return null;
                byte[] keys = new byte[32];
                return delegate { XQueryKeymap(own, keys); return (keys[1] & 2) != 0; };
            }
        }

        internal override PixelPoint? Pointer()
        {
            IntPtr top;
            IntPtr child;
            int x;
            int y;
            int inX;
            int inY;
            uint mask;
            return XQueryPointer(display, root, out top, out child, out x, out y, out inX, out inY, out mask) ? new PixelPoint(x, y) : (PixelPoint?)null;
        }

        // Under EWMH a user time of 0 means "don't focus me when I'm mapped". 6 is XA_CARDINAL; 0 replaces the property.
        internal override void Unfocused(long window)
        {
            XChangeProperty(display, new IntPtr(window), Atom("_NET_WM_USER_TIME"), new IntPtr(6), 32, 0, new[] { IntPtr.Zero }, 1);
            XFlush(display);
        }

        private IntPtr Atom(string name)
        {
            IntPtr atom;
            if (!atoms.TryGetValue(name, out atom)) { atom = XInternAtom(display, name, false); atoms[name] = atom; }
            return atom;
        }

        private long[] Property(IntPtr window, string name)
        {
            IntPtr type;
            int format;
            IntPtr items;
            IntPtr after;
            IntPtr data;
            if (XGetWindowProperty(display, window, Atom(name), IntPtr.Zero, new IntPtr(4096), false, IntPtr.Zero, out type, out format, out items, out after, out data) != 0 || data == IntPtr.Zero) return null;
            try
            {
                if (format != 32) return null;
                long[] values = new long[items.ToInt64()];
                for (int i = 0; i < values.Length; i++) values[i] = Marshal.ReadIntPtr(data, i * IntPtr.Size).ToInt64();
                return values;
            }
            finally { XFree(data); }
        }

        internal override List<Spot> Windows(ICollection<long> skip)
        {
            List<Spot> found = new List<Spot>();
            long[] stack = Property(root, "_NET_CLIENT_LIST_STACKING");
            if (stack == null) return found;
            long[] current = Property(root, "_NET_CURRENT_DESKTOP");
            for (int i = stack.Length - 1; i >= 0; i--)
            {
                if (skip.Contains(stack[i])) continue;
                Spot spot = Look(new IntPtr(stack[i]), current != null && current.Length > 0 ? current[0] : -1);
                if (spot != null) found.Add(spot);
            }
            return found;
        }

        internal override Spot Find(long window)
        {
            long[] clients = Property(root, "_NET_CLIENT_LIST");
            if (clients != null && Array.IndexOf(clients, window) < 0) return null;
            long[] current = Property(root, "_NET_CURRENT_DESKTOP");
            return Look(new IntPtr(window), current != null && current.Length > 0 ? current[0] : -1);
        }

        internal override bool Alive(long window)
        {
            IntPtr parent;
            int x;
            int y;
            uint width;
            uint height;
            uint border;
            uint depth;
            return XGetGeometry(display, new IntPtr(window), out parent, out x, out y, out width, out height, out border, out depth) != 0;
        }

        private Spot Look(IntPtr window, long desktop)
        {
            long[] where = Property(window, "_NET_WM_DESKTOP");
            if (desktop >= 0 && where != null && where.Length > 0 && where[0] != desktop && (where[0] & 0xFFFFFFFF) != 0xFFFFFFFF) return null;
            long[] kinds = Property(window, "_NET_WM_WINDOW_TYPE");
            if (kinds != null)
                foreach (long kind in kinds)
                    if (kind != Atom("_NET_WM_WINDOW_TYPE_NORMAL").ToInt64() && kind != Atom("_NET_WM_WINDOW_TYPE_DIALOG").ToInt64()) return null;
            bool seat = true;
            long[] states = Property(window, "_NET_WM_STATE");
            if (states != null)
            {
                bool tall = false;
                bool wide = false;
                foreach (long state in states)
                {
                    if (state == Atom("_NET_WM_STATE_HIDDEN").ToInt64()) return null;
                    if (state == Atom("_NET_WM_STATE_FULLSCREEN").ToInt64()) seat = false;
                    if (state == Atom("_NET_WM_STATE_MAXIMIZED_VERT").ToInt64()) tall = true;
                    if (state == Atom("_NET_WM_STATE_MAXIMIZED_HORZ").ToInt64()) wide = true;
                }
                if (tall && wide) seat = false;
            }
            IntPtr parent;
            int x;
            int y;
            uint width;
            uint height;
            uint border;
            uint depth;
            if (XGetGeometry(display, window, out parent, out x, out y, out width, out height, out border, out depth) == 0) return null;
            int left;
            int top;
            IntPtr child;
            if (!XTranslateCoordinates(display, window, root, 0, 0, out left, out top, out child)) return null;
            int right = left + (int)width;
            int bottom = top + (int)height;
            long[] frame = Property(window, "_NET_FRAME_EXTENTS");
            if (frame != null && frame.Length == 4) { left -= (int)frame[0]; right += (int)frame[1]; top -= (int)frame[2]; bottom += (int)frame[3]; }
            long[] shadow = Property(window, "_GTK_FRAME_EXTENTS");
            if (shadow != null && shadow.Length == 4) { left += (int)shadow[0]; right -= (int)shadow[1]; top += (int)shadow[2]; bottom -= (int)shadow[3]; }
            if (right <= left || bottom <= top) return null;
            Spot found = Make(window.ToInt64(), new PixelRect(left, top, right - left, bottom - top), seat);
            long[] process = Property(window, "_NET_WM_PID");
            if (process != null && process.Length > 0) found.Pid = (int)process[0];
            found.Name = NameOf(window);
            return found;
        }
    }

    // Hyprland, through its own socket: her windows float and stay on every workspace instead of tiling, she sits on the
    // windows it lists on the workspaces it shows, and she knows where the mouse is, which Wayland doesn't otherwise say.
    internal sealed class HyprDesk : Desk
    {
        private readonly string socket = Socket();
        private readonly int own = Environment.ProcessId;
        private readonly HashSet<string> tamed = new HashSet<string>();
        private readonly System.Threading.Timer taming;
        private double scale = 1;

        // Her bubble, typing box and pill show up after she does, so every second any new window of hers gets tamed too.
        internal HyprDesk()
        {
            taming = new System.Threading.Timer(delegate
            {
                try { foreach (JsonElement client in Json("j/clients").EnumerateArray()) if (client.GetProperty("pid").GetInt32() == own) Tame(client); }
                catch (Exception) { }
            }, null, 500, 1000);
        }

        internal static string Socket()
        {
            string signature = Environment.GetEnvironmentVariable("HYPRLAND_INSTANCE_SIGNATURE");
            if (string.IsNullOrEmpty(signature) || signature.IndexOfAny(new[] { '/', '\\' }) >= 0) return null;
            string runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            foreach (string folder in new[] { runtime == null ? null : Path.Combine(runtime, "hypr"), "/tmp/hypr" })
            {
                if (folder == null) continue;
                string path = Path.Combine(folder, signature, ".socket.sock");
                if (File.Exists(path)) return path;
            }
            return null;
        }

        internal override string Name { get { return "Hyprland"; } }

        // Only windows that already float: moving a tile would swap it around their layout, and they couldn't get that back.
        private readonly HashSet<long> floating = new HashSet<long>();

        internal override bool Carry(long window, PixelPoint at, PixelPoint goal, double seconds)
        {
            lock (floating) if (!floating.Contains(window)) return false;
            Ask("dispatch movewindowpixel exact " + (int)Math.Round(at.X / scale) + " " + (int)Math.Round(at.Y / scale) + ",address:0x" + window.ToString("x"));
            return true;
        }

        internal override bool Keys(Action<int> pressed)
        {
            string[] letters = { "V", "N", "P" };
            string[] files = new string[3];
            for (int i = 0; i < 3; i++)
            {
                files[i] = Path.Combine(Path.GetTempPath(), "capri-key" + i + "-" + Environment.ProcessId);
                Ask("keyword bind CTRL ALT," + letters[i] + ",exec,touch " + files[i]);
            }
            Thread waiter = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(120);
                    for (int i = 0; i < 3; i++)
                        if (File.Exists(files[i])) { File.Delete(files[i]); pressed(i); }
                }
            });
            waiter.IsBackground = true;
            waiter.Name = "Capri keys";
            waiter.Start();
            return true;
        }

        private string escFile;

        // A bind that lets Esc through, and another for letting it go. Theirs stays theirs: with an Esc bind of their own she
        // doesn't add any, since taking hers off again would take theirs with it.
        internal override void Chaos(bool on)
        {
            try
            {
                if (on && escFile == null)
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(Ask("j/binds") ?? "", @"""key""\s*:\s*""Escape""", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return;
                    escFile = Path.Combine(Path.GetTempPath(), "capri-esc-" + Environment.ProcessId);
                    Ask("keyword bindn ,Escape,exec,touch " + escFile);
                    Ask("keyword bindrn ,Escape,exec,rm -f " + escFile);
                }
                else if (!on && escFile != null)
                {
                    Ask("keyword unbind ,Escape");
                    File.Delete(escFile);
                    escFile = null;
                }
            }
            catch (Exception) { }
        }

        internal override Func<bool> EscDown
        {
            get { string file = escFile; return file == null ? (Func<bool>)null : delegate { return File.Exists(file); }; }
        }

        internal override string Debug() { return "scale " + scale; }

        internal string Ask(string command)
        {
            using (Socket talk = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                talk.ReceiveTimeout = 1000;
                talk.SendTimeout = 1000;
                talk.Connect(new UnixDomainSocketEndPoint(socket));
                talk.Send(Encoding.UTF8.GetBytes(command));
                using (MemoryStream reply = new MemoryStream())
                {
                    byte[] chunk = new byte[65536];
                    int got;
                    while ((got = talk.Receive(chunk)) > 0) reply.Write(chunk, 0, got);
                    return Encoding.UTF8.GetString(reply.ToArray());
                }
            }
        }

        private JsonElement Json(string command)
        {
            using (JsonDocument parsed = JsonDocument.Parse(Ask(command))) return parsed.RootElement.Clone();
        }

        // XWayland apps, like her, see Hyprland's layout times the monitor's scale when it zeroes their scaling.
        private void Rescale(JsonElement monitors)
        {
            double found = 1;
            try
            {
                JsonElement option = Json("j/getoption xwayland:force_zero_scaling");
                if (option.TryGetProperty("int", out JsonElement zero) && zero.GetInt32() == 1)
                    foreach (JsonElement monitor in monitors.EnumerateArray()) found = Math.Max(found, monitor.GetProperty("scale").GetDouble());
            }
            catch (Exception) { }
            scale = found;
        }

        private int Px(double value) { return (int)Math.Round(value * scale); }

        internal override PixelPoint? Pointer()
        {
            try
            {
                JsonElement at = Json("j/cursorpos");
                return new PixelPoint(Px(at.GetProperty("x").GetDouble()), Px(at.GetProperty("y").GetDouble()));
            }
            catch (Exception) { return null; }
        }

        internal override List<Spot> Windows(ICollection<long> skip)
        {
            List<Spot> found = new List<Spot>();
            JsonElement monitors = Json("j/monitors");
            Rescale(monitors);
            HashSet<long> shown = new HashSet<long>();
            foreach (JsonElement monitor in monitors.EnumerateArray())
            {
                shown.Add(monitor.GetProperty("activeWorkspace").GetProperty("id").GetInt64());
                if (monitor.TryGetProperty("specialWorkspace", out JsonElement special) && special.TryGetProperty("id", out JsonElement id) && id.GetInt64() != 0) shown.Add(id.GetInt64());
            }
            List<JsonElement> clients = new List<JsonElement>();
            foreach (JsonElement client in Json("j/clients").EnumerateArray())
            {
                if (client.GetProperty("pid").GetInt32() == own) { Tame(client); continue; }
                clients.Add(client);
            }
            // Floating windows sit over tiled ones, and among each the last focused is on top.
            clients.Sort(delegate(JsonElement a, JsonElement b)
            {
                int floating = b.GetProperty("floating").GetBoolean().CompareTo(a.GetProperty("floating").GetBoolean());
                return floating != 0 ? floating : a.GetProperty("focusHistoryID").GetInt32().CompareTo(b.GetProperty("focusHistoryID").GetInt32());
            });
            foreach (JsonElement client in clients)
            {
                Spot spot = Look(client, shown);
                if (spot != null && !skip.Contains(spot.Window)) found.Add(spot);
            }
            return found;
        }

        internal override Spot Find(long window)
        {
            JsonElement monitors = Json("j/monitors");
            HashSet<long> shown = new HashSet<long>();
            foreach (JsonElement monitor in monitors.EnumerateArray()) shown.Add(monitor.GetProperty("activeWorkspace").GetProperty("id").GetInt64());
            foreach (JsonElement client in Json("j/clients").EnumerateArray())
                if (Address(client) == window) return Look(client, shown);
            return null;
        }

        internal override bool Alive(long window)
        {
            foreach (JsonElement client in Json("j/clients").EnumerateArray()) if (Address(client) == window) return true;
            return false;
        }

        private static long Address(JsonElement client)
        {
            string address = client.GetProperty("address").GetString() ?? "";
            if (address.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) address = address.Substring(2);
            long value;
            return long.TryParse(address, System.Globalization.NumberStyles.HexNumber, null, out value) ? value : 0;
        }

        private Spot Look(JsonElement client, HashSet<long> shown)
        {
            if (!client.GetProperty("mapped").GetBoolean() || client.GetProperty("hidden").GetBoolean()) return null;
            long workspace = client.GetProperty("workspace").GetProperty("id").GetInt64();
            bool pinned = client.TryGetProperty("pinned", out JsonElement pin) && pin.GetBoolean();
            if (!pinned && !shown.Contains(workspace)) return null;
            JsonElement at = client.GetProperty("at"), size = client.GetProperty("size");
            int width = Px(size[0].GetDouble()), height = Px(size[1].GetDouble());
            if (width <= 0 || height <= 0) return null;
            int full = client.TryGetProperty("fullscreen", out JsonElement screen) ? (screen.ValueKind == JsonValueKind.True ? 1 : screen.ValueKind == JsonValueKind.Number ? screen.GetInt32() : 0) : 0;
            long address = Address(client);
            string called = (client.TryGetProperty("class", out JsonElement kind) ? kind.GetString() : "") + " " + (client.TryGetProperty("title", out JsonElement title) ? title.GetString() : "");
            lock (floating) { if (client.GetProperty("floating").GetBoolean()) floating.Add(address); else floating.Remove(address); }
            Spot made = Make(address, new PixelRect(Px(at[0].GetDouble()), Px(at[1].GetDouble()), width, height), full == 0);
            made.Name = called;
            return made;
        }

        // Hyprland tiles every new window, which would make her a tile, so each of hers floats and is pinned to every workspace.
        private void Tame(JsonElement client)
        {
            string address = client.GetProperty("address").GetString();
            if (address == null) return;
            lock (tamed) if (tamed.Contains(address)) return;
            try
            {
                if (!client.GetProperty("floating").GetBoolean()) Dispatch("float", "setfloating", address);
                if (!(client.TryGetProperty("pinned", out JsonElement pin) && pin.GetBoolean())) Dispatch("pin", "pin", address);
                lock (tamed) tamed.Add(address);
            }
            catch (Exception) { }
        }

        // Hyprland 0.57 and later take Lua, like hl.dsp.window.pin({ action = "enable", window = "address:0x..." }); older ones
        // take "pin address:0x...". She tries Lua first and falls back when it isn't taken.
        private void Dispatch(string lua, string classic, string address)
        {
            string reply = Ask("dispatch hl.dsp.window." + lua + "({ action = \"enable\", window = \"address:" + address + "\" })");
            if (reply.Trim() != "ok") Ask("dispatch " + classic + " address:" + address);
        }
    }

    // KDE on Wayland: apps aren't told about other windows, but a KWin script is. A small script stays loaded in KWin and
    // prints the windows it would show to the journal, only when they've changed, and she follows the journal for its tag, so
    // asking where the windows are costs nothing while she's running. Positions are KWin's, which match hers at 100% scale.
    internal sealed class KdeDesk : Desk
    {
        private const string Script = "capri-desk";
        private readonly object gate = new object();
        private readonly int own = Environment.ProcessId;
        private readonly string tag = "CAPRIDESK" + Environment.ProcessId;
        private readonly Dictionary<string, long> ids = new Dictionary<string, long>();
        private List<Spot> latest = new List<Spot>();
        private Process follower;
        private string problem;

        internal KdeDesk()
        {
            try
            {
                string path = Path.Combine(Path.GetTempPath(), "capri-desk-" + own + ".js");
                File.WriteAllText(path, Source(tag));
                Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + Script);
                ProcessStartInfo watch = new ProcessStartInfo("journalctl") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
                foreach (string arg in new[] { "--user", "-f", "-n", "0", "-o", "cat", "--grep", tag }) watch.ArgumentList.Add(arg);
                follower = Process.Start(watch);
                follower.OutputDataReceived += delegate(object sender, DataReceivedEventArgs e) { if (e.Data != null) Read(e.Data); };
                follower.BeginOutputReadLine();
                follower.ErrorDataReceived += delegate { };
                follower.BeginErrorReadLine();
                string loaded = Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.loadScript", "string:" + path, "string:" + Script);
                System.Text.RegularExpressions.Match id = System.Text.RegularExpressions.Regex.Match(loaded ?? "", @"int32\s+(\d+)");
                if (!id.Success) { problem = "KWin wouldn't load her script."; return; }
                Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting/Script" + id.Groups[1].Value, "org.kde.kwin.Script.run");
            }
            catch (Exception error) { problem = error.Message; }
        }

        internal override string Name { get { return "KDE"; } }

        private const string ChaosScript = "capri-chaos";
        private readonly Dictionary<long, string> names = new Dictionary<long, string>();
        private readonly Dictionary<long, PixelPoint> heading = new Dictionary<long, PixelPoint>();
        private System.Collections.Concurrent.BlockingCollection<Action> errands;
        private PixelPoint? cursor;
        private long escAt;
        private bool chaos;

        private static void Load(string name, string source)
        {
            string path = Path.Combine(Path.GetTempPath(), name + "-" + Environment.ProcessId + ".js");
            File.WriteAllText(path, source);
            Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + name);
            // A script just unloaded keeps its name for a moment, and KWin answers -1 until it lets go of it.
            System.Text.RegularExpressions.Match id = null;
            for (int tries = 0; tries < 10; tries++)
            {
                string loaded = Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.loadScript", "string:" + path, "string:" + name);
                id = System.Text.RegularExpressions.Regex.Match(loaded ?? "", @"int32\s+(\d+)");
                if (id.Success) break;
                Thread.Sleep(40);
            }
            if (id.Success) Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting/Script" + id.Groups[1].Value, "org.kde.kwin.Script.run");
            try { File.Delete(path); }
            catch (IOException) { }
        }

        private static void Unload(string name) { Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + name); }

        // KWin scripts can't be talked to once they run, and a process for every frame would be too many, so a script per
        // stretch of the drag glides the window to where its hauler's headed, on its own clock.
        private void Errand(Action errand)
        {
            if (errands == null)
            {
                errands = new System.Collections.Concurrent.BlockingCollection<Action>();
                System.Collections.Concurrent.BlockingCollection<Action> queue = errands;
                Thread runner = new Thread(delegate() { foreach (Action next in queue.GetConsumingEnumerable()) { try { next(); } catch (Exception) { } } });
                runner.IsBackground = true;
                runner.Name = "Capri KWin errands";
                runner.Start();
            }
            errands.Add(errand);
        }

        internal static string GlideSource(string id, PixelPoint from, PixelPoint to, int milliseconds, bool raise)
        {
            return "var capriHeld = null, capriList = workspace.windowList();\n" +
                "for (var i = 0; i < capriList.length; i++) if (String(capriList[i].internalId) === '" + id + "') capriHeld = capriList[i];\n" +
                "var capriTimer = new QTimer(), capriFrom = Date.now();\n" +
                "function capriStep() {\n" +
                "  if (!capriHeld || capriHeld.deleted || capriHeld.fullScreen || capriHeld.move || capriHeld.resize) { capriTimer.stop(); return; }\n" +
                "  var t = Math.min(1, (Date.now() - capriFrom) / " + Math.Max(1, milliseconds) + "), g = capriHeld.frameGeometry;\n" +
                "  capriHeld.frameGeometry = { x: " + from.X + " + (" + (to.X - from.X) + ") * t, y: " + from.Y + " + (" + (to.Y - from.Y) + ") * t, width: g.width, height: g.height };\n" +
                "  if (t >= 1) capriTimer.stop();\n" +
                "}\n" +
                (raise ? "if (capriHeld) { var capriActive = workspace.activeWindow; workspace.raiseWindow(capriHeld); if (capriActive && capriActive !== capriHeld) workspace.raiseWindow(capriActive); }\n" : "") +
                "capriTimer.interval = 33;\ncapriTimer.timeout.connect(capriStep);\ncapriTimer.start();\ncapriStep();\n";
        }

        internal override bool Carry(long window, PixelPoint at, PixelPoint goal, double seconds)
        {
            string id;
            bool first;
            lock (gate)
            {
                if (!names.TryGetValue(window, out id)) return false;
                PixelPoint was;
                first = !heading.TryGetValue(window, out was);
                if (!first && was == goal) return true;
                heading[window] = goal;
            }
            string name = "capri-haul-" + window;
            string source = GlideSource(id, at, goal, (int)(seconds * 1000), first);
            Errand(delegate { Load(name, source); });
            return true;
        }

        internal override void Halt(long window)
        {
            lock (gate) if (!heading.Remove(window)) return;
            string name = "capri-haul-" + window;
            Errand(delegate { Unload(name); });
        }

        internal override bool Place(long window, PixelPoint at)
        {
            string id;
            lock (gate) { if (!names.TryGetValue(window, out id)) return false; heading.Remove(window); }
            string name = "capri-haul-" + window;
            string source = GlideSource(id, at, at, 1, false);
            Errand(delegate { Load(name, source); Thread.Sleep(120); Unload(name); });
            return true;
        }

        // While her clones are out, KWin says where the mouse is and takes Esc as a shortcut, so one press clears them: Wayland
        // won't show her the key otherwise, and KWin can't say how long it's been held.
        internal static string ChaosSource(string tag)
        {
            return "var capriWas = '';\n" +
                "function capriCursor() { var now = Math.round(workspace.cursorPos.x) + ' ' + Math.round(workspace.cursorPos.y); if (now !== capriWas) { capriWas = now; print('" + tag + "M ' + now); } }\n" +
                "registerShortcut('Capri clear clones', 'Capri: clear her clones', 'Escape', function () { print('" + tag + "E'); });\n" +
                "var capriTimer = new QTimer();\ncapriTimer.interval = 120;\ncapriTimer.timeout.connect(capriCursor);\ncapriTimer.start();\ncapriCursor();\n";
        }

        internal override void Chaos(bool on)
        {
            chaos = on;
            string source = ChaosSource(tag);
            if (on) Errand(delegate { Load(ChaosScript, source); });
            else Errand(delegate { Unload(ChaosScript); ForgetEsc(); });
            if (!on) lock (gate) cursor = null;
        }

        // Esc goes back to their apps, and nothing of hers stays in their shortcuts.
        private static void ForgetEsc() { Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.kglobalaccel", "/kglobalaccel", "org.kde.KGlobalAccel.unregister", "string:kwin", "string:Capri clear clones"); }

        internal override PixelPoint? Pointer() { lock (gate) return chaos ? cursor : null; }

        private Action<int> keyPressed;

        internal static string KeysSource(string tag)
        {
            return "registerShortcut('Capri talk', 'Capri: voice chat', 'Ctrl+Alt+V', function () { print('" + tag + "K0'); });\n" +
                "registerShortcut('Capri mute', 'Capri: mute her mic', 'Ctrl+Alt+N', function () { print('" + tag + "K1'); });\n" +
                "registerShortcut('Capri quiet', 'Capri: quiet', 'Ctrl+Alt+P', function () { print('" + tag + "K2'); });\n";
        }

        internal override bool Keys(Action<int> pressed)
        {
            keyPressed = pressed;
            string source = KeysSource(tag);
            Errand(delegate { Load("capri-keys", source); });
            return true;
        }

        internal override Func<bool> EscDown
        {
            get { return delegate { long pressed = Interlocked.Read(ref escAt); return pressed != 0 && Environment.TickCount64 - pressed < 900; }; }
        }

        internal override string Debug() { lock (gate) return problem ?? latest.Count + " windows from KWin"; }

        private static string Tool(string file, params string[] args)
        {
            ProcessStartInfo start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using (Process process = Process.Start(start))
            {
                if (process == null) return null;
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(2000);
                return output;
            }
        }

        // One line a change: id,pid,x,y,width,height,seat for each window, bottom first, with nothing printed when it's the same.
        internal static string Source(string tag)
        {
            // At the top level on purpose: a timer inside a function gets collected, and the updates stop after the first.
            return "var capriLast = null;\n" +
                "function capriDump() {\n" +
                "  var out = [], list = workspace.stackingOrder;\n" +
                "  for (var i = 0; i < list.length; i++) {\n" +
                "    var w = list[i];\n" +
                "    if ((!w.normalWindow && !w.dialog) || w.minimized || w.hidden || w.deleted) continue;\n" +
                "    if (!w.onAllDesktops && w.desktops.map(function (d) { return d.id; }).indexOf(workspace.currentDesktop.id) < 0) continue;\n" +
                "    if (w.activities.length > 0 && w.activities.indexOf(workspace.currentActivity) < 0) continue;\n" +
                "    var g = w.frameGeometry, area = workspace.clientArea(KWin.MaximizeArea, w);\n" +
                "    var seat = !w.fullScreen && !(g.width >= area.width && g.height >= area.height);\n" +
                "    out.push([w.internalId, w.pid, Math.round(g.x), Math.round(g.y), Math.round(g.width), Math.round(g.height), seat ? 1 : 0, (String(w.resourceClass) + ' ' + String(w.caption)).replace(/[;,\\n]/g, ' ')].join(','));\n" +
                "  }\n" +
                "  var now = out.join(';');\n" +
                "  if (now !== capriLast) { capriLast = now; print('" + tag + " ' + now); }\n" +
                "}\n" +
                "var capriTimer = new QTimer();\n" +
                "capriTimer.interval = 300;\n" +
                "capriTimer.timeout.connect(capriDump);\n" +
                "capriTimer.start();\n" +
                "capriDump();\n";
        }

        // Her own windows are left out, and the top window comes first, as the other desks give them.
        internal void Read(string line)
        {
            int at = line.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0 || line.Length <= at + tag.Length || " EMK".IndexOf(line[at + tag.Length]) < 0) return;
            if (line[at + tag.Length] == 'K')
            {
                Action<int> pressed = keyPressed;
                if (pressed != null && line.Length > at + tag.Length + 1 && char.IsDigit(line[at + tag.Length + 1])) pressed(line[at + tag.Length + 1] - '0');
                return;
            }
            if (line.Length > at + tag.Length && line[at + tag.Length] == 'E') { Interlocked.Exchange(ref escAt, Environment.TickCount64); return; }
            if (line.Length > at + tag.Length && line[at + tag.Length] == 'M')
            {
                string[] spot = line.Substring(at + tag.Length + 1).Trim().Split(' ');
                int mouseX, mouseY;
                if (spot.Length == 2 && int.TryParse(spot[0], out mouseX) && int.TryParse(spot[1], out mouseY)) lock (gate) cursor = new PixelPoint(mouseX, mouseY);
                return;
            }
            List<Spot> found = new List<Spot>();
            lock (gate)
            {
                foreach (string entry in line.Substring(at + tag.Length + 1).Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] part = entry.Split(',', 8);
                    int pid, x, y, wide, tall, seat;
                    if (part.Length < 7 || !int.TryParse(part[1], out pid) || !int.TryParse(part[2], out x) || !int.TryParse(part[3], out y) || !int.TryParse(part[4], out wide) || !int.TryParse(part[5], out tall) || !int.TryParse(part[6], out seat)) continue;
                    if (pid == own || wide <= 0 || tall <= 0) continue;
                    long id;
                    if (!ids.TryGetValue(part[0], out id)) { id = ids.Count + 1; ids[part[0]] = id; names[id] = part[0]; }
                    Spot made = Make(id, new PixelRect(x, y, wide, tall), seat == 1);
                    made.Pid = pid;
                    if (part.Length > 7) made.Name = part[7];
                    found.Insert(0, made);
                }
                latest = found;
            }
        }

        internal override List<Spot> Windows(ICollection<long> skip)
        {
            List<Spot> found = new List<Spot>();
            lock (gate) foreach (Spot spot in latest) if (!skip.Contains(spot.Window)) found.Add(spot);
            return found;
        }

        internal override Spot Find(long window)
        {
            lock (gate) foreach (Spot spot in latest) if (spot.Window == window) return spot;
            return null;
        }

        internal override bool Alive(long window) { return Find(window) != null; }

        internal override void Close()
        {
            try { Tool("dbus-send", "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + Script); }
            catch (Exception) { }
            try { Unload(ChaosScript); ForgetEsc(); Unload("capri-keys"); }
            catch (Exception) { }
            try { if (follower != null && !follower.HasExited) follower.Kill(); }
            catch (Exception) { }
            try { File.Delete(Path.Combine(Path.GetTempPath(), "capri-desk-" + own + ".js")); }
            catch (Exception) { }
        }
    }

    // KDE on Wayland won't tell an app where the mouse is, but KWin will tell a script. Her jobs ask it this way, only
    // around a borrowed click, since it takes a few hundred milliseconds: a one-line script prints the cursor and the
    // desktop's size to the journal under a fresh tag, and she reads it back. 0 to 1 across the desktop, or null.
    internal static class KWinMouse
    {
        internal static bool Here
        {
            get
            {
                return OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") != null
                    && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "").IndexOf("KDE", StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        internal static double[] Ask()
        {
            if (!Here) return null;
            string tag = "CAPRIPOS" + Guid.NewGuid().ToString("N").Substring(0, 12);
            string path = Path.Combine(Path.GetTempPath(), tag.ToLowerInvariant() + ".js");
            try
            {
                File.WriteAllText(path, "print(\"" + tag + " \" + workspace.cursorPos.x + \" \" + workspace.cursorPos.y + \" \" + workspace.virtualScreenSize.width + \" \" + workspace.virtualScreenSize.height);");
                string loaded = Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.loadScript", "string:" + path, "string:" + tag });
                System.Text.RegularExpressions.Match id = System.Text.RegularExpressions.Regex.Match(loaded ?? "", @"int32\s+(\d+)");
                if (!id.Success) return null;
                Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting/Script" + id.Groups[1].Value, "org.kde.kwin.Script.run" });
                double[] found = null;
                for (int i = 0; i < 12 && found == null; i++)
                {
                    System.Threading.Thread.Sleep(60);
                    string said = Music.Run("journalctl", new[] { "--user", "--since", "-10s", "-o", "cat", "--grep", tag });
                    System.Text.RegularExpressions.Match line = System.Text.RegularExpressions.Regex.Match(said ?? "", tag + @" (-?[\d.]+) (-?[\d.]+) ([\d.]+) ([\d.]+)");
                    if (!line.Success) continue;
                    System.Globalization.CultureInfo plain = System.Globalization.CultureInfo.InvariantCulture;
                    double x = double.Parse(line.Groups[1].Value, plain), y = double.Parse(line.Groups[2].Value, plain), w = double.Parse(line.Groups[3].Value, plain), h = double.Parse(line.Groups[4].Value, plain);
                    if (w > 1 && h > 1) found = new[] { x / (w - 1), y / (h - 1) };
                }
                Music.Run("dbus-send", new[] { "--session", "--print-reply", "--dest=org.kde.KWin", "/Scripting", "org.kde.kwin.Scripting.unloadScript", "string:" + tag });
                return found;
            }
            catch (Exception) { return null; }
            finally
            {
                try { File.Delete(path); }
                catch (IOException) { }
            }
        }
    }
}
