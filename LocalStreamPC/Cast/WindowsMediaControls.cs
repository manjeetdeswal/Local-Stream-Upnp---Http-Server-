using System;
using System.Runtime.InteropServices;

namespace LocalStreamPC.Cast
{
    /// <summary>
    /// Windows' System Media Transport Controls for the player window: the media keys on the
    /// keyboard and the media overlay (volume pop-up, lock screen) show the title and cover and can
    /// play, pause, skip and stop.
    ///
    /// Called through the plain WinRT ABI (combase.dll) so the app needs no Windows-only build.
    /// Every step is checked; if anything is refused the player simply works without the overlay.
    /// </summary>
    internal sealed unsafe class WindowsMediaControls : IDisposable
    {
        public enum Button { Play = 0, Pause = 1, Stop = 2, Next = 6, Previous = 7 }
        public enum Status { Closed = 0, Changing = 1, Stopped = 2, Playing = 3, Paused = 4 }
        private enum MediaType { Unknown = 0, Music = 1, Video = 2, Image = 3 }

        private static readonly Guid IidInterop = new("ddb0472d-c911-4a1f-86d9-dc3d71a95f5a");
        private static readonly Guid IidSmtc = new("99fa3ff4-1742-42a6-902e-087d41f965ec");
        private static readonly Guid IidButtonHandler = new("0557e996-7b23-5bae-aa81-ea0d671143a4"); // TypedEventHandler<SMTC, ButtonPressedEventArgs>
        private static readonly Guid IidStreamRefStatics = new("857309dc-3fbf-4e7d-986f-ef3b1a07a964");
        private static readonly Guid IidUriFactory = new("44a9796f-723e-4fdf-a218-033e75b0c084");
        private static readonly Guid IidUnknown = new("00000000-0000-0000-c000-000000000046");
        private static readonly Guid IidAgile = new("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");

        // ISystemMediaTransportControls vtable slots (IInspectable has 6).
        private const int PutPlaybackStatus = 7, GetDisplayUpdater = 8, PutIsEnabled = 11, PutIsPlayEnabled = 13, PutIsStopEnabled = 15,
                          PutIsPauseEnabled = 17, PutIsPreviousEnabled = 25, PutIsNextEnabled = 27, AddButtonPressed = 32, RemoveButtonPressed = 33;
        // ISystemMediaTransportControlsDisplayUpdater
        private const int UpdaterPutType = 7, UpdaterPutThumbnail = 11, UpdaterGetMusic = 12, UpdaterGetVideo = 13, UpdaterGetImage = 14, UpdaterClearAll = 16, UpdaterUpdate = 17;

        private IntPtr _smtc;
        private long _token;
        private IntPtr _handler;
        private GCHandle _self;
        private readonly Action<Button> _onButton;

        private WindowsMediaControls(IntPtr smtc, Action<Button> onButton)
        {
            _smtc = smtc;
            _onButton = onButton;
        }

        /// <summary>Sets up the controls for a window (Windows 10+). Null if not available.</summary>
        public static WindowsMediaControls? TryCreate(IntPtr hwnd, Action<Button> onButton, Action<string>? log = null)
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10) || hwnd == IntPtr.Zero) return null;
            IntPtr interop = IntPtr.Zero, smtc = IntPtr.Zero;
            try
            {
                interop = GetFactory("Windows.Media.SystemMediaTransportControls", IidInterop);
                Guid iid = IidSmtc;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)Slot(interop, 6))(interop, hwnd, &iid, &smtc), "GetForWindow");
                var controls = new WindowsMediaControls(smtc, onButton);
                controls.SetFlag(PutIsEnabled, true);
                controls.SetFlag(PutIsPlayEnabled, true);
                controls.SetFlag(PutIsPauseEnabled, true);
                controls.SetFlag(PutIsStopEnabled, true);
                controls.Subscribe();
                return controls;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Windows media keys unavailable ({ex.Message}).");
                if (smtc != IntPtr.Zero) Marshal.Release(smtc);
                return null;
            }
            finally
            {
                if (interop != IntPtr.Zero) Marshal.Release(interop);
            }
        }

        public void SetStatus(Status status)
        {
            if (_smtc == IntPtr.Zero) return;
            Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(_smtc, PutPlaybackStatus))(_smtc, (int)status), "PlaybackStatus");
        }

        public void SetNextPrevious(bool canPrevious, bool canNext)
        {
            if (_smtc == IntPtr.Zero) return;
            SetFlag(PutIsPreviousEnabled, canPrevious);
            SetFlag(PutIsNextEnabled, canNext);
        }

        /// <summary>Title, artist and cover (a URL Windows downloads itself, e.g. served by this app on localhost).</summary>
        public void SetInfo(string title, string? artist, string? album, bool isAudio, bool isImage, string? coverUrl)
        {
            if (_smtc == IntPtr.Zero) return;
            IntPtr updater = IntPtr.Zero, props = IntPtr.Zero;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(_smtc, GetDisplayUpdater))(_smtc, &updater), "DisplayUpdater");
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(updater, UpdaterClearAll))(updater);
                var type = isImage ? MediaType.Image : isAudio ? MediaType.Music : MediaType.Video;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int, int>)Slot(updater, UpdaterPutType))(updater, (int)type), "Type");

                int getter = type switch { MediaType.Music => UpdaterGetMusic, MediaType.Image => UpdaterGetImage, _ => UpdaterGetVideo };
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(updater, getter))(updater, &props), "Properties");
                SetString(props, 7, title); // put_Title
                if (type == MediaType.Music)
                {
                    if (!string.IsNullOrEmpty(artist)) SetString(props, 11, artist); // put_Artist
                }
                else if (!string.IsNullOrEmpty(artist))
                {
                    SetString(props, 9, artist); // put_Subtitle
                }

                if (coverUrl != null)
                {
                    try
                    {
                        IntPtr reference = StreamReferenceFromUri(coverUrl);
                        try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(updater, UpdaterPutThumbnail))(updater, reference), "Thumbnail"); }
                        finally { Marshal.Release(reference); }
                    }
                    catch { /* no cover in the overlay */ }
                }
                Check(((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(updater, UpdaterUpdate))(updater), "Update");
            }
            finally
            {
                if (props != IntPtr.Zero) Marshal.Release(props);
                if (updater != IntPtr.Zero) Marshal.Release(updater);
            }
        }

        public void Dispose()
        {
            if (_smtc == IntPtr.Zero) return;
            try
            {
                SetStatus(Status.Closed);
                if (_token != 0) ((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Slot(_smtc, RemoveButtonPressed))(_smtc, _token);
                SetFlag(PutIsEnabled, false);
            }
            catch { }
            Marshal.Release(_smtc);
            _smtc = IntPtr.Zero;
            if (_handler != IntPtr.Zero) ButtonHandler.Release(_handler);
            _handler = IntPtr.Zero;
            if (_self.IsAllocated) _self.Free();
        }

        // ------------------------------------------------------------ helpers

        private void SetFlag(int slot, bool value)
            => Check(((delegate* unmanaged[Stdcall]<IntPtr, byte, int>)Slot(_smtc, slot))(_smtc, value ? (byte)1 : (byte)0), "flag " + slot);

        private void Subscribe()
        {
            _self = GCHandle.Alloc(this);
            _handler = ButtonHandler.Create(GCHandle.ToIntPtr(_self));
            long token;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, long*, int>)Slot(_smtc, AddButtonPressed))(_smtc, _handler, &token);
            Check(hr, "ButtonPressed");
            _token = token;
        }

        internal void OnButton(int button)
        {
            if (Enum.IsDefined(typeof(Button), button)) _onButton((Button)button);
        }

        private static void SetString(IntPtr obj, int slot, string value)
        {
            IntPtr h = CreateHString(value);
            try { Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(obj, slot))(obj, h), "string " + slot); }
            finally { WindowsDeleteString(h); }
        }

        private static IntPtr StreamReferenceFromUri(string url)
        {
            IntPtr uriFactory = GetFactory("Windows.Foundation.Uri", IidUriFactory), uri = IntPtr.Zero;
            IntPtr statics = GetFactory("Windows.Storage.Streams.RandomAccessStreamReference", IidStreamRefStatics);
            IntPtr h = CreateHString(url);
            try
            {
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(uriFactory, 6))(uriFactory, h, &uri), "CreateUri");
                IntPtr reference;
                Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)Slot(statics, 7))(statics, uri, &reference), "CreateFromUri");
                return reference;
            }
            finally
            {
                WindowsDeleteString(h);
                if (uri != IntPtr.Zero) Marshal.Release(uri);
                Marshal.Release(uriFactory);
                Marshal.Release(statics);
            }
        }

        private static IntPtr GetFactory(string className, Guid iid)
        {
            IntPtr h = CreateHString(className);
            try
            {
                Check(RoGetActivationFactory(h, ref iid, out IntPtr factory), "factory " + className);
                return factory;
            }
            finally { WindowsDeleteString(h); }
        }

        private static IntPtr CreateHString(string s)
        {
            Check(WindowsCreateString(s, (uint)s.Length, out IntPtr h), "string");
            return h;
        }

        private static IntPtr Slot(IntPtr obj, int index) => (*(IntPtr**)obj)[index];

        private static void Check(int hr, string what)
        {
            if (hr < 0) throw new InvalidOperationException($"{what} failed (0x{hr:X8})");
        }

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = true, CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(string sourceString, uint length, out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        /// <summary>
        /// A tiny COM object implementing the ButtonPressed delegate (IUnknown + Invoke). It is agile,
        /// so Windows may call it from any thread.
        /// </summary>
        private static class ButtonHandler
        {
            private struct Layout
            {
                public IntPtr Vtable;
                public int RefCount;
                public IntPtr Owner; // GCHandle of the WindowsMediaControls
            }

            private static readonly IntPtr Vtable = CreateVtable();

            private static IntPtr CreateVtable()
            {
                var v = (IntPtr*)Marshal.AllocHGlobal(sizeof(IntPtr) * 4);
                v[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
                v[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
                v[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&ReleaseStub;
                v[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)&Invoke;
                return (IntPtr)v;
            }

            public static IntPtr Create(IntPtr owner)
            {
                var p = (Layout*)Marshal.AllocHGlobal(sizeof(Layout));
                p->Vtable = Vtable;
                p->RefCount = 1;
                p->Owner = owner;
                return (IntPtr)p;
            }

            public static void Release(IntPtr p) => ReleaseImpl(p);

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
            private static int QueryInterface(IntPtr self, Guid* riid, IntPtr* ppv)
            {
                if (*riid == IidUnknown || *riid == IidAgile || *riid == IidButtonHandler)
                {
                    *ppv = self;
                    System.Threading.Interlocked.Increment(ref ((Layout*)self)->RefCount);
                    return 0;
                }
                *ppv = IntPtr.Zero;
                return unchecked((int)0x80004002); // E_NOINTERFACE
            }

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
            private static uint AddRef(IntPtr self) => (uint)System.Threading.Interlocked.Increment(ref ((Layout*)self)->RefCount);

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
            private static uint ReleaseStub(IntPtr self) => ReleaseImpl(self);

            private static uint ReleaseImpl(IntPtr self)
            {
                int count = System.Threading.Interlocked.Decrement(ref ((Layout*)self)->RefCount);
                if (count == 0) Marshal.FreeHGlobal(self);
                return (uint)Math.Max(0, count);
            }

            [UnmanagedCallersOnly(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvStdcall) })]
            private static int Invoke(IntPtr self, IntPtr sender, IntPtr args)
            {
                try
                {
                    int button;
                    // ISystemMediaTransportControlsButtonPressedEventArgs.get_Button
                    if (((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Slot(args, 6))(args, &button) < 0) return 0;
                    var owner = GCHandle.FromIntPtr(((Layout*)self)->Owner);
                    if (owner.IsAllocated && owner.Target is WindowsMediaControls controls) controls.OnButton(button);
                }
                catch { }
                return 0;
            }
        }
    }
}
